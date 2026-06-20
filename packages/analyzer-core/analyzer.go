package analyzercore

import "time"

type FrameStats struct {
	AverageLuma float64
	Difference  float64
	FPS         float64
	BitrateKbps float64
}

type AudioStats struct {
	LevelDB       float64
	ClippingCount int
	NoiseScore    float64
}

type TransportStats struct {
	PacketLossPercent float64
	JitterMS          float64
	RTTMS             float64
	ReconnectCount    int
	BitrateKbps       float64
}

func IsBlack(s FrameStats, threshold float64) bool {
	return s.AverageLuma <= threshold
}

func IsFrozen(s FrameStats, threshold float64) bool {
	return s.Difference <= threshold
}

func IsSilent(s AudioStats, thresholdDB float64) bool {
	return s.LevelDB <= thresholdDB
}

func IsClipping(s AudioStats, thresholdCount int) bool {
	return s.ClippingCount >= thresholdCount
}

type DetectionConfig struct {
	BlackThreshold         float64
	MinBlackDuration       time.Duration
	FreezeThreshold        float64
	MinFreezeDuration      time.Duration
	InputTimeout           time.Duration
	SilenceThresholdDB     float64
	MinSilenceDuration     time.Duration
	LowLevelThresholdDB    float64
	HighLevelThresholdDB   float64
	ClippingCountThreshold int
	NoiseScoreThreshold    float64
	LowFPSThreshold        float64
	BitrateLowKbps         float64
	PacketLossHighPercent  float64
	JitterHighMS           float64
	RTTHighMS              float64
	ReconnectLoopThreshold int
}

func DefaultConfig() DetectionConfig {
	return DetectionConfig{
		BlackThreshold:         0.05,
		MinBlackDuration:       3 * time.Second,
		FreezeThreshold:        0.01,
		MinFreezeDuration:      5 * time.Second,
		InputTimeout:           5 * time.Second,
		SilenceThresholdDB:     -50,
		MinSilenceDuration:     5 * time.Second,
		LowLevelThresholdDB:    -45,
		HighLevelThresholdDB:   -3,
		ClippingCountThreshold: 10,
		NoiseScoreThreshold:    0.85,
		LowFPSThreshold:        15,
		BitrateLowKbps:         500,
		PacketLossHighPercent:  5,
		JitterHighMS:           80,
		RTTHighMS:              300,
		ReconnectLoopThreshold: 3,
	}
}

type DurationState struct {
	BlackSince   time.Time
	FreezeSince  time.Time
	SilenceSince time.Time
	LastInputAt  time.Time
	OpenAlerts   map[string]bool
}

type DetectionAlert struct {
	Type       string
	Open       bool
	Duration   time.Duration
	OccurredAt time.Time
}

func EvaluateVideo(now time.Time, stats FrameStats, cfg DetectionConfig, state *DurationState) []DetectionAlert {
	if state.OpenAlerts == nil {
		state.OpenAlerts = map[string]bool{}
	}
	var alerts []DetectionAlert
	if IsBlack(stats, cfg.BlackThreshold) {
		alerts = appendDurationAlert(alerts, state, "video.black", now, &state.BlackSince, cfg.MinBlackDuration)
	} else {
		state.BlackSince = time.Time{}
		state.OpenAlerts["video.black"] = false
	}
	if IsFrozen(stats, cfg.FreezeThreshold) {
		alerts = appendDurationAlert(alerts, state, "video.freeze", now, &state.FreezeSince, cfg.MinFreezeDuration)
	} else {
		state.FreezeSince = time.Time{}
		state.OpenAlerts["video.freeze"] = false
	}
	if cfg.LowFPSThreshold > 0 && stats.FPS > 0 && stats.FPS < cfg.LowFPSThreshold {
		alerts = appendImmediateAlert(alerts, state, "video.low_fps", now)
	} else {
		state.OpenAlerts["video.low_fps"] = false
	}
	if cfg.BitrateLowKbps > 0 && stats.BitrateKbps > 0 && stats.BitrateKbps < cfg.BitrateLowKbps {
		alerts = appendImmediateAlert(alerts, state, "video.bitrate_low", now)
	} else {
		state.OpenAlerts["video.bitrate_low"] = false
	}
	state.LastInputAt = now
	return alerts
}

func EvaluateAudio(now time.Time, stats AudioStats, cfg DetectionConfig, state *DurationState) []DetectionAlert {
	if state.OpenAlerts == nil {
		state.OpenAlerts = map[string]bool{}
	}
	var alerts []DetectionAlert
	if IsSilent(stats, cfg.SilenceThresholdDB) {
		alerts = appendDurationAlert(alerts, state, "audio.silence", now, &state.SilenceSince, cfg.MinSilenceDuration)
	} else {
		state.SilenceSince = time.Time{}
		state.OpenAlerts["audio.silence"] = false
	}
	if IsClipping(stats, cfg.ClippingCountThreshold) && !state.OpenAlerts["audio.clipping"] {
		alerts = appendImmediateAlert(alerts, state, "audio.clipping", now)
	} else if !IsClipping(stats, cfg.ClippingCountThreshold) {
		state.OpenAlerts["audio.clipping"] = false
	}
	if cfg.LowLevelThresholdDB != 0 && stats.LevelDB <= cfg.LowLevelThresholdDB && !IsSilent(stats, cfg.SilenceThresholdDB) {
		alerts = appendImmediateAlert(alerts, state, "audio.low_level", now)
	} else {
		state.OpenAlerts["audio.low_level"] = false
	}
	if cfg.HighLevelThresholdDB != 0 && stats.LevelDB >= cfg.HighLevelThresholdDB {
		alerts = appendImmediateAlert(alerts, state, "audio.high_level", now)
	} else {
		state.OpenAlerts["audio.high_level"] = false
	}
	if cfg.NoiseScoreThreshold > 0 && stats.NoiseScore >= cfg.NoiseScoreThreshold {
		alerts = appendImmediateAlert(alerts, state, "audio.noise_detected", now)
	} else {
		state.OpenAlerts["audio.noise_detected"] = false
	}
	return alerts
}

func EvaluateTransport(now time.Time, stats TransportStats, cfg DetectionConfig, state *DurationState) []DetectionAlert {
	if state.OpenAlerts == nil {
		state.OpenAlerts = map[string]bool{}
	}
	var alerts []DetectionAlert
	if cfg.PacketLossHighPercent > 0 && stats.PacketLossPercent >= cfg.PacketLossHighPercent {
		alerts = appendImmediateAlert(alerts, state, "transport.packet_loss_high", now)
	} else {
		state.OpenAlerts["transport.packet_loss_high"] = false
	}
	if cfg.JitterHighMS > 0 && stats.JitterMS >= cfg.JitterHighMS {
		alerts = appendImmediateAlert(alerts, state, "transport.jitter_high", now)
	} else {
		state.OpenAlerts["transport.jitter_high"] = false
	}
	if cfg.RTTHighMS > 0 && stats.RTTMS >= cfg.RTTHighMS {
		alerts = appendImmediateAlert(alerts, state, "transport.rtt_high", now)
	} else {
		state.OpenAlerts["transport.rtt_high"] = false
	}
	if cfg.ReconnectLoopThreshold > 0 && stats.ReconnectCount >= cfg.ReconnectLoopThreshold {
		alerts = appendImmediateAlert(alerts, state, "transport.reconnect_loop", now)
	} else {
		state.OpenAlerts["transport.reconnect_loop"] = false
	}
	if cfg.BitrateLowKbps > 0 && stats.BitrateKbps > 0 && stats.BitrateKbps < cfg.BitrateLowKbps {
		alerts = appendImmediateAlert(alerts, state, "transport.bitrate_unstable", now)
	} else {
		state.OpenAlerts["transport.bitrate_unstable"] = false
	}
	return alerts
}

func EvaluateNoSignal(now time.Time, cfg DetectionConfig, state *DurationState) []DetectionAlert {
	if state.OpenAlerts == nil {
		state.OpenAlerts = map[string]bool{}
	}
	if state.LastInputAt.IsZero() || now.Sub(state.LastInputAt) < cfg.InputTimeout || state.OpenAlerts["video.no_signal"] {
		return nil
	}
	state.OpenAlerts["video.no_signal"] = true
	return []DetectionAlert{{Type: "video.no_signal", Open: true, Duration: now.Sub(state.LastInputAt), OccurredAt: now}}
}

func appendDurationAlert(alerts []DetectionAlert, state *DurationState, typ string, now time.Time, since *time.Time, min time.Duration) []DetectionAlert {
	if since.IsZero() {
		*since = now
		return alerts
	}
	duration := now.Sub(*since)
	if duration >= min && !state.OpenAlerts[typ] {
		state.OpenAlerts[typ] = true
		alerts = append(alerts, DetectionAlert{Type: typ, Open: true, Duration: duration, OccurredAt: now})
	}
	return alerts
}

func appendImmediateAlert(alerts []DetectionAlert, state *DurationState, typ string, now time.Time) []DetectionAlert {
	if state.OpenAlerts[typ] {
		return alerts
	}
	state.OpenAlerts[typ] = true
	return append(alerts, DetectionAlert{Type: typ, Open: true, OccurredAt: now})
}
