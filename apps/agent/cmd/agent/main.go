package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/example/ip-multiviewer/packages/contracts"
	streamutils "github.com/example/ip-multiviewer/packages/stream-utils"
)

type config struct {
	ServerURL          string
	ClientID           string
	ClientName         string
	ClientToken        string
	Interval           time.Duration
	ProbeTimeout       time.Duration
	RunOnce            bool
	DisableProbing     bool
	DisableAutoResolve bool
	HTTPClient         *http.Client
}

type agent struct {
	cfg        config
	httpClient *http.Client
}

func main() {
	cfg := configFromEnv()
	if cfg.ServerURL == "" {
		log.Fatal("MULTIVIEWER_SERVER_URL is required")
	}
	a := newAgent(cfg)
	ctx := context.Background()
	if err := a.register(ctx); err != nil {
		log.Fatalf("register failed: %v", err)
	}
	if cfg.RunOnce {
		if err := a.tick(ctx); err != nil {
			log.Fatalf("agent tick failed: %v", err)
		}
		return
	}
	ticker := time.NewTicker(cfg.Interval)
	defer ticker.Stop()
	for {
		if err := a.tick(ctx); err != nil {
			log.Printf("agent tick failed: %v", err)
		}
		<-ticker.C
	}
}

func configFromEnv() config {
	return config{
		ServerURL:          strings.TrimRight(strings.TrimSpace(os.Getenv("MULTIVIEWER_SERVER_URL")), "/"),
		ClientID:           firstNonEmpty(os.Getenv("MULTIVIEWER_AGENT_CLIENT_ID"), "agent-"+hostname()),
		ClientName:         firstNonEmpty(os.Getenv("MULTIVIEWER_AGENT_NAME"), "IP MultiViewer Agent"),
		ClientToken:        strings.TrimSpace(os.Getenv("MULTIVIEWER_CLIENT_TOKEN")),
		Interval:           durationFromEnv("MULTIVIEWER_AGENT_INTERVAL_SEC", 30*time.Second),
		ProbeTimeout:       durationFromEnv("MULTIVIEWER_AGENT_PROBE_TIMEOUT_SEC", 5*time.Second),
		RunOnce:            boolFromEnv("MULTIVIEWER_AGENT_RUN_ONCE"),
		DisableProbing:     boolFromEnv("MULTIVIEWER_AGENT_DISABLE_PROBING"),
		DisableAutoResolve: boolFromEnv("MULTIVIEWER_AGENT_DISABLE_AUTO_RESOLVE"),
	}
}

func newAgent(cfg config) *agent {
	client := cfg.HTTPClient
	if client == nil {
		client = &http.Client{Timeout: cfg.ProbeTimeout + 5*time.Second}
	}
	return &agent{cfg: cfg, httpClient: client}
}

func (a *agent) register(ctx context.Context) error {
	body := contracts.ClientRegistration{
		ClientID: a.cfg.ClientID,
		Name:     a.cfg.ClientName,
		Version:  "dev",
		Mode:     "agent",
		Capabilities: map[string]bool{
			"stream_probe": true,
			"media_decode": false,
		},
	}
	return a.postJSON(ctx, "/clients/register", body, nil)
}

func (a *agent) tick(ctx context.Context) error {
	streams, err := a.fetchStreams(ctx)
	if err != nil {
		return err
	}
	activeAlerts := 0
	if !a.cfg.DisableProbing {
		for _, stream := range streams {
			if !stream.Enabled {
				continue
			}
			if err := probeStream(ctx, a.httpClient, stream.InputURL, a.cfg.ProbeTimeout); err != nil {
				activeAlerts++
				if postErr := a.postProbeAlert(ctx, stream, err); postErr != nil {
					return postErr
				}
				continue
			}
			if !a.cfg.DisableAutoResolve {
				if err := a.resolveRecoveredAlert(ctx, stream.ID, contracts.AlertVideoNoSignal); err != nil {
					return err
				}
			}
		}
	}
	heartbeat := contracts.ClientHeartbeat{
		ClientID:     a.cfg.ClientID,
		Status:       "monitoring",
		ActiveAlerts: activeAlerts,
		Timestamp:    time.Now().UTC(),
	}
	return a.postJSON(ctx, "/clients/heartbeat", heartbeat, nil)
}

func (a *agent) fetchStreams(ctx context.Context) ([]contracts.StreamConfig, error) {
	var streams []contracts.StreamConfig
	if err := a.getJSON(ctx, "/streams", &streams); err != nil {
		return nil, err
	}
	return streams, nil
}

func (a *agent) postProbeAlert(ctx context.Context, stream contracts.StreamConfig, cause error) error {
	message := "stream probe failed"
	if cause != nil {
		message = message + ": " + streamutils.MaskCredentials(cause.Error())
	}
	alert := contracts.Alert{
		ID:        newID(),
		StreamID:  stream.ID,
		Type:      contracts.AlertVideoNoSignal,
		Severity:  contracts.SeverityError,
		Status:    contracts.AlertOpen,
		Message:   message,
		StartedAt: time.Now().UTC(),
		UpdatedAt: time.Now().UTC(),
	}
	return a.postJSON(ctx, "/client-alerts", alert, nil)
}

func (a *agent) resolveRecoveredAlert(ctx context.Context, streamID string, alertType contracts.AlertType) error {
	var alerts []contracts.Alert
	if err := a.getJSON(ctx, "/alerts", &alerts); err != nil {
		return err
	}
	for _, alert := range alerts {
		if alert.StreamID != streamID || alert.Type != alertType || !isActiveAlertStatus(alert.Status) {
			continue
		}
		if err := a.postJSON(ctx, "/alerts/"+url.PathEscape(alert.ID)+"/resolve", nil, nil); err != nil {
			return err
		}
	}
	return nil
}

func isActiveAlertStatus(status contracts.AlertStatus) bool {
	return status == contracts.AlertOpen || status == contracts.AlertAcknowledged || status == contracts.AlertSnoozed
}

func (a *agent) getJSON(ctx context.Context, path string, dest any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, a.cfg.ServerURL+path, nil)
	if err != nil {
		return err
	}
	a.authorize(req)
	res, err := a.httpClient.Do(req)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		return statusError(res)
	}
	return json.NewDecoder(res.Body).Decode(dest)
}

func (a *agent) postJSON(ctx context.Context, path string, body any, dest any) error {
	payload, err := json.Marshal(body)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, a.cfg.ServerURL+path, bytes.NewReader(payload))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	a.authorize(req)
	res, err := a.httpClient.Do(req)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		return statusError(res)
	}
	if dest == nil {
		_, _ = io.Copy(io.Discard, res.Body)
		return nil
	}
	return json.NewDecoder(res.Body).Decode(dest)
}

func (a *agent) authorize(req *http.Request) {
	if a.cfg.ClientToken != "" {
		req.Header.Set("Authorization", "Bearer "+a.cfg.ClientToken)
	}
}

func probeStream(ctx context.Context, client *http.Client, rawURL string, timeout time.Duration) error {
	if err := streamutils.ValidateStreamURL(rawURL); err != nil {
		return err
	}
	parsed, err := url.Parse(rawURL)
	if err != nil {
		return err
	}
	switch strings.ToLower(parsed.Scheme) {
	case "http", "https":
		return probeHTTP(ctx, client, rawURL)
	case "rtsp", "rtmp", "rtmps":
		return probeTCP(ctx, parsed.Host, timeout)
	case "srt", "udp", "rtp":
		return probeUDP(ctx, parsed.Host, timeout)
	default:
		return errors.New("unsupported stream protocol")
	}
}

func probeHTTP(ctx context.Context, client *http.Client, rawURL string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodHead, rawURL, nil)
	if err != nil {
		return err
	}
	res, err := client.Do(req)
	if err != nil {
		return errors.New("http probe failed for " + streamutils.MaskCredentials(rawURL))
	}
	defer res.Body.Close()
	if res.StatusCode >= 200 && res.StatusCode < 500 {
		return nil
	}
	return fmt.Errorf("http probe returned status %d for %s", res.StatusCode, streamutils.MaskCredentials(rawURL))
}

func probeTCP(ctx context.Context, address string, timeout time.Duration) error {
	dialer := net.Dialer{Timeout: timeout}
	conn, err := dialer.DialContext(ctx, "tcp", address)
	if err != nil {
		return errors.New("tcp probe failed for " + address)
	}
	_ = conn.Close()
	return nil
}

func probeUDP(ctx context.Context, address string, timeout time.Duration) error {
	dialer := net.Dialer{Timeout: timeout}
	conn, err := dialer.DialContext(ctx, "udp", address)
	if err != nil {
		return errors.New("udp probe failed for " + address)
	}
	_ = conn.Close()
	return nil
}

func statusError(res *http.Response) error {
	body, _ := io.ReadAll(io.LimitReader(res.Body, 256))
	if len(body) == 0 {
		return fmt.Errorf("server returned status %d", res.StatusCode)
	}
	return fmt.Errorf("server returned status %d: %s", res.StatusCode, strings.TrimSpace(string(body)))
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

func boolFromEnv(name string) bool {
	value := strings.ToLower(strings.TrimSpace(os.Getenv(name)))
	return value == "1" || value == "true" || value == "yes"
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}

func hostname() string {
	name, err := os.Hostname()
	if err != nil || strings.TrimSpace(name) == "" {
		return "unknown"
	}
	return strings.ToLower(strings.TrimSpace(name))
}

func newID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return time.Now().UTC().Format("20060102150405.000000000")
	}
	return hex.EncodeToString(b[:])
}
