package contracts

import "testing"

func TestWireConstants(t *testing.T) {
	if ProtocolRTSP != "rtsp" || ProtocolRTMP != "rtmp" || ProtocolHLS != "hls" {
		t.Fatal("protocol wire values changed")
	}
	if Layout2x2 != "2x2" || LayoutCustom != "custom" {
		t.Fatal("layout wire values changed")
	}
	if AlertOpen != "open" || AlertSnoozed != "snoozed" || AlertResolved != "resolved" {
		t.Fatal("alert status wire values changed")
	}
	if AlertVideoBlack != "video.black" || AlertAudioSilence != "audio.silence" || AlertTransportPacketLossHigh != "transport.packet_loss_high" {
		t.Fatal("alert type wire values changed")
	}
	if NotificationDiscord != "discord" || NotificationGeneric != "generic" {
		t.Fatal("notification channel type wire values changed")
	}
}
