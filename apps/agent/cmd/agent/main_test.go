package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/example/ip-multiviewer/packages/contracts"
)

func TestAgentRegistersHeartbeatsAndUploadsProbeAlert(t *testing.T) {
	var registered bool
	var heartbeat contracts.ClientHeartbeat
	var alert contracts.Alert
	var authHeaders []string

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		authHeaders = append(authHeaders, r.Header.Get("Authorization"))
		switch r.URL.Path {
		case "/clients/register":
			registered = true
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(`{"client_id":"agent-01"}`))
		case "/streams":
			writeJSON(w, []contracts.StreamConfig{
				{ID: "cam-01", Name: "Camera 01", Enabled: true, InputURL: "rtsp://127.0.0.1:1/live?password=super-secret", InputProtocol: "rtsp"},
			})
		case "/client-alerts":
			if err := json.NewDecoder(r.Body).Decode(&alert); err != nil {
				t.Fatalf("decode alert: %v", err)
			}
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(`{"status":"accepted"}`))
		case "/clients/heartbeat":
			if err := json.NewDecoder(r.Body).Decode(&heartbeat); err != nil {
				t.Fatalf("decode heartbeat: %v", err)
			}
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(`{"status":"accepted"}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	a := newAgent(config{
		ServerURL:    server.URL,
		ClientID:     "agent-01",
		ClientName:   "Agent 01",
		ClientToken:  "client-token",
		ProbeTimeout: 50 * time.Millisecond,
		HTTPClient:   server.Client(),
	})
	if err := a.register(t.Context()); err != nil {
		t.Fatalf("register: %v", err)
	}
	if err := a.tick(t.Context()); err != nil {
		t.Fatalf("tick: %v", err)
	}
	if !registered {
		t.Fatal("agent did not register")
	}
	if heartbeat.ClientID != "agent-01" || heartbeat.ActiveAlerts != 1 {
		t.Fatalf("bad heartbeat: %#v", heartbeat)
	}
	if alert.StreamID != "cam-01" || alert.Type != contracts.AlertVideoNoSignal {
		t.Fatalf("bad alert: %#v", alert)
	}
	if strings.Contains(alert.Message, "secret") || strings.Contains(alert.Message, "password=super-secret") {
		t.Fatalf("alert leaked stream credentials: %s", alert.Message)
	}
	for _, header := range authHeaders {
		if header != "Bearer client-token" {
			t.Fatalf("missing bearer token: %#v", authHeaders)
		}
	}
}

func TestAgentCanRunHeartbeatWithoutProbing(t *testing.T) {
	var alertPosted bool
	var heartbeat contracts.ClientHeartbeat
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/streams":
			writeJSON(w, []contracts.StreamConfig{
				{ID: "cam-01", Name: "Camera 01", Enabled: true, InputURL: "rtsp://127.0.0.1:1/live", InputProtocol: "rtsp"},
			})
		case "/client-alerts":
			alertPosted = true
			w.WriteHeader(http.StatusAccepted)
		case "/clients/heartbeat":
			if err := json.NewDecoder(r.Body).Decode(&heartbeat); err != nil {
				t.Fatalf("decode heartbeat: %v", err)
			}
			w.WriteHeader(http.StatusAccepted)
		default:
			w.WriteHeader(http.StatusAccepted)
		}
	}))
	defer server.Close()

	a := newAgent(config{
		ServerURL:      server.URL,
		ClientID:       "agent-01",
		ProbeTimeout:   50 * time.Millisecond,
		DisableProbing: true,
		HTTPClient:     server.Client(),
	})
	if err := a.tick(t.Context()); err != nil {
		t.Fatalf("tick: %v", err)
	}
	if alertPosted {
		t.Fatal("alert should not be posted when probing is disabled")
	}
	if heartbeat.ActiveAlerts != 0 {
		t.Fatalf("bad heartbeat: %#v", heartbeat)
	}
}

func TestAgentResolvesNoSignalAfterProbeRecovery(t *testing.T) {
	var resolveCalled bool
	var alertPosted bool
	var heartbeat contracts.ClientHeartbeat
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/streams":
			writeJSON(w, []contracts.StreamConfig{
				{ID: "cam-01", Name: "Camera 01", Enabled: true, InputURL: "http://" + r.Host + "/live.m3u8", InputProtocol: "hls"},
			})
		case r.URL.Path == "/live.m3u8":
			w.WriteHeader(http.StatusOK)
		case r.URL.Path == "/alerts":
			writeJSON(w, []contracts.Alert{
				{ID: "alert-01", StreamID: "cam-01", Type: contracts.AlertVideoNoSignal, Severity: contracts.SeverityError, Status: contracts.AlertOpen},
				{ID: "alert-02", StreamID: "cam-02", Type: contracts.AlertVideoNoSignal, Severity: contracts.SeverityError, Status: contracts.AlertOpen},
				{ID: "alert-03", StreamID: "cam-01", Type: contracts.AlertVideoLowFPS, Severity: contracts.SeverityWarning, Status: contracts.AlertOpen},
			})
		case r.URL.Path == "/alerts/alert-01/resolve":
			resolveCalled = true
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write([]byte(`{"status":"resolved"}`))
		case r.URL.Path == "/client-alerts":
			alertPosted = true
			w.WriteHeader(http.StatusAccepted)
		case r.URL.Path == "/clients/heartbeat":
			if err := json.NewDecoder(r.Body).Decode(&heartbeat); err != nil {
				t.Fatalf("decode heartbeat: %v", err)
			}
			w.WriteHeader(http.StatusAccepted)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	a := newAgent(config{
		ServerURL:    server.URL,
		ClientID:     "agent-01",
		ProbeTimeout: time.Second,
		HTTPClient:   server.Client(),
	})
	if err := a.tick(t.Context()); err != nil {
		t.Fatalf("tick: %v", err)
	}
	if alertPosted {
		t.Fatal("alert should not be posted after successful probe")
	}
	if !resolveCalled {
		t.Fatal("active no-signal alert was not resolved after recovery")
	}
	if heartbeat.ActiveAlerts != 0 {
		t.Fatalf("bad heartbeat: %#v", heartbeat)
	}
}

func TestAgentCanDisableAutoResolve(t *testing.T) {
	var resolveCalled bool
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/streams":
			writeJSON(w, []contracts.StreamConfig{
				{ID: "cam-01", Name: "Camera 01", Enabled: true, InputURL: "http://" + r.Host + "/live.m3u8", InputProtocol: "hls"},
			})
		case "/live.m3u8":
			w.WriteHeader(http.StatusOK)
		case "/alerts/alert-01/resolve":
			resolveCalled = true
			w.WriteHeader(http.StatusOK)
		case "/clients/heartbeat":
			w.WriteHeader(http.StatusAccepted)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	a := newAgent(config{
		ServerURL:          server.URL,
		ClientID:           "agent-01",
		ProbeTimeout:       time.Second,
		DisableAutoResolve: true,
		HTTPClient:         server.Client(),
	})
	if err := a.tick(t.Context()); err != nil {
		t.Fatalf("tick: %v", err)
	}
	if resolveCalled {
		t.Fatal("auto resolve should be disabled")
	}
}

func TestProbeHTTPAcceptsReachableStream(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))
	defer server.Close()
	if err := probeStream(t.Context(), server.Client(), server.URL+"/live.m3u8", time.Second); err != nil {
		t.Fatalf("probe stream: %v", err)
	}
}

func writeJSON(w http.ResponseWriter, value any) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(value)
}
