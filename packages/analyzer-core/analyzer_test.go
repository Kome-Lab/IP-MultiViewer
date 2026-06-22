package analyzercore

import (
	"testing"
	"time"
)

func TestVideoAudioRules(t *testing.T) {
	if !IsBlack(FrameStats{AverageLuma: 0.01}, 0.05) {
		t.Fatal("expected black frame")
	}
	if !IsFrozen(FrameStats{Difference: 0.001}, 0.01) {
		t.Fatal("expected frozen frame")
	}
	if !IsSilent(AudioStats{LevelDB: -60}, -50) {
		t.Fatal("expected silence")
	}
	if !IsClipping(AudioStats{ClippingCount: 10}, 10) {
		t.Fatal("expected clipping")
	}
}

func TestBlackAlertIsImmediateAndDeduplicated(t *testing.T) {
	cfg := DefaultConfig()
	cfg.MinBlackDuration = 30 * time.Second
	now := time.Unix(100, 0)
	state := &DurationState{}
	got := EvaluateVideo(now, FrameStats{AverageLuma: 0.01, Difference: 1}, cfg, state)
	if len(got) != 1 || got[0].Type != "video.black" {
		t.Fatalf("expected immediate black alert: %#v", got)
	}
	got = EvaluateVideo(now.Add(time.Second), FrameStats{AverageLuma: 0.01, Difference: 1}, cfg, state)
	if len(got) != 0 {
		t.Fatalf("open black alert should dedupe: %#v", got)
	}
}

func TestDurationBasedFreezeAlertDeduplication(t *testing.T) {
	cfg := DefaultConfig()
	now := time.Unix(100, 0)
	state := &DurationState{}
	if got := EvaluateVideo(now, FrameStats{AverageLuma: 1, Difference: 0.001}, cfg, state); len(got) != 0 {
		t.Fatalf("first frozen frame should not alert: %#v", got)
	}
	got := EvaluateVideo(now.Add(6*time.Second), FrameStats{AverageLuma: 1, Difference: 0.001}, cfg, state)
	if len(got) != 1 || got[0].Type != "video.freeze" {
		t.Fatalf("expected freeze alert: %#v", got)
	}
	got = EvaluateVideo(now.Add(7*time.Second), FrameStats{AverageLuma: 1, Difference: 0.001}, cfg, state)
	if len(got) != 0 {
		t.Fatalf("open freeze alert should dedupe: %#v", got)
	}
}

func TestVideoImmediateThresholdAlerts(t *testing.T) {
	cfg := DefaultConfig()
	now := time.Unix(100, 0)
	state := &DurationState{}
	got := EvaluateVideo(now, FrameStats{AverageLuma: 1, Difference: 1, FPS: 10, BitrateKbps: 300}, cfg, state)
	if len(got) != 2 {
		t.Fatalf("expected low fps and bitrate alerts: %#v", got)
	}
	if got[0].Type != "video.low_fps" || got[1].Type != "video.bitrate_low" {
		t.Fatalf("unexpected video alerts: %#v", got)
	}
	if again := EvaluateVideo(now.Add(time.Second), FrameStats{AverageLuma: 1, Difference: 1, FPS: 10, BitrateKbps: 300}, cfg, state); len(again) != 0 {
		t.Fatalf("open threshold alerts should dedupe: %#v", again)
	}
	if reset := EvaluateVideo(now.Add(2*time.Second), FrameStats{AverageLuma: 1, Difference: 1, FPS: 30, BitrateKbps: 2000}, cfg, state); len(reset) != 0 {
		t.Fatalf("normal video stats should not alert: %#v", reset)
	}
	if got := EvaluateVideo(now.Add(3*time.Second), FrameStats{AverageLuma: 1, Difference: 1, FPS: 10, BitrateKbps: 2000}, cfg, state); len(got) != 1 || got[0].Type != "video.low_fps" {
		t.Fatalf("expected low fps after reset: %#v", got)
	}
}

func TestAudioLevelAndNoiseAlerts(t *testing.T) {
	cfg := DefaultConfig()
	now := time.Unix(100, 0)
	state := &DurationState{}
	got := EvaluateAudio(now, AudioStats{LevelDB: -46}, cfg, state)
	if len(got) != 1 || got[0].Type != "audio.low_level" {
		t.Fatalf("expected low level alert: %#v", got)
	}
	got = EvaluateAudio(now.Add(time.Second), AudioStats{LevelDB: -1, NoiseScore: 0.9}, cfg, state)
	if len(got) != 2 || got[0].Type != "audio.high_level" || got[1].Type != "audio.noise_detected" {
		t.Fatalf("expected high level and noise alerts: %#v", got)
	}
}

func TestTransportAlerts(t *testing.T) {
	cfg := DefaultConfig()
	now := time.Unix(100, 0)
	state := &DurationState{}
	got := EvaluateTransport(now, TransportStats{PacketLossPercent: 7, JitterMS: 90, RTTMS: 400, ReconnectCount: 3, BitrateKbps: 300}, cfg, state)
	if len(got) != 5 {
		t.Fatalf("expected transport threshold alerts: %#v", got)
	}
	want := []string{"transport.packet_loss_high", "transport.jitter_high", "transport.rtt_high", "transport.reconnect_loop", "transport.bitrate_unstable"}
	for i, typ := range want {
		if got[i].Type != typ {
			t.Fatalf("alert %d = %s, want %s: %#v", i, got[i].Type, typ, got)
		}
	}
	if again := EvaluateTransport(now.Add(time.Second), TransportStats{PacketLossPercent: 7, JitterMS: 90, RTTMS: 400, ReconnectCount: 3, BitrateKbps: 300}, cfg, state); len(again) != 0 {
		t.Fatalf("open transport alerts should dedupe: %#v", again)
	}
}

func TestNoSignalAlert(t *testing.T) {
	cfg := DefaultConfig()
	now := time.Unix(100, 0)
	state := &DurationState{LastInputAt: now}
	got := EvaluateNoSignal(now.Add(6*time.Second), cfg, state)
	if len(got) != 1 || got[0].Type != "video.no_signal" {
		t.Fatalf("expected no signal alert: %#v", got)
	}
}
