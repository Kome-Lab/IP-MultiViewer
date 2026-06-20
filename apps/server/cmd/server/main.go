package main

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"database/sql"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/example/ip-multiviewer/packages/contracts"
	notificationcore "github.com/example/ip-multiviewer/packages/notification-core"
	streamutils "github.com/example/ip-multiviewer/packages/stream-utils"
	_ "modernc.org/sqlite"
)

type memoryStore struct {
	sync.Mutex
	streams                map[string]contracts.StreamConfig
	layouts                map[string]contracts.Layout
	alertProfiles          map[string]contracts.AlertProfile
	clients                map[string]contracts.ClientRegistration
	clientHeartbeats       map[string]contracts.ClientHeartbeat
	alerts                 map[string]contracts.Alert
	notificationChannels   map[string]contracts.NotificationChannel
	notificationSecrets    map[string]string
	notificationDeliveries map[string]contracts.NotificationDelivery
	clientToken            string
	clientTokens           map[string]clientTokenRecord
	statePath              string
	db                     *sql.DB
	secretCodec            *secretCodec
	httpClient             *http.Client
	clientOfflineAfter     time.Duration
}

type clientTokenRecord struct {
	ClientID  string
	TokenHash string
	Revoked   bool
	CreatedAt time.Time
	RevokedAt time.Time
}

type persistedState struct {
	Streams                map[string]contracts.StreamConfig         `json:"streams"`
	Layouts                map[string]contracts.Layout               `json:"layouts"`
	AlertProfiles          map[string]contracts.AlertProfile         `json:"alert_profiles"`
	Clients                map[string]contracts.ClientRegistration   `json:"clients"`
	ClientHeartbeats       map[string]contracts.ClientHeartbeat      `json:"client_heartbeats"`
	Alerts                 map[string]contracts.Alert                `json:"alerts"`
	NotificationChannels   map[string]contracts.NotificationChannel  `json:"notification_channels"`
	NotificationSecrets    map[string]string                         `json:"notification_secrets"`
	NotificationDeliveries map[string]contracts.NotificationDelivery `json:"notification_deliveries"`
	ClientTokens           map[string]clientTokenRecord              `json:"client_tokens"`
}

func main() {
	addr := os.Getenv("MULTIVIEWER_BIND_ADDR")
	if addr == "" {
		addr = "127.0.0.1:8080"
	}
	db, statePath, err := persistenceFromEnv()
	if err != nil {
		log.Printf("persistence setup failed; continuing without persistence: %v", err)
	}
	if db != nil {
		defer db.Close()
	}
	secretCodec, err := secretCodecFromEnv()
	if err != nil {
		log.Printf("notification secret encryption disabled: %v", err)
	}
	store := &memoryStore{
		streams:                map[string]contracts.StreamConfig{},
		layouts:                map[string]contracts.Layout{},
		alertProfiles:          map[string]contracts.AlertProfile{},
		clients:                map[string]contracts.ClientRegistration{},
		clientHeartbeats:       map[string]contracts.ClientHeartbeat{},
		alerts:                 map[string]contracts.Alert{},
		notificationChannels:   map[string]contracts.NotificationChannel{},
		notificationSecrets:    map[string]string{},
		notificationDeliveries: map[string]contracts.NotificationDelivery{},
		clientToken:            strings.TrimSpace(os.Getenv("MULTIVIEWER_CLIENT_TOKEN")),
		clientTokens:           parseClientTokens(os.Getenv("MULTIVIEWER_CLIENT_TOKENS")),
		statePath:              statePath,
		db:                     db,
		secretCodec:            secretCodec,
		httpClient:             &http.Client{Timeout: 10 * time.Second},
		clientOfflineAfter:     durationFromEnv("MULTIVIEWER_CLIENT_OFFLINE_AFTER_SEC", 90*time.Second),
	}
	if err := store.loadSnapshot(); err != nil {
		log.Printf("failed to load server state snapshot: %v", err)
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok", "service": "ip-multiviewer-server"})
	})
	mux.HandleFunc("GET /streams", store.listStreams)
	mux.HandleFunc("POST /streams", store.createStream)
	mux.HandleFunc("GET /streams/{id}", store.getStream)
	mux.HandleFunc("PUT /streams/{id}", store.updateStream)
	mux.HandleFunc("DELETE /streams/{id}", store.deleteStream)
	mux.HandleFunc("GET /layouts", store.listLayouts)
	mux.HandleFunc("POST /layouts", store.createLayout)
	mux.HandleFunc("GET /layouts/{id}", store.getLayout)
	mux.HandleFunc("PUT /layouts/{id}", store.updateLayout)
	mux.HandleFunc("DELETE /layouts/{id}", store.deleteLayout)
	mux.HandleFunc("GET /alert-profiles", store.listAlertProfiles)
	mux.HandleFunc("POST /alert-profiles", store.createAlertProfile)
	mux.HandleFunc("GET /alert-profiles/{id}", store.getAlertProfile)
	mux.HandleFunc("PUT /alert-profiles/{id}", store.updateAlertProfile)
	mux.HandleFunc("DELETE /alert-profiles/{id}", store.deleteAlertProfile)
	mux.HandleFunc("GET /clients", store.listClients)
	mux.HandleFunc("GET /clients/{id}", store.getClient)
	mux.HandleFunc("POST /clients/register", store.registerClient)
	mux.HandleFunc("POST /clients/{id}/revoke-token", store.revokeClientToken)
	mux.HandleFunc("POST /clients/heartbeat", store.clientHeartbeat)
	mux.HandleFunc("POST /client-alerts", store.createClientAlert)
	mux.HandleFunc("GET /alerts", store.listAlerts)
	mux.HandleFunc("GET /alerts/{id}", store.getAlert)
	mux.HandleFunc("POST /alerts/{id}/acknowledge", store.setAlertStatus(contracts.AlertAcknowledged))
	mux.HandleFunc("POST /alerts/{id}/snooze", store.setAlertStatus(contracts.AlertSnoozed))
	mux.HandleFunc("POST /alerts/{id}/resolve", store.setAlertStatus(contracts.AlertResolved))
	mux.HandleFunc("GET /notification-channels", store.listNotificationChannels)
	mux.HandleFunc("POST /notification-channels", store.createNotificationChannel)
	mux.HandleFunc("PUT /notification-channels/{id}", store.updateNotificationChannel)
	mux.HandleFunc("DELETE /notification-channels/{id}", store.deleteNotificationChannel)
	mux.HandleFunc("POST /notification-channels/{id}/test", store.testNotificationChannel)
	mux.HandleFunc("GET /notification-deliveries", store.listNotificationDeliveries)
	mux.HandleFunc("GET /system/status", store.systemStatus)

	log.Printf("ip-multiviewer-server listening on %s", addr)
	log.Fatal(http.ListenAndServe(addr, secureHeaders(mux)))
}

func (s *memoryStore) listStreams(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	defer s.Unlock()
	items := make([]contracts.StreamConfig, 0, len(s.streams))
	for _, item := range s.streams {
		item.InputURL = streamutils.MaskCredentials(item.InputURL)
		items = append(items, item)
	}
	writeJSON(w, http.StatusOK, items)
}

func (s *memoryStore) createStream(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	var item contracts.StreamConfig
	if !decode(w, r, &item) {
		return
	}
	if item.ID == "" {
		item.ID = newID()
	}
	if strings.TrimSpace(item.Name) == "" {
		writeError(w, http.StatusBadRequest, "name_required")
		return
	}
	if err := streamutils.ValidateStreamURL(item.InputURL); err != nil {
		writeError(w, http.StatusBadRequest, "invalid_stream_url")
		return
	}
	now := time.Now().UTC()
	item.CreatedAt = now
	item.UpdatedAt = now
	s.Lock()
	s.streams[item.ID] = item
	s.Unlock()
	s.persistSnapshot()
	item.InputURL = streamutils.MaskCredentials(item.InputURL)
	writeJSON(w, http.StatusCreated, item)
}

func (s *memoryStore) getStream(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	item, ok := s.streams[r.PathValue("id")]
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	item.InputURL = streamutils.MaskCredentials(item.InputURL)
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) updateStream(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	id := r.PathValue("id")
	var item contracts.StreamConfig
	if !decode(w, r, &item) {
		return
	}
	if err := streamutils.ValidateStreamURL(item.InputURL); err != nil {
		writeError(w, http.StatusBadRequest, "invalid_stream_url")
		return
	}
	s.Lock()
	existing, ok := s.streams[id]
	if !ok {
		s.Unlock()
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	item.ID = id
	item.CreatedAt = existing.CreatedAt
	item.UpdatedAt = time.Now().UTC()
	s.streams[id] = item
	s.Unlock()
	s.persistSnapshot()
	item.InputURL = streamutils.MaskCredentials(item.InputURL)
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) deleteStream(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	s.Lock()
	_, ok := s.streams[r.PathValue("id")]
	delete(s.streams, r.PathValue("id"))
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, map[string]string{"status": "deleted"})
}

func (s *memoryStore) listLayouts(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	defer s.Unlock()
	items := make([]contracts.Layout, 0, len(s.layouts))
	for _, item := range s.layouts {
		items = append(items, item)
	}
	writeJSON(w, http.StatusOK, items)
}

func (s *memoryStore) createLayout(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	var item contracts.Layout
	if !decode(w, r, &item) {
		return
	}
	if item.ID == "" {
		item.ID = newID()
	}
	if item.Kind == "" {
		item.Kind = contracts.Layout2x2
	}
	now := time.Now().UTC()
	item.CreatedAt = now
	item.UpdatedAt = now
	s.Lock()
	s.layouts[item.ID] = item
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusCreated, item)
}

func (s *memoryStore) getLayout(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	item, ok := s.layouts[r.PathValue("id")]
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) updateLayout(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	id := r.PathValue("id")
	var item contracts.Layout
	if !decode(w, r, &item) {
		return
	}
	s.Lock()
	existing, ok := s.layouts[id]
	if !ok {
		s.Unlock()
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	item.ID = id
	item.CreatedAt = existing.CreatedAt
	item.UpdatedAt = time.Now().UTC()
	s.layouts[id] = item
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) deleteLayout(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	s.Lock()
	_, ok := s.layouts[r.PathValue("id")]
	delete(s.layouts, r.PathValue("id"))
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, map[string]string{"status": "deleted"})
}

func (s *memoryStore) listAlertProfiles(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	defer s.Unlock()
	items := make([]contracts.AlertProfile, 0, len(s.alertProfiles))
	for _, item := range s.alertProfiles {
		items = append(items, item)
	}
	writeJSON(w, http.StatusOK, items)
}

func (s *memoryStore) createAlertProfile(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	var item contracts.AlertProfile
	if !decode(w, r, &item) {
		return
	}
	if item.ID == "" {
		item.ID = newID()
	}
	now := time.Now().UTC()
	item.CreatedAt = now
	item.UpdatedAt = now
	s.Lock()
	s.alertProfiles[item.ID] = item
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusCreated, item)
}

func (s *memoryStore) getAlertProfile(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	item, ok := s.alertProfiles[r.PathValue("id")]
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) updateAlertProfile(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	id := r.PathValue("id")
	var item contracts.AlertProfile
	if !decode(w, r, &item) {
		return
	}
	s.Lock()
	existing, ok := s.alertProfiles[id]
	if !ok {
		s.Unlock()
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	item.ID = id
	item.CreatedAt = existing.CreatedAt
	item.UpdatedAt = time.Now().UTC()
	s.alertProfiles[id] = item
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) deleteAlertProfile(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	s.Lock()
	_, ok := s.alertProfiles[r.PathValue("id")]
	delete(s.alertProfiles, r.PathValue("id"))
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, map[string]string{"status": "deleted"})
}

func (s *memoryStore) listClients(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	statuses := s.clientStatusesLocked(time.Now().UTC())
	s.Unlock()
	sort.Slice(statuses, func(i, j int) bool {
		return statuses[i].ClientID < statuses[j].ClientID
	})
	writeJSON(w, http.StatusOK, statuses)
}

func (s *memoryStore) getClient(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	clientID := r.PathValue("id")
	s.Lock()
	status, ok := s.clientStatusLocked(clientID, time.Now().UTC())
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	writeJSON(w, http.StatusOK, status)
}

func (s *memoryStore) registerClient(w http.ResponseWriter, r *http.Request) {
	tokenHash, ok := s.authorizeWrite(w, r)
	if !ok {
		return
	}
	var item contracts.ClientRegistration
	if !decode(w, r, &item) {
		return
	}
	if item.ClientID == "" {
		item.ClientID = newID()
	}
	if !s.validateClientTokenOwner(w, tokenHash, item.ClientID) {
		return
	}
	s.Lock()
	s.clients[item.ClientID] = item
	if tokenHash != "" && !s.isBootstrapTokenHash(tokenHash) {
		record := s.clientTokens[tokenHash]
		record.ClientID = item.ClientID
		record.TokenHash = tokenHash
		record.CreatedAt = time.Now().UTC()
		s.clientTokens[tokenHash] = record
	}
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusAccepted, item)
}

func (s *memoryStore) revokeClientToken(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	clientID := r.PathValue("id")
	if strings.TrimSpace(clientID) == "" {
		writeError(w, http.StatusBadRequest, "client_id_required")
		return
	}
	revoked := 0
	now := time.Now().UTC()
	s.Lock()
	for hash, record := range s.clientTokens {
		if record.ClientID != clientID || record.Revoked {
			continue
		}
		record.Revoked = true
		record.RevokedAt = now
		s.clientTokens[hash] = record
		revoked++
	}
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, map[string]any{"status": "revoked", "client_id": clientID, "revoked_tokens": revoked})
}

func (s *memoryStore) clientHeartbeat(w http.ResponseWriter, r *http.Request) {
	tokenHash, ok := s.authorizeWrite(w, r)
	if !ok {
		return
	}
	var item contracts.ClientHeartbeat
	if !decode(w, r, &item) {
		return
	}
	if item.ClientID == "" {
		writeError(w, http.StatusBadRequest, "client_id_required")
		return
	}
	if !s.validateClientTokenOwner(w, tokenHash, item.ClientID) {
		return
	}
	if item.Timestamp.IsZero() {
		item.Timestamp = time.Now().UTC()
	}
	s.Lock()
	s.clientHeartbeats[item.ClientID] = item
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusAccepted, item)
}

func (s *memoryStore) createClientAlert(w http.ResponseWriter, r *http.Request) {
	tokenHash, ok := s.authorizeWrite(w, r)
	if !ok {
		return
	}
	var item contracts.Alert
	if !decode(w, r, &item) {
		return
	}
	if item.ID == "" {
		item.ID = newID()
	}
	if item.Status == "" {
		item.Status = contracts.AlertOpen
	}
	if !validAlertType(item.Type) {
		writeError(w, http.StatusBadRequest, "invalid_alert_type")
		return
	}
	if !validAlertSeverity(item.Severity) {
		writeError(w, http.StatusBadRequest, "invalid_alert_severity")
		return
	}
	if !validAlertStatus(item.Status) {
		writeError(w, http.StatusBadRequest, "invalid_alert_status")
		return
	}
	if item.StreamID == "" {
		writeError(w, http.StatusBadRequest, "stream_id_required")
		return
	}
	if item.SourceClientID == "" {
		item.SourceClientID = s.clientIDForTokenHash(tokenHash)
	}
	now := time.Now().UTC()
	if item.StartedAt.IsZero() {
		item.StartedAt = now
	}
	item.UpdatedAt = now
	isNewAlert := true
	shouldNotify := false
	notificationEventType := string(item.Type)
	s.Lock()
	if existing, ok := s.alerts[item.ID]; ok {
		item.StartedAt = existing.StartedAt
		if severityRank(item.Severity) > severityRank(existing.Severity) && isActiveAlertStatus(item.Status) {
			shouldNotify = true
			notificationEventType = string(item.Type) + ".updated"
		} else if severityRank(item.Severity) < severityRank(existing.Severity) {
			item.Severity = existing.Severity
		}
		s.alerts[item.ID] = item
		isNewAlert = false
	} else if existingID, existing, ok := s.findActiveAlertLocked(item.StreamID, item.Type); ok {
		item.ID = existingID
		item.StartedAt = existing.StartedAt
		if !isClientStatusTransition(item.Status) {
			item.Status = existing.Status
		}
		if severityRank(item.Severity) > severityRank(existing.Severity) && isActiveAlertStatus(item.Status) {
			shouldNotify = true
			notificationEventType = string(item.Type) + ".updated"
		} else if severityRank(item.Severity) < severityRank(existing.Severity) {
			item.Severity = existing.Severity
		}
		s.alerts[existingID] = item
		isNewAlert = false
	} else {
		s.alerts[item.ID] = item
		shouldNotify = isActiveAlertStatus(item.Status)
	}
	s.Unlock()
	s.persistSnapshot()
	if (isNewAlert || shouldNotify) && isActiveAlertStatus(item.Status) {
		s.deliverAlertNotifications(item, notificationEventType)
	}
	writeJSON(w, http.StatusAccepted, item)
}

func (s *memoryStore) listAlerts(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	defer s.Unlock()
	items := make([]contracts.Alert, 0, len(s.alerts))
	for _, item := range s.alerts {
		items = append(items, item)
	}
	writeJSON(w, http.StatusOK, items)
}

func (s *memoryStore) getAlert(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	item, ok := s.alerts[r.PathValue("id")]
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) setAlertStatus(status contracts.AlertStatus) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !s.requireWriteToken(w, r) {
			return
		}
		var snoozedUntil time.Time
		if status == contracts.AlertSnoozed && r.Body != nil && r.ContentLength != 0 {
			var body struct {
				SnoozedUntil time.Time `json:"snoozed_until"`
			}
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				writeError(w, http.StatusBadRequest, "invalid_json")
				return
			}
			snoozedUntil = body.SnoozedUntil
		}
		if status == contracts.AlertSnoozed && snoozedUntil.IsZero() {
			snoozedUntil = time.Now().UTC().Add(10 * time.Minute)
		}
		s.Lock()
		item, ok := s.alerts[r.PathValue("id")]
		if ok {
			item.Status = status
			if status == contracts.AlertSnoozed {
				item.SnoozedUntil = snoozedUntil
			} else {
				item.SnoozedUntil = time.Time{}
			}
			item.UpdatedAt = time.Now().UTC()
			s.alerts[item.ID] = item
		}
		s.Unlock()
		if !ok {
			writeError(w, http.StatusNotFound, "not_found")
			return
		}
		s.persistSnapshot()
		writeJSON(w, http.StatusOK, item)
	}
}

func (s *memoryStore) findActiveAlertLocked(streamID string, alertType contracts.AlertType) (string, contracts.Alert, bool) {
	if streamID == "" || alertType == "" {
		return "", contracts.Alert{}, false
	}
	for id, alert := range s.alerts {
		if alert.StreamID == streamID && alert.Type == alertType && isActiveAlertStatus(alert.Status) {
			return id, alert, true
		}
	}
	return "", contracts.Alert{}, false
}

func isActiveAlertStatus(status contracts.AlertStatus) bool {
	return status == contracts.AlertOpen || status == contracts.AlertAcknowledged || status == contracts.AlertSnoozed
}

func isClientStatusTransition(status contracts.AlertStatus) bool {
	return status == contracts.AlertAcknowledged ||
		status == contracts.AlertSnoozed ||
		status == contracts.AlertResolved ||
		status == contracts.AlertIgnored
}

func validAlertStatus(status contracts.AlertStatus) bool {
	return status == contracts.AlertOpen ||
		status == contracts.AlertAcknowledged ||
		status == contracts.AlertSnoozed ||
		status == contracts.AlertResolved ||
		status == contracts.AlertIgnored
}

func validAlertSeverity(severity contracts.AlertSeverity) bool {
	return severity == contracts.SeverityInfo ||
		severity == contracts.SeverityWarning ||
		severity == contracts.SeverityError ||
		severity == contracts.SeverityCritical
}

func validAlertType(alertType contracts.AlertType) bool {
	switch alertType {
	case contracts.AlertVideoBlack,
		contracts.AlertVideoFreeze,
		contracts.AlertVideoNoSignal,
		contracts.AlertVideoLowFPS,
		contracts.AlertVideoBitrateLow,
		contracts.AlertVideoDecodeError,
		contracts.AlertVideoResolutionChanged,
		contracts.AlertVideoNoiseDetected,
		contracts.AlertAudioSilence,
		contracts.AlertAudioLowLevel,
		contracts.AlertAudioHighLevel,
		contracts.AlertAudioClipping,
		contracts.AlertAudioNoiseDetected,
		contracts.AlertAudioChannelMissing,
		contracts.AlertAudioDropout,
		contracts.AlertTransportPacketLossHigh,
		contracts.AlertTransportJitterHigh,
		contracts.AlertTransportRTTHigh,
		contracts.AlertTransportReconnectLoop,
		contracts.AlertTransportInputTimeout,
		contracts.AlertTransportBitrateUnstable,
		contracts.AlertSystemCPUHigh,
		contracts.AlertSystemMemoryHigh,
		contracts.AlertSystemDiskLow:
		return true
	default:
		return false
	}
}

func severityRank(severity contracts.AlertSeverity) int {
	switch severity {
	case contracts.SeverityCritical:
		return 4
	case contracts.SeverityError:
		return 3
	case contracts.SeverityWarning:
		return 2
	case contracts.SeverityInfo:
		return 1
	default:
		return 0
	}
}

func (s *memoryStore) listNotificationChannels(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	defer s.Unlock()
	items := make([]contracts.NotificationChannel, 0, len(s.notificationChannels))
	for _, item := range s.notificationChannels {
		items = append(items, item)
	}
	writeJSON(w, http.StatusOK, items)
}

func (s *memoryStore) createNotificationChannel(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	var item contracts.NotificationChannel
	if !decode(w, r, &item) {
		return
	}
	if item.ID == "" {
		item.ID = newID()
	}
	now := time.Now().UTC()
	item.CreatedAt = now
	item.UpdatedAt = now
	secret, hasSecret, ok := s.prepareNotificationChannel(w, &item, contracts.NotificationChannel{})
	if !ok {
		return
	}
	s.Lock()
	s.notificationChannels[item.ID] = item
	if hasSecret {
		s.notificationSecrets[item.ID] = secret
	}
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusCreated, item)
}

func (s *memoryStore) updateNotificationChannel(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	id := r.PathValue("id")
	var item contracts.NotificationChannel
	if !decode(w, r, &item) {
		return
	}
	s.Lock()
	existing, ok := s.notificationChannels[id]
	if !ok {
		s.Unlock()
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	item.ID = id
	item.CreatedAt = existing.CreatedAt
	item.UpdatedAt = time.Now().UTC()
	secret, hasSecret, ok := s.prepareNotificationChannel(w, &item, existing)
	if !ok {
		return
	}
	s.notificationChannels[id] = item
	if hasSecret {
		s.notificationSecrets[id] = secret
	}
	s.Unlock()
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, item)
}

func (s *memoryStore) deleteNotificationChannel(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	s.Lock()
	_, ok := s.notificationChannels[r.PathValue("id")]
	delete(s.notificationChannels, r.PathValue("id"))
	delete(s.notificationSecrets, r.PathValue("id"))
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	s.persistSnapshot()
	writeJSON(w, http.StatusOK, map[string]string{"status": "deleted"})
}

func (s *memoryStore) testNotificationChannel(w http.ResponseWriter, r *http.Request) {
	if !s.requireWriteToken(w, r) {
		return
	}
	s.Lock()
	channel, ok := s.notificationChannels[r.PathValue("id")]
	secret := s.notificationSecrets[channel.ID]
	s.Unlock()
	if !ok {
		writeError(w, http.StatusNotFound, "not_found")
		return
	}
	if secret == "" {
		writeError(w, http.StatusBadRequest, "notification_target_not_configured")
		return
	}
	target, err := s.decryptNotificationTarget(secret)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "notification_target_unavailable")
		return
	}
	delivery := s.deliverWebhook(channel, target, "notification.test", "INFO: test notification\nStream: test\nAlert: notification.test\nDuration: 0.0 sec\nStatus: open")
	status := http.StatusAccepted
	if delivery.Status == "failed" {
		status = http.StatusBadGateway
	}
	writeJSON(w, status, delivery)
}

func (s *memoryStore) listNotificationDeliveries(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	defer s.Unlock()
	items := make([]contracts.NotificationDelivery, 0, len(s.notificationDeliveries))
	for _, item := range s.notificationDeliveries {
		items = append(items, item)
	}
	writeJSON(w, http.StatusOK, items)
}

func (s *memoryStore) systemStatus(w http.ResponseWriter, r *http.Request) {
	if !s.requireReadToken(w, r) {
		return
	}
	s.Lock()
	clientStatuses := s.clientStatusesLocked(time.Now().UTC())
	onlineClients := 0
	offlineClients := 0
	for _, status := range clientStatuses {
		if status.Online {
			onlineClients++
		} else {
			offlineClients++
		}
	}
	counts := map[string]any{
		"streams":                 len(s.streams),
		"layouts":                 len(s.layouts),
		"alert_profiles":          len(s.alertProfiles),
		"clients":                 len(clientStatuses),
		"client_heartbeats":       len(s.clientHeartbeats),
		"clients_online":          onlineClients,
		"clients_offline":         offlineClients,
		"alerts":                  len(s.alerts),
		"notification_channels":   len(s.notificationChannels),
		"notification_deliveries": len(s.notificationDeliveries),
		"client_tokens":           len(s.clientTokens),
		"state_persistence":       s.persistenceStatus(),
		"media_processing":        "disabled_on_server",
	}
	s.Unlock()
	writeJSON(w, http.StatusOK, counts)
}

func (s *memoryStore) loadSnapshot() error {
	if s.db != nil {
		var payload string
		err := s.db.QueryRow(`SELECT payload FROM server_state WHERE id = 'current'`).Scan(&payload)
		if err != nil {
			if err == sql.ErrNoRows {
				return nil
			}
			return err
		}
		return s.restoreSnapshot(strings.NewReader(payload))
	}
	if s.statePath == "" {
		return nil
	}
	file, err := os.Open(s.statePath)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	defer file.Close()

	return s.restoreSnapshot(file)
}

func (s *memoryStore) restoreSnapshot(reader io.Reader) error {
	var state persistedState
	if err := json.NewDecoder(reader).Decode(&state); err != nil {
		return err
	}
	s.Lock()
	defer s.Unlock()
	if state.Streams != nil {
		s.streams = state.Streams
	}
	if state.Layouts != nil {
		s.layouts = state.Layouts
	}
	if state.AlertProfiles != nil {
		s.alertProfiles = state.AlertProfiles
	}
	if state.Clients != nil {
		s.clients = state.Clients
	}
	if state.ClientHeartbeats != nil {
		s.clientHeartbeats = state.ClientHeartbeats
	}
	if state.Alerts != nil {
		s.alerts = state.Alerts
	}
	if state.NotificationChannels != nil {
		s.notificationChannels = state.NotificationChannels
	}
	if state.NotificationSecrets != nil {
		s.notificationSecrets = state.NotificationSecrets
	}
	if state.NotificationDeliveries != nil {
		s.notificationDeliveries = state.NotificationDeliveries
	}
	for tokenHash, record := range state.ClientTokens {
		if _, exists := s.clientTokens[tokenHash]; !exists {
			s.clientTokens[tokenHash] = record
		}
	}
	return nil
}

func (s *memoryStore) persistSnapshot() {
	state := s.snapshotState()
	if s.db != nil {
		payload, err := json.MarshalIndent(state, "", "  ")
		if err != nil {
			log.Printf("failed to encode SQL state snapshot: %v", err)
			return
		}
		if _, err := s.db.Exec(
			`INSERT INTO server_state (id, payload, updated_at) VALUES ('current', ?, CURRENT_TIMESTAMP)
			 ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, updated_at = CURRENT_TIMESTAMP`,
			string(payload),
		); err != nil {
			log.Printf("failed to persist SQL state snapshot: %v", err)
		}
		return
	}

	if s.statePath == "" {
		return
	}
	if err := os.MkdirAll(filepath.Dir(s.statePath), 0o750); err != nil {
		log.Printf("failed to create state directory: %v", err)
		return
	}
	tmpPath := s.statePath + ".tmp"
	file, err := os.OpenFile(tmpPath, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
	if err != nil {
		log.Printf("failed to open state snapshot: %v", err)
		return
	}
	encoder := json.NewEncoder(file)
	encoder.SetIndent("", "  ")
	encodeErr := encoder.Encode(state)
	closeErr := file.Close()
	if encodeErr != nil {
		log.Printf("failed to encode state snapshot: %v", encodeErr)
		_ = os.Remove(tmpPath)
		return
	}
	if closeErr != nil {
		log.Printf("failed to close state snapshot: %v", closeErr)
		_ = os.Remove(tmpPath)
		return
	}
	if err := os.Rename(tmpPath, s.statePath); err != nil {
		log.Printf("failed to commit state snapshot: %v", err)
	}
}

func (s *memoryStore) persistenceStatus() string {
	if s.db != nil {
		return "sqlite"
	}
	if s.statePath == "" {
		return "disabled"
	}
	return "json_snapshot"
}

func (s *memoryStore) clientStatusesLocked(now time.Time) []contracts.ClientStatus {
	ids := map[string]bool{}
	for clientID := range s.clients {
		ids[clientID] = true
	}
	for clientID := range s.clientHeartbeats {
		ids[clientID] = true
	}
	statuses := make([]contracts.ClientStatus, 0, len(ids))
	for clientID := range ids {
		status, _ := s.clientStatusLocked(clientID, now)
		statuses = append(statuses, status)
	}
	return statuses
}

func (s *memoryStore) clientStatusLocked(clientID string, now time.Time) (contracts.ClientStatus, bool) {
	registration, registered := s.clients[clientID]
	heartbeat, hasHeartbeat := s.clientHeartbeats[clientID]
	if !registered && !hasHeartbeat {
		return contracts.ClientStatus{}, false
	}
	status := contracts.ClientStatus{
		ClientID:     clientID,
		Registered:   registered,
		Status:       "offline",
		Online:       false,
		Capabilities: map[string]bool{},
	}
	if registered {
		status.Name = registration.Name
		status.Version = registration.Version
		status.Mode = registration.Mode
		status.Capabilities = copyMap(registration.Capabilities)
	}
	if hasHeartbeat {
		status.Status = heartbeat.Status
		status.ActiveLayoutID = heartbeat.ActiveLayoutID
		status.ActiveAlerts = heartbeat.ActiveAlerts
		status.LastHeartbeatAt = heartbeat.Timestamp
		if !heartbeat.Timestamp.IsZero() {
			age := now.Sub(heartbeat.Timestamp)
			if age < 0 {
				age = 0
			}
			status.HeartbeatAgeSec = int64(age.Seconds())
			status.Online = age <= s.clientOfflineAfter
			if !status.Online {
				status.Status = "offline"
			}
		}
	}
	return status, true
}

func (s *memoryStore) snapshotState() persistedState {
	s.Lock()
	defer s.Unlock()
	return persistedState{
		Streams:                copyMap(s.streams),
		Layouts:                copyMap(s.layouts),
		AlertProfiles:          copyMap(s.alertProfiles),
		Clients:                copyMap(s.clients),
		ClientHeartbeats:       copyMap(s.clientHeartbeats),
		Alerts:                 copyMap(s.alerts),
		NotificationChannels:   copyMap(s.notificationChannels),
		NotificationSecrets:    copyMap(s.notificationSecrets),
		NotificationDeliveries: copyMap(s.notificationDeliveries),
		ClientTokens:           copyMap(s.clientTokens),
	}
}

func persistenceFromEnv() (*sql.DB, string, error) {
	databaseURL := strings.TrimSpace(os.Getenv("DATABASE_URL"))
	if databaseURL != "" && strings.HasPrefix(strings.ToLower(databaseURL), "sqlite://") {
		db, err := openSQLiteState(databaseURL)
		return db, "", err
	}
	return nil, statePathFromEnv(), nil
}

func statePathFromEnv() string {
	dataDir := strings.TrimSpace(os.Getenv("MULTIVIEWER_DATA_DIR"))
	if dataDir == "" {
		return ""
	}
	return filepath.Join(dataDir, "server-state.json")
}

func durationFromEnv(name string, fallback time.Duration) time.Duration {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback
	}
	seconds, err := strconv.Atoi(value)
	if err != nil || seconds <= 0 {
		return fallback
	}
	return time.Duration(seconds) * time.Second
}

func openSQLiteState(databaseURL string) (*sql.DB, error) {
	dsn, err := sqliteDSN(databaseURL)
	if err != nil {
		return nil, err
	}
	if dsn != ":memory:" {
		if err := os.MkdirAll(filepath.Dir(dsn), 0o750); err != nil {
			return nil, err
		}
	}
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, err
	}
	if _, err := db.Exec(`CREATE TABLE IF NOT EXISTS server_state (
		id TEXT PRIMARY KEY,
		payload TEXT NOT NULL,
		updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
	)`); err != nil {
		_ = db.Close()
		return nil, err
	}
	return db, nil
}

func sqliteDSN(databaseURL string) (string, error) {
	if databaseURL == "sqlite://:memory:" || databaseURL == "sqlite:///:memory:" {
		return ":memory:", nil
	}
	u, err := url.Parse(databaseURL)
	if err != nil {
		return "", err
	}
	path := u.Path
	if u.Host != "" {
		path = filepath.Join(string(os.PathSeparator)+u.Host, u.Path)
	}
	if strings.HasPrefix(path, "/") && len(path) >= 3 && path[2] == ':' {
		path = strings.TrimPrefix(path, "/")
	}
	return filepath.Clean(path), nil
}

func copyMap[K comparable, V any](input map[K]V) map[K]V {
	output := make(map[K]V, len(input))
	for key, value := range input {
		output[key] = value
	}
	return output
}

func (s *memoryStore) requireReadToken(w http.ResponseWriter, r *http.Request) bool {
	_, ok := s.authorizeWrite(w, r)
	return ok
}

func (s *memoryStore) requireWriteToken(w http.ResponseWriter, r *http.Request) bool {
	_, ok := s.authorizeWrite(w, r)
	return ok
}

func (s *memoryStore) authorizeWrite(w http.ResponseWriter, r *http.Request) (string, bool) {
	token := bearerToken(r)
	if token == "" {
		token = strings.TrimSpace(r.Header.Get("X-Client-Token"))
	}
	if token == "" {
		if s.clientToken == "" && len(s.clientTokens) == 0 {
			return "", true
		}
		w.Header().Set("WWW-Authenticate", `Bearer realm="ip-multiviewer"`)
		writeError(w, http.StatusUnauthorized, "unauthorized")
		return "", false
	}
	tokenHash := hashToken(token)
	s.Lock()
	record, hasRecord := s.clientTokens[tokenHash]
	s.Unlock()
	if hasRecord {
		if record.Revoked {
			w.Header().Set("WWW-Authenticate", `Bearer realm="ip-multiviewer"`)
			writeError(w, http.StatusUnauthorized, "unauthorized")
			return "", false
		}
		return tokenHash, true
	}
	if s.clientToken != "" && subtle.ConstantTimeCompare([]byte(token), []byte(s.clientToken)) == 1 {
		return tokenHash, true
	}
	if s.clientToken == "" && len(s.clientTokens) == 0 {
		return tokenHash, true
	}
	w.Header().Set("WWW-Authenticate", `Bearer realm="ip-multiviewer"`)
	writeError(w, http.StatusUnauthorized, "unauthorized")
	return "", false
}

func (s *memoryStore) validateClientTokenOwner(w http.ResponseWriter, tokenHash string, clientID string) bool {
	if tokenHash == "" || s.isBootstrapTokenHash(tokenHash) {
		return true
	}
	s.Lock()
	record, ok := s.clientTokens[tokenHash]
	s.Unlock()
	if !ok || record.ClientID == "" || record.ClientID == clientID {
		return true
	}
	writeError(w, http.StatusForbidden, "client_token_scope_mismatch")
	return false
}

func (s *memoryStore) clientIDForTokenHash(tokenHash string) string {
	if tokenHash == "" || s.isBootstrapTokenHash(tokenHash) {
		return ""
	}
	s.Lock()
	record, ok := s.clientTokens[tokenHash]
	s.Unlock()
	if !ok {
		return ""
	}
	return record.ClientID
}

func (s *memoryStore) isBootstrapTokenHash(tokenHash string) bool {
	return s.clientToken != "" && subtle.ConstantTimeCompare([]byte(tokenHash), []byte(hashToken(s.clientToken))) == 1
}

func bearerToken(r *http.Request) string {
	value := strings.TrimSpace(r.Header.Get("Authorization"))
	if value == "" {
		return ""
	}
	const prefix = "Bearer "
	if len(value) < len(prefix) || !strings.EqualFold(value[:len(prefix)], prefix) {
		return ""
	}
	return strings.TrimSpace(value[len(prefix):])
}

func secureHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Referrer-Policy", "no-referrer")
		next.ServeHTTP(w, r)
	})
}

func decode(w http.ResponseWriter, r *http.Request, dest any) bool {
	if err := json.NewDecoder(r.Body).Decode(dest); err != nil {
		writeError(w, http.StatusBadRequest, "bad_request")
		return false
	}
	return true
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, status int, code string) {
	writeJSON(w, status, contracts.ErrorResponse{RequestID: newID(), Code: code, Message: code})
}

func newID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return time.Now().UTC().Format("20060102150405.000000000")
	}
	return hex.EncodeToString(b[:])
}

func hashToken(token string) string {
	sum := sha256.Sum256([]byte(strings.TrimSpace(token)))
	return hex.EncodeToString(sum[:])
}

func parseClientTokens(value string) map[string]clientTokenRecord {
	records := map[string]clientTokenRecord{}
	for _, entry := range strings.Split(value, ",") {
		entry = strings.TrimSpace(entry)
		if entry == "" {
			continue
		}
		clientID, token, ok := strings.Cut(entry, ":")
		clientID = strings.TrimSpace(clientID)
		token = strings.TrimSpace(token)
		if !ok || clientID == "" || token == "" {
			continue
		}
		tokenHash := hashToken(token)
		records[tokenHash] = clientTokenRecord{
			ClientID:  clientID,
			TokenHash: tokenHash,
			CreatedAt: time.Now().UTC(),
		}
	}
	return records
}

type secretCodec struct {
	key []byte
}

func secretCodecFromEnv() (*secretCodec, error) {
	value := strings.TrimSpace(os.Getenv("MULTIVIEWER_SECRET_ENCRYPTION_KEY"))
	if value == "" {
		return nil, nil
	}
	return newSecretCodec(value)
}

func newSecretCodec(value string) (*secretCodec, error) {
	key := []byte(value)
	if decoded, err := hex.DecodeString(value); err == nil && len(decoded) == 32 {
		key = decoded
	} else if decoded, err := base64.StdEncoding.DecodeString(value); err == nil && len(decoded) == 32 {
		key = decoded
	}
	if len(key) != 32 {
		return nil, errors.New("MULTIVIEWER_SECRET_ENCRYPTION_KEY must be 32 bytes, 64 hex chars, or base64 encoded 32 bytes")
	}
	return &secretCodec{key: append([]byte(nil), key...)}, nil
}

func (c *secretCodec) encrypt(plain string) (string, error) {
	block, err := aes.NewCipher(c.key)
	if err != nil {
		return "", err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", err
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return "", err
	}
	sealed := gcm.Seal(nonce, nonce, []byte(plain), nil)
	return "v1:" + base64.StdEncoding.EncodeToString(sealed), nil
}

func (c *secretCodec) decrypt(cipherText string) (string, error) {
	encoded, ok := strings.CutPrefix(cipherText, "v1:")
	if !ok {
		return "", errors.New("unsupported secret version")
	}
	payload, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		return "", err
	}
	block, err := aes.NewCipher(c.key)
	if err != nil {
		return "", err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", err
	}
	if len(payload) < gcm.NonceSize() {
		return "", errors.New("invalid encrypted secret")
	}
	nonce := payload[:gcm.NonceSize()]
	cipherBody := payload[gcm.NonceSize():]
	plain, err := gcm.Open(nil, nonce, cipherBody, nil)
	if err != nil {
		return "", err
	}
	return string(plain), nil
}

func (s *memoryStore) prepareNotificationChannel(w http.ResponseWriter, item *contracts.NotificationChannel, existing contracts.NotificationChannel) (string, bool, bool) {
	target := strings.TrimSpace(item.TargetURL)
	if target == "" && looksLikeWebhookURL(item.TargetMasked) {
		target = strings.TrimSpace(item.TargetMasked)
	}
	item.TargetURL = ""
	if target == "" {
		item.TargetMasked = existing.TargetMasked
		item.TargetConfigured = existing.TargetConfigured
		return "", false, true
	}
	if err := validateWebhookTarget(target); err != nil {
		writeError(w, http.StatusBadRequest, "invalid_notification_target")
		return "", false, false
	}
	if s.secretCodec == nil {
		writeError(w, http.StatusBadRequest, "secret_encryption_key_required")
		return "", false, false
	}
	encrypted, err := s.secretCodec.encrypt(target)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "secret_encrypt_failed")
		return "", false, false
	}
	item.TargetMasked = maskWebhookTarget(target)
	item.TargetConfigured = true
	return encrypted, true, true
}

func looksLikeWebhookURL(value string) bool {
	value = strings.TrimSpace(value)
	return strings.HasPrefix(value, "https://") || strings.HasPrefix(value, "http://")
}

func validateWebhookTarget(value string) error {
	parsed, err := url.Parse(strings.TrimSpace(value))
	if err != nil {
		return err
	}
	if parsed.Scheme != "https" && parsed.Scheme != "http" {
		return errors.New("unsupported webhook scheme")
	}
	if parsed.Host == "" {
		return errors.New("webhook host required")
	}
	return nil
}

func (s *memoryStore) decryptNotificationTarget(secret string) (string, error) {
	if s.secretCodec == nil {
		return "", errors.New("secret encryption key is not configured")
	}
	return s.secretCodec.decrypt(secret)
}

func (s *memoryStore) deliverAlertNotifications(alert contracts.Alert, eventType string) {
	s.Lock()
	streamName := alert.StreamID
	if stream, ok := s.streams[alert.StreamID]; ok && stream.Name != "" {
		streamName = stream.Name
	}
	type targetChannel struct {
		channel contracts.NotificationChannel
		secret  string
	}
	channels := make([]targetChannel, 0, len(s.notificationChannels))
	for _, channel := range s.notificationChannels {
		if !channel.Enabled || !channel.TargetConfigured {
			continue
		}
		if !matchesSeverity(channel.SeverityFilters, alert.Severity) || !matchesEvent(channel.EventFilters, eventType) {
			continue
		}
		channels = append(channels, targetChannel{channel: channel, secret: s.notificationSecrets[channel.ID]})
	}
	s.Unlock()

	duration := 0.0
	if !alert.StartedAt.IsZero() {
		duration = time.Since(alert.StartedAt).Seconds()
	}
	text := notificationcore.FormatJapanese(notificationcore.Alert{
		Severity:    string(alert.Severity),
		StreamName:  streamName,
		Type:        string(alert.Type),
		DurationSec: duration,
		Status:      string(alert.Status),
	})
	for _, item := range channels {
		target, err := s.decryptNotificationTarget(item.secret)
		if err != nil {
			s.recordNotificationDelivery(item.channel, eventType, 0, "failed", "notification target unavailable")
			continue
		}
		s.deliverWebhook(item.channel, target, eventType, text)
	}
}

func matchesSeverity(filters []contracts.AlertSeverity, severity contracts.AlertSeverity) bool {
	if len(filters) == 0 {
		return true
	}
	for _, item := range filters {
		if item == severity {
			return true
		}
	}
	return false
}

func matchesEvent(filters []string, eventType string) bool {
	if len(filters) == 0 {
		return true
	}
	for _, item := range filters {
		if item == eventType {
			return true
		}
	}
	return false
}

func (s *memoryStore) deliverWebhook(channel contracts.NotificationChannel, target string, eventType string, text string) contracts.NotificationDelivery {
	attempts := 0
	var lastErr error
	for attempts = 1; attempts <= 3; attempts++ {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		err := notificationcore.DeliverWebhook(ctx, s.httpClient, notificationcore.WebhookRequest{
			URL:         target,
			ChannelType: string(channel.Type),
			Text:        text,
		})
		cancel()
		if err == nil {
			return s.recordNotificationDelivery(channel, eventType, attempts, "delivered", "")
		}
		lastErr = err
		if attempts < 3 {
			time.Sleep(time.Duration(attempts) * 100 * time.Millisecond)
		}
	}
	return s.recordNotificationDelivery(channel, eventType, attempts-1, "failed", safeDeliveryError(lastErr))
}

func (s *memoryStore) recordNotificationDelivery(channel contracts.NotificationChannel, eventType string, attempts int, status string, lastError string) contracts.NotificationDelivery {
	now := time.Now().UTC()
	delivery := contracts.NotificationDelivery{
		ID:           newID(),
		ChannelID:    channel.ID,
		EventType:    eventType,
		Status:       status,
		TargetMasked: channel.TargetMasked,
		Attempts:     attempts,
		LastError:    lastError,
		CreatedAt:    now,
		UpdatedAt:    now,
	}
	s.Lock()
	s.notificationDeliveries[delivery.ID] = delivery
	s.Unlock()
	s.persistSnapshot()
	return delivery
}

func safeDeliveryError(err error) string {
	if err == nil {
		return ""
	}
	message := err.Error()
	if strings.Contains(message, "http://") || strings.Contains(message, "https://") {
		return "webhook request failed"
	}
	if len(message) > 200 {
		message = message[:200]
	}
	return message
}

func maskWebhookTarget(value string) string {
	value = strings.TrimSpace(value)
	if value == "" {
		return ""
	}
	if strings.Contains(value, "discord.com/api/webhooks/") {
		return "https://discord.com/api/webhooks/<WEBHOOK_ID>/<WEBHOOK_TOKEN>"
	}
	if strings.Contains(value, "hooks.slack.com/services/") {
		return "https://hooks.slack.com/services/<YOUR_WEBHOOK>"
	}
	return "<configured>"
}
