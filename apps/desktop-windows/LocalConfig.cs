using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPMultiViewer.Desktop;

public sealed class LocalConfig
{
    public ObservableCollection<StreamConfig> Streams { get; set; } = [];
    public ObservableCollection<LayoutConfig> Layouts { get; set; } = [];
    public ObservableCollection<AlertProfileConfig> AlertProfiles { get; set; } = [];
    public ObservableCollection<AlertRecord> AlertHistory { get; set; } = [];
    public string ActiveLayoutId { get; set; } = "layout-2x2";
    public bool GlobalMute { get; set; }
    public bool AlertSoundEnabled { get; set; } = true;
    public int AlertSnoozeMinutes { get; set; } = 10;
    public bool ServerSyncEnabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientVersion { get; set; } = "0.1.0";
    public string ClientToken { get; set; } = "";
    public string ServerUrl { get; set; } = "";
}

public sealed class StreamConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string InputUrl { get; set; } = "";
    public string InputProtocol { get; set; } = "rtsp";
    public string Group { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public bool AudioMonitorEnabled { get; set; } = true;
    public bool AnalysisEnabled { get; set; } = true;
    public bool AlertMuted { get; set; }
    public string AlertProfileId { get; set; } = "default";
}

public sealed class LayoutConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "2x2";
    public ObservableCollection<LayoutTileConfig> Tiles { get; set; } = [];
}

public sealed class LayoutTileConfig
{
    public int Index { get; set; }
    public string StreamId { get; set; } = "";
    public int Row { get; set; }
    public int Column { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
}

public sealed class AlertProfileConfig
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Default";
    public double BlackThreshold { get; set; } = 0.04;
    public double BlackPixelThreshold { get; set; } = 0.04;
    public double BlackPixelRatioThreshold { get; set; } = 0.85;
    public double MinBlackDurationSec { get; set; } = 0;
    public double FreezeDifferenceThreshold { get; set; } = 0.015;
    public double MinFreezeDurationSec { get; set; } = 5;
    public int MinFreezeFrames { get; set; } = 30;
    public double SilenceThresholdDb { get; set; } = -50;
    public double MinSilenceDurationSec { get; set; } = 5;
    public double ClippingThresholdDb { get; set; } = -1;
    public int ClippingCountThreshold { get; set; } = 10;
    public double InputTimeoutSec { get; set; } = 5;
    public double LowFpsThreshold { get; set; } = 15;
    public double MinLowFpsDurationSec { get; set; } = 5;
}

public sealed class AlertRecord
{
    public string Id { get; set; } = "";
    public string StreamId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Status { get; set; } = "open";
    public string Message { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SnoozedUntil { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
    public string SourceClientId { get; set; } = "";

    public override string ToString()
    {
        var snooze = SnoozedUntil is not null && string.Equals(Status, "snoozed", StringComparison.OrdinalIgnoreCase)
            ? $" / snoozed until {SnoozedUntil:HH:mm}"
            : "";
        return $"{Severity.ToUpperInvariant()} / {Status} / {Type} / {Message}{snooze}";
    }
}

public sealed class TileViewModel
{
    public string StreamName { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string StatusColor { get; init; } = "#6b7280";
    public string Metadata { get; init; } = "";
    public string AudioLevel { get; init; } = "";
    public string AlertText { get; init; } = "";
}

public static class LocalConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IPMultiViewer", "config");

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IPMultiViewer", "logs");

    public static string DefaultConfigPath => Path.Combine(ConfigDirectory, "local-config.json");

    public static LocalConfig LoadOrCreate()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        if (!File.Exists(DefaultConfigPath))
        {
            var created = CreateDefault();
            Save(created);
            return created;
        }

        using var stream = File.OpenRead(DefaultConfigPath);
        var config = JsonSerializer.Deserialize<LocalConfig>(stream, Options) ?? CreateDefault();
        EnsureClientIdentity(config);
        EnsureAlertDefaults(config);
        return config;
    }

    public static LocalConfig LoadFrom(string path)
    {
        using var stream = File.OpenRead(path);
        var config = JsonSerializer.Deserialize<LocalConfig>(stream, Options) ?? CreateDefault();
        EnsureClientIdentity(config);
        EnsureAlertDefaults(config);
        return config;
    }

    public static void Save(LocalConfig config)
    {
        EnsureClientIdentity(config);
        EnsureAlertDefaults(config);
        Directory.CreateDirectory(ConfigDirectory);
        SaveTo(config, DefaultConfigPath);
    }

    public static void SaveTo(LocalConfig config, string path)
    {
        EnsureClientIdentity(config);
        EnsureAlertDefaults(config);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, config, Options);
    }

    public static LocalConfig CreateDefault()
    {
        var config = new LocalConfig();
        EnsureClientIdentity(config);
        config.Streams.Add(new StreamConfig { Id = "cam-01", Name = "Camera 01", InputUrl = "rtsp://user:<PASSWORD>@camera.example.com/live", InputProtocol = "rtsp", Group = "Studio", Tags = ["studio", "main"] });
        config.Streams.Add(new StreamConfig { Id = "cam-02", Name = "Camera 02", InputUrl = "rtmp://source.example.com/live", InputProtocol = "rtmp", Group = "Studio" });
        config.Streams.Add(new StreamConfig { Id = "cam-03", Name = "Camera 03", InputUrl = "https://source.example.com/live.m3u8", InputProtocol = "hls", Group = "Remote" });
        config.Streams.Add(new StreamConfig { Id = "cam-04", Name = "Camera 04", Enabled = false, InputUrl = "rtsp://camera.example.com/offline", InputProtocol = "rtsp", Group = "Remote" });
        config.AlertProfiles.Add(new AlertProfileConfig());
        config.Layouts.Add(new LayoutConfig
        {
            Id = "layout-2x2",
            Name = "Default 2x2",
            Kind = "2x2",
            Tiles =
            [
                new LayoutTileConfig { Index = 0, StreamId = "cam-01", Row = 0, Column = 0 },
                new LayoutTileConfig { Index = 1, StreamId = "cam-02", Row = 0, Column = 1 },
                new LayoutTileConfig { Index = 2, StreamId = "cam-03", Row = 1, Column = 0 },
                new LayoutTileConfig { Index = 3, StreamId = "cam-04", Row = 1, Column = 1 }
            ]
        });
        config.AlertHistory.Add(new AlertRecord { Id = "alert-01", StreamId = "cam-02", Type = "video.black", Severity = "warning", Status = "open", Message = "\u9ed2\u5473\u3092\u691c\u77e5\u3057\u307e\u3057\u305f", SourceClientId = config.ClientId });
        return config;
    }

    private static void EnsureClientIdentity(LocalConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            config.ClientId = $"client-{Guid.NewGuid():N}";
        }

        if (string.IsNullOrWhiteSpace(config.ClientName))
        {
            config.ClientName = Environment.MachineName;
        }
    }

    private static void EnsureAlertDefaults(LocalConfig config)
    {
        if (config.AlertProfiles.Count == 0)
        {
            config.AlertProfiles.Add(new AlertProfileConfig());
        }

        var usedProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.AlertProfiles)
        {
            var defaults = new AlertProfileConfig();
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = usedProfileIds.Count == 0 && !usedProfileIds.Contains(defaults.Id)
                    ? defaults.Id
                    : CreateAlertProfileId(usedProfileIds);
            }

            if (!usedProfileIds.Add(profile.Id))
            {
                profile.Id = CreateAlertProfileId(usedProfileIds);
                usedProfileIds.Add(profile.Id);
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = defaults.Name;
            }

            profile.BlackThreshold = profile.BlackThreshold > 0 ? profile.BlackThreshold : defaults.BlackThreshold;
            profile.BlackPixelThreshold = profile.BlackPixelThreshold > 0 ? profile.BlackPixelThreshold : defaults.BlackPixelThreshold;
            profile.BlackPixelRatioThreshold = profile.BlackPixelRatioThreshold > 0 ? profile.BlackPixelRatioThreshold : defaults.BlackPixelRatioThreshold;
            profile.MinBlackDurationSec = 0;
            profile.FreezeDifferenceThreshold = profile.FreezeDifferenceThreshold > 0 ? profile.FreezeDifferenceThreshold : defaults.FreezeDifferenceThreshold;
            profile.MinFreezeDurationSec = profile.MinFreezeDurationSec > 0 ? profile.MinFreezeDurationSec : defaults.MinFreezeDurationSec;
            profile.MinFreezeFrames = profile.MinFreezeFrames > 0 ? profile.MinFreezeFrames : defaults.MinFreezeFrames;
            profile.SilenceThresholdDb = profile.SilenceThresholdDb < 0 ? profile.SilenceThresholdDb : defaults.SilenceThresholdDb;
            profile.MinSilenceDurationSec = profile.MinSilenceDurationSec > 0 ? profile.MinSilenceDurationSec : defaults.MinSilenceDurationSec;
            profile.ClippingThresholdDb = profile.ClippingThresholdDb < 0 ? profile.ClippingThresholdDb : defaults.ClippingThresholdDb;
            profile.ClippingCountThreshold = profile.ClippingCountThreshold > 0 ? profile.ClippingCountThreshold : defaults.ClippingCountThreshold;
            profile.InputTimeoutSec = profile.InputTimeoutSec > 0 ? profile.InputTimeoutSec : defaults.InputTimeoutSec;
            profile.LowFpsThreshold = profile.LowFpsThreshold > 0 ? profile.LowFpsThreshold : defaults.LowFpsThreshold;
            profile.MinLowFpsDurationSec = profile.MinLowFpsDurationSec > 0 ? profile.MinLowFpsDurationSec : defaults.MinLowFpsDurationSec;
        }

        var fallbackProfileId = config.AlertProfiles.FirstOrDefault()?.Id ?? "default";
        foreach (var stream in config.Streams.Where(stream => config.AlertProfiles.All(profile => !string.Equals(profile.Id, stream.AlertProfileId, StringComparison.OrdinalIgnoreCase))))
        {
            stream.AlertProfileId = fallbackProfileId;
        }

        foreach (var alert in config.AlertHistory)
        {
            if (alert.StartedAt == default)
            {
                alert.StartedAt = DateTimeOffset.UtcNow;
            }

            if (alert.UpdatedAt == default)
            {
                alert.UpdatedAt = alert.StartedAt;
            }
        }
    }

    private static string CreateAlertProfileId(ISet<string> usedIds)
    {
        string id;
        do
        {
            id = $"profile-{Guid.NewGuid():N}"[..20];
        }
        while (usedIds.Contains(id));
        return id;
    }
}
