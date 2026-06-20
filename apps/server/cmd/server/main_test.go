package main

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/example/ip-multiviewer/packages/contracts"
)

func TestStreamAPIMasksCredentials(t *testing.T) {
	store := testStore()
	req := httptest.NewRequest(http.MethodPost, "/streams", strings.NewReader(`{"id":"cam-01","name":"Camera 01","enabled":true,"input_url":"rtsp://user@camera.example.com/live?password=super-secret","input_protocol":"rtsp"}`))
	res := httptest.NewRecorder()
	store.createStream(res, req)
	if res.Code != http.StatusCreated {
		t.Fatalf("create stream status = %d body = %s", res.Code, res.Body.String())
	}
	if strings.Contains(res.Body.String(), "super-secret") || !strings.Contains(res.Body.String(), "%2A%2A%2A%2A@camera.example.com") {
		t.Fatalf("stream response did not mask credentials: %s", res.Body.String())
	}

	req = httptest.NewRequest(http.MethodGet, "/streams/cam-01", nil)
	req.SetPathValue("id", "cam-01")
	res = httptest.NewRecorder()
	store.getStream(res, req)
	if res.Code != http.StatusOK {
		t.Fatalf("get stream status = %d body = %s", res.Code, res.Body.String())
	}
	if strings.Contains(res.Body.String(), "super-secret") || !strings.Contains(res.Body.String(), "%2A%2A%2A%2A@camera.example.com") {
		t.Fatalf("get stream response did not mask credentials: %s", res.Body.String())
	}
}

func TestAlertStatusTransitions(t *testing.T) {
	store := testStore()
	req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"error","message":"black detected"}`))
	res := httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("create alert status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/alerts/alert-01/acknowledge", nil)
	req.SetPathValue("id", "alert-01")
	res = httptest.NewRecorder()
	store.setAlertStatus(contracts.AlertAcknowledged)(res, req)
	if res.Code != http.StatusOK || !strings.Contains(res.Body.String(), `"status":"acknowledged"`) {
		t.Fatalf("ack status = %d body = %s", res.Code, res.Body.String())
	}
}

func TestClientAlertDeduplicatesActiveAlertAndNotification(t *testing.T) {
	webhookCalls := 0
	webhook := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		webhookCalls++
		w.WriteHeader(http.StatusNoContent)
	}))
	defer webhook.Close()

	store := testStore()
	store.streams["cam-01"] = contracts.StreamConfig{ID: "cam-01", Name: "Camera 01"}
	createChannelReq := httptest.NewRequest(http.MethodPost, "/notification-channels", strings.NewReader(`{"id":"ntf-01","name":"Generic","type":"generic","target_url":"`+webhook.URL+`","enabled":true}`))
	createChannelRes := httptest.NewRecorder()
	store.createNotificationChannel(createChannelRes, createChannelReq)
	if createChannelRes.Code != http.StatusCreated {
		t.Fatalf("create notification channel status = %d body = %s", createChannelRes.Code, createChannelRes.Body.String())
	}

	firstReq := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.no_signal","severity":"warning","message":"first"}`))
	firstRes := httptest.NewRecorder()
	store.createClientAlert(firstRes, firstReq)
	if firstRes.Code != http.StatusAccepted {
		t.Fatalf("first alert status = %d body = %s", firstRes.Code, firstRes.Body.String())
	}

	secondReq := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-02","stream_id":"cam-01","type":"video.no_signal","severity":"warning","message":"second"}`))
	secondRes := httptest.NewRecorder()
	store.createClientAlert(secondRes, secondReq)
	if secondRes.Code != http.StatusAccepted {
		t.Fatalf("second alert status = %d body = %s", secondRes.Code, secondRes.Body.String())
	}
	if !strings.Contains(secondRes.Body.String(), `"id":"alert-01"`) || !strings.Contains(secondRes.Body.String(), `"severity":"warning"`) {
		t.Fatalf("second alert was not merged into active alert: %s", secondRes.Body.String())
	}
	if len(store.alerts) != 1 {
		t.Fatalf("active alert was not deduplicated: %#v", store.alerts)
	}
	if webhookCalls != 1 || len(store.notificationDeliveries) != 1 {
		t.Fatalf("notification was not deduplicated: calls=%d deliveries=%d", webhookCalls, len(store.notificationDeliveries))
	}
}

func TestClientAlertEscalationSendsUpdatedNotification(t *testing.T) {
	webhookCalls := 0
	webhook := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		webhookCalls++
		w.WriteHeader(http.StatusNoContent)
	}))
	defer webhook.Close()

	store := testStore()
	store.streams["cam-01"] = contracts.StreamConfig{ID: "cam-01", Name: "Camera 01"}
	createChannelReq := httptest.NewRequest(http.MethodPost, "/notification-channels", strings.NewReader(`{"id":"ntf-01","name":"Generic","type":"generic","target_url":"`+webhook.URL+`","enabled":true}`))
	createChannelRes := httptest.NewRecorder()
	store.createNotificationChannel(createChannelRes, createChannelReq)
	if createChannelRes.Code != http.StatusCreated {
		t.Fatalf("create notification channel status = %d body = %s", createChannelRes.Code, createChannelRes.Body.String())
	}

	req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"warning","message":"first"}`))
	res := httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("first alert status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-02","stream_id":"cam-01","type":"video.black","severity":"error","message":"escalated"}`))
	res = httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("escalated alert status = %d body = %s", res.Code, res.Body.String())
	}
	if webhookCalls != 2 {
		t.Fatalf("severity escalation should notify once more: calls=%d", webhookCalls)
	}
	var updatedDeliveries int
	for _, delivery := range store.notificationDeliveries {
		if delivery.EventType == "video.black.updated" {
			updatedDeliveries++
		}
	}
	if updatedDeliveries != 1 {
		t.Fatalf("updated delivery not recorded once: %#v", store.notificationDeliveries)
	}
}

func TestClientAlertCreatesNewAfterResolved(t *testing.T) {
	store := testStore()
	req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.no_signal","severity":"error","message":"first"}`))
	res := httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("create alert status = %d body = %s", res.Code, res.Body.String())
	}

	resolveReq := httptest.NewRequest(http.MethodPost, "/alerts/alert-01/resolve", nil)
	resolveReq.SetPathValue("id", "alert-01")
	resolveRes := httptest.NewRecorder()
	store.setAlertStatus(contracts.AlertResolved)(resolveRes, resolveReq)
	if resolveRes.Code != http.StatusOK {
		t.Fatalf("resolve status = %d body = %s", resolveRes.Code, resolveRes.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-02","stream_id":"cam-01","type":"video.no_signal","severity":"error","message":"second"}`))
	res = httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("create second alert status = %d body = %s", res.Code, res.Body.String())
	}
	if len(store.alerts) != 2 {
		t.Fatalf("new alert after resolve was not created: %#v", store.alerts)
	}
}

func TestClientAlertStatusSyncCanResolveActiveAlert(t *testing.T) {
	store := testStore()
	req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.no_signal","severity":"error","message":"first"}`))
	res := httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("create alert status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.no_signal","severity":"error","status":"resolved","message":"resolved locally"}`))
	res = httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("resolve via client alert status = %d body = %s", res.Code, res.Body.String())
	}
	if !strings.Contains(res.Body.String(), `"status":"resolved"`) {
		t.Fatalf("client alert status transition was not applied: %s", res.Body.String())
	}
	if got := store.alerts["alert-01"].Status; got != contracts.AlertResolved {
		t.Fatalf("server alert status = %s, want resolved", got)
	}
}

func TestDuplicateOpenAlertDoesNotReopenAcknowledgedAlert(t *testing.T) {
	store := testStore()
	req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"warning","message":"first"}`))
	res := httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("create alert status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"warning","status":"acknowledged","message":"acknowledged locally"}`))
	res = httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("acknowledge via client alert status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-02","stream_id":"cam-01","type":"video.black","severity":"error","status":"open","message":"duplicate open"}`))
	res = httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("duplicate alert status = %d body = %s", res.Code, res.Body.String())
	}
	if got := store.alerts["alert-01"].Status; got != contracts.AlertAcknowledged {
		t.Fatalf("duplicate open reopened acknowledged alert: got %s", got)
	}
	if got := store.alerts["alert-01"].Severity; got != contracts.SeverityError {
		t.Fatalf("duplicate alert did not preserve higher severity: got %s", got)
	}
}

func TestInvalidClientAlertRejected(t *testing.T) {
	store := testStore()
	cases := []string{
		`{"id":"alert-01","stream_id":"cam-01","type":"video.unknown","severity":"error","message":"bad"}`,
		`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"loud","message":"bad"}`,
		`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"error","status":"muted","message":"bad"}`,
	}
	for _, body := range cases {
		req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(body))
		res := httptest.NewRecorder()
		store.createClientAlert(res, req)
		if res.Code != http.StatusBadRequest {
			t.Fatalf("invalid alert status = %d body = %s for %s", res.Code, res.Body.String(), body)
		}
	}
}

func TestSnoozeAlertAcceptsSnoozedUntil(t *testing.T) {
	store := testStore()
	req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"error","message":"black detected"}`))
	res := httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("create alert status = %d body = %s", res.Code, res.Body.String())
	}

	until := time.Now().UTC().Add(30 * time.Minute).Truncate(time.Second)
	req = httptest.NewRequest(http.MethodPost, "/alerts/alert-01/snooze", strings.NewReader(`{"snoozed_until":"`+until.Format(time.RFC3339)+`"}`))
	req.SetPathValue("id", "alert-01")
	res = httptest.NewRecorder()
	store.setAlertStatus(contracts.AlertSnoozed)(res, req)
	if res.Code != http.StatusOK {
		t.Fatalf("snooze status = %d body = %s", res.Code, res.Body.String())
	}
	if got := store.alerts["alert-01"].SnoozedUntil.Truncate(time.Second); !got.Equal(until) {
		t.Fatalf("snoozed_until = %s, want %s", got, until)
	}
}

func TestNotificationChannelMasksTarget(t *testing.T) {
	store := testStore()
	target := "https://discord.com/api/webhooks/123/" + "secret"
	req := httptest.NewRequest(http.MethodPost, "/notification-channels", strings.NewReader(`{"id":"ntf-01","name":"Discord","type":"discord","target_masked":"`+target+`","enabled":true}`))
	res := httptest.NewRecorder()
	store.createNotificationChannel(res, req)
	if res.Code != http.StatusCreated {
		t.Fatalf("create notification channel status = %d body = %s", res.Code, res.Body.String())
	}
	if strings.Contains(res.Body.String(), "/123/secret") || !strings.Contains(res.Body.String(), "WEBHOOK_TOKEN") {
		t.Fatalf("webhook target was not masked: %s", res.Body.String())
	}
	for _, encrypted := range store.notificationSecrets {
		if strings.Contains(encrypted, "secret") {
			t.Fatalf("encrypted webhook target leaked raw secret: %s", encrypted)
		}
	}
}

func TestNotificationTestDeliversAndRecordsHistory(t *testing.T) {
	var gotBody string
	webhook := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		gotBody = string(body)
		w.WriteHeader(http.StatusNoContent)
	}))
	defer webhook.Close()

	store := testStore()
	req := httptest.NewRequest(http.MethodPost, "/notification-channels", strings.NewReader(`{"id":"ntf-01","name":"Generic","type":"generic","target_url":"`+webhook.URL+`","enabled":true}`))
	res := httptest.NewRecorder()
	store.createNotificationChannel(res, req)
	if res.Code != http.StatusCreated {
		t.Fatalf("create notification channel status = %d body = %s", res.Code, res.Body.String())
	}
	if strings.Contains(res.Body.String(), webhook.URL) {
		t.Fatalf("notification response leaked raw webhook URL: %s", res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/notification-channels/ntf-01/test", nil)
	req.SetPathValue("id", "ntf-01")
	res = httptest.NewRecorder()
	store.testNotificationChannel(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("test notification status = %d body = %s", res.Code, res.Body.String())
	}
	if !strings.Contains(gotBody, "notification.test") {
		t.Fatalf("webhook did not receive test message: %s", gotBody)
	}
	if len(store.notificationDeliveries) != 1 {
		t.Fatalf("delivery history was not recorded: %#v", store.notificationDeliveries)
	}
}

func TestClientTokenRequiredForWriteAPI(t *testing.T) {
	store := testStore()
	store.clientToken = "expected-token"

	req := httptest.NewRequest(http.MethodPost, "/clients/heartbeat", strings.NewReader(`{"client_id":"client-01","status":"idle","active_alerts":0}`))
	res := httptest.NewRecorder()
	store.clientHeartbeat(res, req)
	if res.Code != http.StatusUnauthorized {
		t.Fatalf("heartbeat without token status = %d body = %s", res.Code, res.Body.String())
	}
	if strings.Contains(res.Body.String(), "expected-token") {
		t.Fatalf("response leaked client token: %s", res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/clients/heartbeat", strings.NewReader(`{"client_id":"client-01","status":"idle","active_alerts":0}`))
	req.Header.Set("Authorization", "Bearer expected-token")
	res = httptest.NewRecorder()
	store.clientHeartbeat(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("heartbeat with token status = %d body = %s", res.Code, res.Body.String())
	}
}

func TestClientTokenRequiredForReadAPI(t *testing.T) {
	store := testStore()
	store.clientToken = "expected-token"
	store.streams["cam-01"] = contracts.StreamConfig{ID: "cam-01", Name: "Camera 01", Enabled: true, InputURL: "rtsp://camera.example.com/live", InputProtocol: "rtsp"}

	req := httptest.NewRequest(http.MethodGet, "/streams", nil)
	res := httptest.NewRecorder()
	store.listStreams(res, req)
	if res.Code != http.StatusUnauthorized {
		t.Fatalf("read without token status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodGet, "/streams", nil)
	req.Header.Set("Authorization", "Bearer expected-token")
	res = httptest.NewRecorder()
	store.listStreams(res, req)
	if res.Code != http.StatusOK {
		t.Fatalf("read with token status = %d body = %s", res.Code, res.Body.String())
	}
}

func TestRevokedClientTokenCannotReadAPI(t *testing.T) {
	store := testStore()
	tokenHash := hashToken("client-token")
	store.clientTokens[tokenHash] = clientTokenRecord{ClientID: "client-01", TokenHash: tokenHash, Revoked: true}

	req := httptest.NewRequest(http.MethodGet, "/streams", nil)
	req.Header.Set("Authorization", "Bearer client-token")
	res := httptest.NewRecorder()
	store.listStreams(res, req)
	if res.Code != http.StatusUnauthorized {
		t.Fatalf("read with revoked token status = %d body = %s", res.Code, res.Body.String())
	}
}

func TestClientTokenMayUseHeaderFallback(t *testing.T) {
	store := testStore()
	store.clientToken = "expected-token"

	req := httptest.NewRequest(http.MethodPost, "/client-alerts", strings.NewReader(`{"id":"alert-01","stream_id":"cam-01","type":"video.black","severity":"error","message":"black detected"}`))
	req.Header.Set("X-Client-Token", "expected-token")
	res := httptest.NewRecorder()
	store.createClientAlert(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("client alert with header token status = %d body = %s", res.Code, res.Body.String())
	}
}

func TestPerClientTokenRevocation(t *testing.T) {
	store := testStore()
	tokenHash := hashToken("client-token")
	store.clientTokens[tokenHash] = clientTokenRecord{ClientID: "client-01", TokenHash: tokenHash}

	req := httptest.NewRequest(http.MethodPost, "/clients/heartbeat", strings.NewReader(`{"client_id":"client-01","status":"idle","active_alerts":0}`))
	req.Header.Set("Authorization", "Bearer client-token")
	res := httptest.NewRecorder()
	store.clientHeartbeat(res, req)
	if res.Code != http.StatusAccepted {
		t.Fatalf("heartbeat before revoke status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/clients/client-01/revoke-token", nil)
	req.SetPathValue("id", "client-01")
	req.Header.Set("Authorization", "Bearer client-token")
	res = httptest.NewRecorder()
	store.revokeClientToken(res, req)
	if res.Code != http.StatusOK || !strings.Contains(res.Body.String(), `"revoked_tokens":1`) {
		t.Fatalf("revoke status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodPost, "/clients/heartbeat", strings.NewReader(`{"client_id":"client-01","status":"idle","active_alerts":0}`))
	req.Header.Set("Authorization", "Bearer client-token")
	res = httptest.NewRecorder()
	store.clientHeartbeat(res, req)
	if res.Code != http.StatusUnauthorized {
		t.Fatalf("heartbeat after revoke status = %d body = %s", res.Code, res.Body.String())
	}
}

func TestClientTokenScopeMismatch(t *testing.T) {
	store := testStore()
	tokenHash := hashToken("client-token")
	store.clientTokens[tokenHash] = clientTokenRecord{ClientID: "client-01", TokenHash: tokenHash}

	req := httptest.NewRequest(http.MethodPost, "/clients/heartbeat", strings.NewReader(`{"client_id":"client-02","status":"idle","active_alerts":0}`))
	req.Header.Set("Authorization", "Bearer client-token")
	res := httptest.NewRecorder()
	store.clientHeartbeat(res, req)
	if res.Code != http.StatusForbidden {
		t.Fatalf("scope mismatch status = %d body = %s", res.Code, res.Body.String())
	}
}

func TestClientStatusListsOnlineAndOfflineClients(t *testing.T) {
	store := testStore()
	store.clientOfflineAfter = 90 * time.Second
	now := time.Now().UTC()
	store.clients["agent-01"] = contracts.ClientRegistration{
		ClientID:     "agent-01",
		Name:         "Agent 01",
		Version:      "dev",
		Mode:         "agent",
		Capabilities: map[string]bool{"stream_probe": true},
	}
	store.clientHeartbeats["agent-01"] = contracts.ClientHeartbeat{
		ClientID:     "agent-01",
		Status:       "monitoring",
		ActiveAlerts: 1,
		Timestamp:    now.Add(-30 * time.Second),
	}
	store.clients["desktop-01"] = contracts.ClientRegistration{
		ClientID: "desktop-01",
		Name:     "Desktop 01",
		Mode:     "desktop",
	}
	store.clientHeartbeats["desktop-01"] = contracts.ClientHeartbeat{
		ClientID:  "desktop-01",
		Status:    "monitoring",
		Timestamp: now.Add(-5 * time.Minute),
	}
	store.clientHeartbeats["unknown-01"] = contracts.ClientHeartbeat{
		ClientID:  "unknown-01",
		Status:    "monitoring",
		Timestamp: now,
	}

	req := httptest.NewRequest(http.MethodGet, "/clients", nil)
	res := httptest.NewRecorder()
	store.listClients(res, req)
	if res.Code != http.StatusOK {
		t.Fatalf("list clients status = %d body = %s", res.Code, res.Body.String())
	}
	var statuses []contracts.ClientStatus
	if err := json.NewDecoder(res.Body).Decode(&statuses); err != nil {
		t.Fatalf("decode client statuses: %v", err)
	}
	if len(statuses) != 3 {
		t.Fatalf("client status count = %d: %#v", len(statuses), statuses)
	}
	byID := map[string]contracts.ClientStatus{}
	for _, status := range statuses {
		byID[status.ClientID] = status
	}
	if !byID["agent-01"].Online || byID["agent-01"].HeartbeatAgeSec < 0 || byID["agent-01"].ActiveAlerts != 1 {
		t.Fatalf("agent status was not online: %#v", byID["agent-01"])
	}
	if byID["desktop-01"].Online || byID["desktop-01"].Status != "offline" {
		t.Fatalf("desktop status was not offline: %#v", byID["desktop-01"])
	}
	if !byID["unknown-01"].Online || byID["unknown-01"].Registered {
		t.Fatalf("unknown heartbeat status was not represented: %#v", byID["unknown-01"])
	}
}

func TestGetClientStatusAndSystemClientCounts(t *testing.T) {
	store := testStore()
	store.clientOfflineAfter = 90 * time.Second
	now := time.Now().UTC()
	store.clients["agent-01"] = contracts.ClientRegistration{ClientID: "agent-01", Name: "Agent 01", Mode: "agent"}
	store.clientHeartbeats["agent-01"] = contracts.ClientHeartbeat{ClientID: "agent-01", Status: "monitoring", Timestamp: now}
	store.clients["desktop-01"] = contracts.ClientRegistration{ClientID: "desktop-01", Name: "Desktop 01", Mode: "desktop"}
	store.clientHeartbeats["desktop-01"] = contracts.ClientHeartbeat{ClientID: "desktop-01", Status: "monitoring", Timestamp: now.Add(-10 * time.Minute)}

	req := httptest.NewRequest(http.MethodGet, "/clients/agent-01", nil)
	req.SetPathValue("id", "agent-01")
	res := httptest.NewRecorder()
	store.getClient(res, req)
	if res.Code != http.StatusOK || !strings.Contains(res.Body.String(), `"online":true`) {
		t.Fatalf("get client status = %d body = %s", res.Code, res.Body.String())
	}

	req = httptest.NewRequest(http.MethodGet, "/system/status", nil)
	res = httptest.NewRecorder()
	store.systemStatus(res, req)
	if res.Code != http.StatusOK {
		t.Fatalf("system status = %d body = %s", res.Code, res.Body.String())
	}
	if !strings.Contains(res.Body.String(), `"clients_online":1`) || !strings.Contains(res.Body.String(), `"clients_offline":1`) {
		t.Fatalf("system status did not include client online counts: %s", res.Body.String())
	}
}

func TestStateSnapshotPersistsAndLoads(t *testing.T) {
	statePath := filepath.Join(t.TempDir(), "server-state.json")
	store := testStore()
	store.statePath = statePath
	store.streams["cam-01"] = contracts.StreamConfig{ID: "cam-01", Name: "Camera 01", Enabled: true, InputURL: "rtsp://camera.example.com/live?password=super-secret", InputProtocol: "rtsp"}
	store.alerts["alert-01"] = contracts.Alert{ID: "alert-01", StreamID: "cam-01", Type: contracts.AlertVideoBlack, Severity: contracts.SeverityWarning, Status: contracts.AlertOpen, Message: "black detected"}
	store.clientTokens[hashToken("client-token")] = clientTokenRecord{ClientID: "client-01", TokenHash: hashToken("client-token")}
	store.persistSnapshot()

	loaded := testStore()
	loaded.statePath = statePath
	if err := loaded.loadSnapshot(); err != nil {
		t.Fatalf("load snapshot: %v", err)
	}
	if loaded.streams["cam-01"].Name != "Camera 01" {
		t.Fatalf("stream was not loaded: %#v", loaded.streams["cam-01"])
	}
	if loaded.alerts["alert-01"].Status != contracts.AlertOpen {
		t.Fatalf("alert was not loaded: %#v", loaded.alerts["alert-01"])
	}
	if _, ok := loaded.clientTokens[hashToken("client-token")]; !ok {
		t.Fatalf("client token record was not loaded")
	}
}

func TestSQLiteStatePersistsAndLoads(t *testing.T) {
	dbPath := filepath.Join(t.TempDir(), "server-state.db")
	db, err := openSQLiteState("sqlite:///" + filepath.ToSlash(dbPath))
	if err != nil {
		t.Fatalf("open sqlite state: %v", err)
	}
	store := testStore()
	store.db = db
	store.streams["cam-01"] = contracts.StreamConfig{ID: "cam-01", Name: "Camera 01", Enabled: true, InputURL: "rtsp://camera.example.com/live?password=super-secret", InputProtocol: "rtsp"}
	store.persistSnapshot()
	if err := db.Close(); err != nil {
		t.Fatalf("close sqlite: %v", err)
	}

	db, err = openSQLiteState("sqlite:///" + filepath.ToSlash(dbPath))
	if err != nil {
		t.Fatalf("reopen sqlite state: %v", err)
	}
	defer db.Close()
	loaded := testStore()
	loaded.db = db
	if err := loaded.loadSnapshot(); err != nil {
		t.Fatalf("load sqlite snapshot: %v", err)
	}
	if loaded.streams["cam-01"].Name != "Camera 01" {
		t.Fatalf("stream was not loaded from sqlite: %#v", loaded.streams["cam-01"])
	}
}

func testStore() *memoryStore {
	return &memoryStore{
		streams:                map[string]contracts.StreamConfig{},
		layouts:                map[string]contracts.Layout{},
		alertProfiles:          map[string]contracts.AlertProfile{},
		clients:                map[string]contracts.ClientRegistration{},
		clientHeartbeats:       map[string]contracts.ClientHeartbeat{},
		alerts:                 map[string]contracts.Alert{},
		notificationChannels:   map[string]contracts.NotificationChannel{},
		notificationSecrets:    map[string]string{},
		notificationDeliveries: map[string]contracts.NotificationDelivery{},
		clientTokens:           map[string]clientTokenRecord{},
		secretCodec:            mustTestSecretCodec(),
		httpClient:             http.DefaultClient,
		clientOfflineAfter:     90 * time.Second,
	}
}

func mustTestSecretCodec() *secretCodec {
	codec, err := newSecretCodec("12345678901234567890123456789012")
	if err != nil {
		panic(err)
	}
	return codec
}
