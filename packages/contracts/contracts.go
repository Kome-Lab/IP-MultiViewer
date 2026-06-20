package contracts

import "time"

type StreamProtocol string

const (
	ProtocolRTSP      StreamProtocol = "rtsp"
	ProtocolSRT       StreamProtocol = "srt"
	ProtocolRTMP      StreamProtocol = "rtmp"
	ProtocolRTMPS     StreamProtocol = "rtmps"
	ProtocolHLS       StreamProtocol = "hls"
	ProtocolUDPMPEGTS StreamProtocol = "udp_mpegts"
	ProtocolRTP       StreamProtocol = "rtp"
)

type StreamConfig struct {
	ID                  string    `json:"id"`
	Name                string    `json:"name"`
	Enabled             bool      `json:"enabled"`
	InputURL            string    `json:"input_url"`
	InputProtocol       string    `json:"input_protocol"`
	Group               string    `json:"group"`
	Tags                []string  `json:"tags"`
	AudioMonitorEnabled bool      `json:"audio_monitor_enabled"`
	AnalysisEnabled     bool      `json:"analysis_enabled"`
	AlertProfileID      string    `json:"alert_profile_id"`
	CreatedAt           time.Time `json:"created_at"`
	UpdatedAt           time.Time `json:"updated_at"`
}

type LayoutKind string

const (
	Layout2x2    LayoutKind = "2x2"
	Layout3x3    LayoutKind = "3x3"
	Layout4x4    LayoutKind = "4x4"
	LayoutCustom LayoutKind = "custom"
)

type Layout struct {
	ID        string       `json:"id"`
	Name      string       `json:"name"`
	Kind      LayoutKind   `json:"kind"`
	Tiles     []LayoutTile `json:"tiles"`
	CreatedAt time.Time    `json:"created_at"`
	UpdatedAt time.Time    `json:"updated_at"`
}

type LayoutTile struct {
	Index    int    `json:"index"`
	StreamID string `json:"stream_id,omitempty"`
	Row      int    `json:"row"`
	Column   int    `json:"column"`
	RowSpan  int    `json:"row_span"`
	ColSpan  int    `json:"col_span"`
}

type AlertSeverity string

const (
	SeverityInfo     AlertSeverity = "info"
	SeverityWarning  AlertSeverity = "warning"
	SeverityError    AlertSeverity = "error"
	SeverityCritical AlertSeverity = "critical"
)

type AlertStatus string

const (
	AlertOpen         AlertStatus = "open"
	AlertAcknowledged AlertStatus = "acknowledged"
	AlertSnoozed      AlertStatus = "snoozed"
	AlertResolved     AlertStatus = "resolved"
	AlertIgnored      AlertStatus = "ignored"
)

type AlertType string

const (
	AlertVideoBlack               AlertType = "video.black"
	AlertVideoFreeze              AlertType = "video.freeze"
	AlertVideoNoSignal            AlertType = "video.no_signal"
	AlertVideoLowFPS              AlertType = "video.low_fps"
	AlertVideoBitrateLow          AlertType = "video.bitrate_low"
	AlertVideoDecodeError         AlertType = "video.decode_error"
	AlertVideoResolutionChanged   AlertType = "video.resolution_changed"
	AlertVideoNoiseDetected       AlertType = "video.noise_detected"
	AlertAudioSilence             AlertType = "audio.silence"
	AlertAudioLowLevel            AlertType = "audio.low_level"
	AlertAudioHighLevel           AlertType = "audio.high_level"
	AlertAudioClipping            AlertType = "audio.clipping"
	AlertAudioNoiseDetected       AlertType = "audio.noise_detected"
	AlertAudioChannelMissing      AlertType = "audio.channel_missing"
	AlertAudioDropout             AlertType = "audio.dropout"
	AlertTransportPacketLossHigh  AlertType = "transport.packet_loss_high"
	AlertTransportJitterHigh      AlertType = "transport.jitter_high"
	AlertTransportRTTHigh         AlertType = "transport.rtt_high"
	AlertTransportReconnectLoop   AlertType = "transport.reconnect_loop"
	AlertTransportInputTimeout    AlertType = "transport.input_timeout"
	AlertTransportBitrateUnstable AlertType = "transport.bitrate_unstable"
	AlertSystemCPUHigh            AlertType = "system.cpu_high"
	AlertSystemMemoryHigh         AlertType = "system.memory_high"
	AlertSystemDiskLow            AlertType = "system.disk_low"
)

type AlertProfile struct {
	ID                     string    `json:"id"`
	Name                   string    `json:"name"`
	BlackThreshold         float64   `json:"black_threshold"`
	MinBlackDurationSec    float64   `json:"min_black_duration_sec"`
	FreezeThreshold        float64   `json:"freeze_difference_threshold"`
	MinFreezeDurationSec   float64   `json:"min_freeze_duration_sec"`
	InputTimeoutSec        float64   `json:"input_timeout_sec"`
	SilenceThresholdDB     float64   `json:"silence_threshold_db"`
	MinSilenceDurationSec  float64   `json:"min_silence_duration_sec"`
	ClippingThresholdDB    float64   `json:"clipping_threshold_db"`
	ClippingCountThreshold int       `json:"clipping_count_threshold"`
	CreatedAt              time.Time `json:"created_at"`
	UpdatedAt              time.Time `json:"updated_at"`
}

type Alert struct {
	ID             string        `json:"id"`
	StreamID       string        `json:"stream_id"`
	Type           AlertType     `json:"type"`
	Severity       AlertSeverity `json:"severity"`
	Status         AlertStatus   `json:"status"`
	Message        string        `json:"message"`
	StartedAt      time.Time     `json:"started_at"`
	UpdatedAt      time.Time     `json:"updated_at"`
	SnoozedUntil   time.Time     `json:"snoozed_until,omitempty"`
	SourceClientID string        `json:"source_client_id,omitempty"`
}

type ClientRegistration struct {
	ClientID     string          `json:"client_id"`
	Name         string          `json:"name"`
	Version      string          `json:"version"`
	Mode         string          `json:"mode"`
	Capabilities map[string]bool `json:"capabilities"`
}

type ClientHeartbeat struct {
	ClientID       string    `json:"client_id"`
	ActiveLayoutID string    `json:"active_layout_id,omitempty"`
	Status         string    `json:"status"`
	ActiveAlerts   int       `json:"active_alerts"`
	Timestamp      time.Time `json:"timestamp"`
}

type ClientStatus struct {
	ClientID        string          `json:"client_id"`
	Name            string          `json:"name,omitempty"`
	Version         string          `json:"version,omitempty"`
	Mode            string          `json:"mode,omitempty"`
	Capabilities    map[string]bool `json:"capabilities,omitempty"`
	Status          string          `json:"status"`
	ActiveLayoutID  string          `json:"active_layout_id,omitempty"`
	ActiveAlerts    int             `json:"active_alerts"`
	LastHeartbeatAt time.Time       `json:"last_heartbeat_at,omitempty"`
	HeartbeatAgeSec int64           `json:"heartbeat_age_sec"`
	Online          bool            `json:"online"`
	Registered      bool            `json:"registered"`
}

type NotificationChannelType string

const (
	NotificationDiscord NotificationChannelType = "discord"
	NotificationSlack   NotificationChannelType = "slack"
	NotificationGeneric NotificationChannelType = "generic"
)

type NotificationChannel struct {
	ID               string                  `json:"id"`
	Name             string                  `json:"name"`
	Type             NotificationChannelType `json:"type"`
	TargetURL        string                  `json:"target_url,omitempty"`
	TargetMasked     string                  `json:"target_masked"`
	TargetConfigured bool                    `json:"target_configured"`
	SeverityFilters  []AlertSeverity         `json:"severity_filters"`
	EventFilters     []string                `json:"event_type_filters"`
	Enabled          bool                    `json:"enabled"`
	CreatedAt        time.Time               `json:"created_at"`
	UpdatedAt        time.Time               `json:"updated_at"`
}

type NotificationDelivery struct {
	ID           string    `json:"id"`
	ChannelID    string    `json:"channel_id"`
	EventType    string    `json:"event_type"`
	Status       string    `json:"status"`
	TargetMasked string    `json:"target_masked"`
	Attempts     int       `json:"attempts"`
	LastError    string    `json:"last_error,omitempty"`
	CreatedAt    time.Time `json:"created_at"`
	UpdatedAt    time.Time `json:"updated_at"`
}

type ErrorResponse struct {
	RequestID string `json:"request_id"`
	Code      string `json:"code"`
	Message   string `json:"message"`
}
