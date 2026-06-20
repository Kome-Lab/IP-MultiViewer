package notificationcore

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestFormatJapanese(t *testing.T) {
	msg := FormatJapanese(Alert{Severity: "ERROR", StreamName: "Camera 01", Type: "video.black", DurationSec: 3.5, Status: "open"})
	if !strings.Contains(msg, "Camera 01") || !strings.Contains(msg, "video.black") {
		t.Fatalf("bad message: %s", msg)
	}
	if !strings.Contains(msg, "\u3092\u691c\u77e5\u3057\u307e\u3057\u305f") {
		t.Fatalf("message is not Japanese: %s", msg)
	}
}

func TestBuildWebhookPayload(t *testing.T) {
	body, contentType, err := BuildWebhookPayload("discord", "hello")
	if err != nil {
		t.Fatalf("build payload: %v", err)
	}
	if contentType != "application/json" || !strings.Contains(string(body), `"content":"hello"`) {
		t.Fatalf("bad discord payload: %s %s", contentType, string(body))
	}
}

func TestDeliverWebhook(t *testing.T) {
	var gotBody string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		gotBody = string(body)
		w.WriteHeader(http.StatusNoContent)
	}))
	defer server.Close()

	err := DeliverWebhook(context.Background(), server.Client(), WebhookRequest{
		URL:         server.URL,
		ChannelType: "generic",
		Text:        "hello",
	})
	if err != nil {
		t.Fatalf("deliver webhook: %v", err)
	}
	if !strings.Contains(gotBody, `"text":"hello"`) {
		t.Fatalf("bad webhook body: %s", gotBody)
	}
}
