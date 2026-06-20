package notificationcore

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

type Alert struct {
	Severity    string
	StreamName  string
	Type        string
	DurationSec float64
	Status      string
}

type WebhookRequest struct {
	URL         string
	ChannelType string
	Text        string
}

func FormatJapanese(a Alert) string {
	return fmt.Sprintf("%s: %s \u3092\u691c\u77e5\u3057\u307e\u3057\u305f\nStream: %s\nAlert: %s\nDuration: %.1f sec\nStatus: %s", strings.ToUpper(a.Severity), a.Type, a.StreamName, a.Type, a.DurationSec, a.Status)
}

func BuildWebhookPayload(channelType string, text string) ([]byte, string, error) {
	field := "text"
	switch strings.ToLower(strings.TrimSpace(channelType)) {
	case "discord":
		field = "content"
	case "slack", "generic", "":
		field = "text"
	default:
		field = "text"
	}
	body, err := json.Marshal(map[string]string{field: text})
	if err != nil {
		return nil, "", err
	}
	return body, "application/json", nil
}

func DeliverWebhook(ctx context.Context, client *http.Client, request WebhookRequest) error {
	if client == nil {
		client = &http.Client{Timeout: 10 * time.Second}
	}
	body, contentType, err := BuildWebhookPayload(request.ChannelType, request.Text)
	if err != nil {
		return err
	}
	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, request.URL, bytes.NewReader(body))
	if err != nil {
		return err
	}
	httpReq.Header.Set("Content-Type", contentType)
	res, err := client.Do(httpReq)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode >= 200 && res.StatusCode < 300 {
		_, _ = io.Copy(io.Discard, res.Body)
		return nil
	}
	limited, _ := io.ReadAll(io.LimitReader(res.Body, 256))
	if len(limited) == 0 {
		return fmt.Errorf("webhook returned status %d", res.StatusCode)
	}
	return fmt.Errorf("webhook returned status %d: %s", res.StatusCode, strings.TrimSpace(string(limited)))
}
