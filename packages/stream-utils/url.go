package streamutils

import (
	"errors"
	"net/url"
	"strings"
)

var allowedSchemes = map[string]bool{"rtsp": true, "rtmp": true, "rtmps": true, "http": true, "https": true, "srt": true, "udp": true, "rtp": true}
var sensitiveQueryKeys = map[string]bool{
	"access_token": true,
	"auth":         true,
	"key":          true,
	"passphrase":   true,
	"password":     true,
	"secret":       true,
	"token":        true,
}

func ValidateStreamURL(raw string) error {
	u, err := url.Parse(raw)
	if err != nil || u.Scheme == "" || u.Host == "" {
		return errors.New("invalid stream url")
	}
	if !allowedSchemes[strings.ToLower(u.Scheme)] {
		return errors.New("unsupported stream protocol")
	}
	return nil
}

func MaskCredentials(raw string) string {
	u, err := url.Parse(raw)
	if err != nil || u.User == nil {
		return maskSensitiveQuery(raw)
	}
	user := u.User.Username()
	if _, ok := u.User.Password(); ok {
		u.User = url.UserPassword(user, "MASKED")
		return maskSensitiveQuery(strings.Replace(u.String(), ":MASKED@", ":****@", 1))
	}
	u.User = url.User("MASKED")
	return maskSensitiveQuery(strings.Replace(u.String(), "MASKED@", "****@", 1))
}

func maskSensitiveQuery(raw string) string {
	u, err := url.Parse(raw)
	if err != nil {
		return raw
	}
	q := u.Query()
	changed := false
	for key := range q {
		if sensitiveQueryKeys[strings.ToLower(key)] {
			q.Set(key, "<REDACTED>")
			changed = true
		}
	}
	if !changed {
		return raw
	}
	u.RawQuery = q.Encode()
	return u.String()
}
