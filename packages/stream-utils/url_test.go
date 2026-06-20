package streamutils

import "testing"

func TestValidateStreamURL(t *testing.T) {
	if err := ValidateStreamURL("rtsp://camera.example.com/live"); err != nil {
		t.Fatal(err)
	}
	if err := ValidateStreamURL("HLS://camera.example.com/live.m3u8"); err == nil {
		t.Fatal("hls pseudo scheme is not a fetchable stream URL and must be rejected")
	}
	if err := ValidateStreamURL("https://camera.example.com/live.m3u8"); err != nil {
		t.Fatal(err)
	}
	if err := ValidateStreamURL("file:///etc/passwd"); err == nil {
		t.Fatal("file scheme must be rejected")
	}
}

func TestMaskCredentials(t *testing.T) {
	got := MaskCredentials("rtsp://user@camera.example.com/live?password=super-secret")
	if got != "rtsp://%2A%2A%2A%2A@camera.example.com/live?password=%3CREDACTED%3E" {
		t.Fatalf("unexpected mask: %s", got)
	}
	got = MaskCredentials("rtsp://user@camera.example.com/live")
	if got != "rtsp://****@camera.example.com/live" {
		t.Fatalf("username-only credential was not masked: %s", got)
	}
	got = MaskCredentials("srt://source.example.com:9000?mode=caller&passphrase=super-secret&token=abc")
	if got == "srt://source.example.com:9000?mode=caller&passphrase=super-secret&token=abc" || got == "" {
		t.Fatalf("sensitive query parameters were not masked: %s", got)
	}
	if got != "srt://source.example.com:9000?mode=caller&passphrase=%3CREDACTED%3E&token=%3CREDACTED%3E" {
		t.Fatalf("unexpected query mask: %s", got)
	}
}
