using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using FormsDockStyle = System.Windows.Forms.DockStyle;
using FormsPanel = System.Windows.Forms.Panel;
using WindowsFormsHost = System.Windows.Forms.Integration.WindowsFormsHost;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using IPMultiViewer.Core.Audio;

namespace IPMultiViewer.Desktop;

public partial class MainWindow : Window
{
    private readonly record struct PlaybackVideoFreshness(double AgeMs, double RenderDelayMs);

    private static readonly object ColorResourceSync = new();
    private static readonly object MissingPlaybackOwner = new();
    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Color> ColorCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ActiveAlertStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "open",
        "acknowledged",
        "snoozed"
    };
    private static readonly string[] PendingIssueTypes =
    [
        "video.no_signal",
        "video.black",
        "video.freeze",
        "video.low_fps",
        "audio.channel_missing",
        "audio.silence",
        "audio.clipping"
    ];
    private static readonly HashSet<string> MonitoringAlertTypes = new(PendingIssueTypes.Concat(["video.decode_error"]), StringComparer.OrdinalIgnoreCase);

    internal static bool IsActiveAlertStatus(string status)
    {
        return ActiveAlertStatuses.Contains(status);
    }

    private LocalConfig _config = new();
    private readonly Dictionary<string, VlcMediaPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FfmpegPlaybackSession> _ffmpegPlaybackSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioLevelMonitor> _audioMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IVideoAnalysisMonitor> _videoMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _videoMonitorShareKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IVideoAnalysisMonitor> _sharedVideoMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingVideoMonitorStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaybackSnapshot> _playbackSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _issueSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastPlaybackTimeMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastPlaybackProgressMarker = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastPlaybackProgressAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastVideoAnalysisLogAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _playbackRestartAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _playbackRestartAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingPlaybackRestarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _monitoringRequestedStreamIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _playbackStartupSlots = new(2, 2);
    private readonly SemaphoreSlim _playbackLaunchGate = new(1, 1);
    private Func<StreamConfig, System.Threading.Tasks.Task>? _playbackStarterOverride = null;
    private readonly Dictionary<string, int> _lastDisplayedPictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastVideoPresentedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Border>> _tileStatusFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _tileBorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _tileNameTextBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _tileStatusTextBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StackPanel> _tileAlertStacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _tileSurfaceHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileSurfaceRenderKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WindowsFormsHost> _tileVideoHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FormsPanel> _tileVideoPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _tileFfmpegImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _tileSurfacePlaceholders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _tileAudioMeterHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioMeterView[]> _tileAudioMeterViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, AudioMeterView>> _tileAudioMeterViewLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastAudioMeterTargetRefreshAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileChromeRenderKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileStatusTextRenderKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileRenderedAlertKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _tileIndexStreamIds = new();
    private readonly Dictionary<string, int> _streamTileIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _alertSoundWaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _immediateVideoAlertEvaluationSync = new();
    private readonly HashSet<string> _pendingImmediateVideoAlertEvaluations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _pendingImmediateBlackFrameDetections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new();
    private SoundPlayer? _activeAlertSoundPlayer;
    private MemoryStream? _activeAlertSoundStream;
    private string? _activeAlertSoundKey;
    private bool _isLoading;
    private bool _blinkPhase;
    private LibVLC? _libVlc;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _audioMeterTimer;
    private SettingsWindow _settingsWindow = null!;
    private AlertHistoryWindow? _alertHistoryWindow;
    private DateTimeOffset _lastServerSyncAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSlowPlaybackRefreshAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAudioMeterAnimationAt = DateTimeOffset.MinValue;
    private bool _serverSyncInFlight;
    private bool _playbackStartInFlight;
    private long _playbackOperationGeneration;
    private bool _layoutSelectionInProgress;
    private DateTimeOffset _nextPlaybackLaunchAt = DateTimeOffset.MinValue;
    private const double TileFrameThickness = 3;
    private const double EmbeddedVideoMeterReservePx = 84;
    private const int LivePlaybackCacheMs = 100;
    private static readonly TimeSpan PlaybackBatchStartSpacing = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PlaybackOwnerStartAttemptTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PlaybackStartupSlotTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PlaybackStartupPollInterval = TimeSpan.FromMilliseconds(100);
    private const string GpuRenderingEnvironmentVariable = "IPMV_GPU_RENDERING";
    private const string ForceFfmpegPlaybackEnvironmentVariable = "IPMV_FORCE_FFMPEG_PLAYBACK";
    private static readonly TimeSpan PlaybackVideoFreshnessWarning = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ImmediateBlackAlertMinimumVisibleDuration = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions ServerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private const string AlertMessageDecodeError = "\u30c7\u30b3\u30fc\u30c9\u30a8\u30e9\u30fc\u3092\u691c\u77e5\u3057\u307e\u3057\u305f";
    private const string AlertMessageNoSignal = "\u7121\u4fe1\u53f7\u307e\u305f\u306f\u5165\u529b\u505c\u6b62\u3092\u691c\u77e5\u3057\u307e\u3057\u305f";
    private const string AlertMessageLowFpsPrefix = "FPS \u304c\u95be\u5024\u3092\u4e0b\u56de\u3063\u3066\u3044\u307e\u3059";
    private const string AlertMessageBlack = "\u9ed2\u5473\u3092\u691c\u77e5\u3057\u307e\u3057\u305f";
    private const string AlertMessageFreeze = "\u30d5\u30ea\u30fc\u30ba\u3092\u691c\u77e5\u3057\u307e\u3057\u305f";
    private const string AlertMessageAudioChannelMissing = "\u97f3\u58f0\u30c8\u30e9\u30c3\u30af\u3092\u691c\u51fa\u3067\u304d\u307e\u305b\u3093";
    private const string AlertMessageAudioSilence = "\u7121\u97f3\u3092\u691c\u77e5\u3057\u307e\u3057\u305f";
    private const string AlertMessageAudioClipping = "\u97f3\u58f0\u30af\u30ea\u30c3\u30d4\u30f3\u30b0\u3092\u691c\u77e5\u3057\u307e\u3057\u305f";

    private enum PlaybackStartupOutcome
    {
        Ready,
        Armed,
        Pending,
        Terminal,
        Superseded,
        Cancelled
    }

    private ListBox StreamsList => _settingsWindow.StreamsList;
    private ComboBox LayoutCombo => _settingsWindow.LayoutCombo;
    private CheckBox GlobalMuteCheck => _settingsWindow.GlobalMuteCheck;
    private CheckBox AlertSoundCheck => _settingsWindow.AlertSoundCheck;
    private TextBox ServerUrlText => _settingsWindow.ServerUrlText;
    private PasswordBox ClientTokenBox => _settingsWindow.ClientTokenBox;
    private CheckBox ServerSyncCheck => _settingsWindow.ServerSyncCheck;
    private TextBlock ServerSyncStatusText => _settingsWindow.ServerSyncStatusText;
    private TextBlock ServerClientSummaryText => _settingsWindow.ServerClientSummaryText;
    private TextBlock ConfigPathText => _settingsWindow.ConfigPathText;
    private TextBlock AlertSummaryText => _settingsWindow.AlertSummaryText;
    private TextBox StreamNameText => _settingsWindow.StreamNameText;
    private TextBox StreamUrlText => _settingsWindow.StreamUrlText;
    private TextBox StreamProtocolText => _settingsWindow.StreamProtocolText;
    private TextBox StreamGroupText => _settingsWindow.StreamGroupText;
    private TextBox StreamTagsText => _settingsWindow.StreamTagsText;
    private ComboBox StreamAlertProfileCombo => _settingsWindow.StreamAlertProfileCombo;
    private CheckBox StreamEnabledCheck => _settingsWindow.StreamEnabledCheck;
    private CheckBox StreamAnalysisCheck => _settingsWindow.StreamAnalysisCheck;
    private CheckBox StreamAudioCheck => _settingsWindow.StreamAudioCheck;
    private CheckBox StreamAlertMuteCheck => _settingsWindow.StreamAlertMuteCheck;
    private Button ApplyStreamButton => _settingsWindow.ApplyStreamButton;
    private Button DeleteStreamButton => _settingsWindow.DeleteStreamButton;
    private ComboBox AlertProfileCombo => _settingsWindow.AlertProfileCombo;
    private TextBox AlertProfileNameText => _settingsWindow.AlertProfileNameText;
    private Button DuplicateAlertProfileButton => _settingsWindow.DuplicateAlertProfileButton;
    private Button DeleteAlertProfileButton => _settingsWindow.DeleteAlertProfileButton;
    private TextBox BlackThresholdText => _settingsWindow.BlackThresholdText;
    private TextBox BlackPixelThresholdText => _settingsWindow.BlackPixelThresholdText;
    private TextBox BlackPixelRatioText => _settingsWindow.BlackPixelRatioText;
    private TextBox FreezeDifferenceText => _settingsWindow.FreezeDifferenceText;
    private TextBox MinFreezeDurationText => _settingsWindow.MinFreezeDurationText;
    private TextBox MinFreezeFramesText => _settingsWindow.MinFreezeFramesText;
    private TextBox InputTimeoutText => _settingsWindow.InputTimeoutText;
    private TextBox LowFpsThresholdText => _settingsWindow.LowFpsThresholdText;
    private TextBox MinLowFpsDurationText => _settingsWindow.MinLowFpsDurationText;
    private TextBox SilenceThresholdText => _settingsWindow.SilenceThresholdText;
    private TextBox MinSilenceDurationText => _settingsWindow.MinSilenceDurationText;
    private TextBox ClippingCountText => _settingsWindow.ClippingCountText;

    public MainWindow()
    {
        _playbackTimer = new DispatcherTimer { Interval = PlaybackRefreshInterval() };
        _playbackTimer.Tick += (_, _) => RefreshPlaybackMetadata();
        _audioMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _audioMeterTimer.Tick += (_, _) => RefreshAudioMeterTargets();
        InitializeComponent();
        SetTextIfChanged(ApplicationVersionText, $"v{CurrentApplicationVersion()}");
        _settingsWindow = new SettingsWindow(this);
        LoadConfig();
        CompositionTarget.Rendering += AudioMeterCompositionTarget_Rendering;
        _playbackTimer.Start();
        _audioMeterTimer.Start();
        if (!InitialPlaybackDisabled())
        {
            QueueInitialPlaybackStart();
        }
    }

    private void LoadConfig()
    {
        _isLoading = true;
        _config = LocalConfigStore.LoadOrCreate();
        ConfigureNextUiFromConfig();
        PrewarmAlertSoundWaves();
        EnsureStandardLayouts();
        MainLayoutCombo.ItemsSource = _config.Layouts;
        MainLayoutCombo.SelectedItem = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
            ?? _config.Layouts.FirstOrDefault();
        StreamsList.ItemsSource = _config.Streams;
        LayoutCombo.ItemsSource = _config.Layouts;
        LayoutCombo.SelectedItem = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
            ?? _config.Layouts.FirstOrDefault();
        StreamAlertProfileCombo.ItemsSource = _config.AlertProfiles;
        AlertProfileCombo.ItemsSource = _config.AlertProfiles;
        AlertProfileCombo.SelectedItem = _config.AlertProfiles.FirstOrDefault();
        PopulateAlertProfileEditor(AlertProfileCombo.SelectedItem as AlertProfileConfig);
        StreamsList.SelectedIndex = _config.Streams.Count > 0 ? 0 : -1;
        GlobalMuteCheck.IsChecked = _config.GlobalMute;
        AlertSoundCheck.IsChecked = _config.AlertSoundEnabled;
        ServerUrlText.Text = _config.ServerUrl;
        ClientTokenBox.Password = _config.ClientToken;
        ServerSyncCheck.IsChecked = _config.ServerSyncEnabled;
        SetTextIfChanged(ModeText, string.IsNullOrWhiteSpace(_config.ServerUrl)
            ? "Standalone local mode"
            : $"Server sync: {_config.ServerUrl}");
        SetTextIfChanged(ServerSyncStatusText, _config.ServerSyncEnabled ? "server sync: pending" : "server sync: disabled");
        SetTextIfChanged(ServerClientSummaryText, _config.ServerSyncEnabled ? "server clients: pending" : "server clients: unavailable");
        SetTextIfChanged(ConfigPathText, LocalConfigStore.DefaultConfigPath);
        RenderAlerts();
        RenderTiles();
        _isLoading = false;
        UpdateFooter("Local config loaded.");
    }

    private LibVLC EnsureLibVlc()
    {
        if (_libVlc is not null)
        {
            return _libVlc;
        }

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC(LowLatencyVlcInstanceOptions());
        AppendDesktopLog(GpuRenderingEnabled()
            ? "LibVLC initialized for embedded GPU rendering: vout=direct3d9, avcodec-hw=dxva2."
            : "LibVLC initialized for standard rendering.");
        return _libVlc;
    }

    internal static string[] LowLatencyVlcInstanceOptions()
    {
        var options = new List<string>
        {
            "--no-video-title-show",
            $"--network-caching={LivePlaybackCacheMs}",
            $"--live-caching={LivePlaybackCacheMs}",
            $"--file-caching={LivePlaybackCacheMs}",
            "--drop-late-frames",
            "--skip-frames",
            "--clock-jitter=0",
            "--clock-synchro=0"
        };

        if (GpuRenderingEnabled())
        {
            options.Add("--vout=direct3d9");
            options.Add("--avcodec-hw=dxva2");
            options.Add("--embedded-video");
            options.Add("--no-video-deco");
        }

        return options.ToArray();
    }

    internal static bool GpuRenderingEnabled()
    {
#if IPMV_GPU_RENDERING
        return true;
#else
        return IsTruthy(Environment.GetEnvironmentVariable(GpuRenderingEnvironmentVariable)) ||
            IsTruthy(Environment.GetEnvironmentVariable("IPMULTIVIEWER_GPU_RENDERING"));
#endif
    }

    internal static string FfmpegHardwareDecodeMode()
    {
        return OperatingSystem.IsWindows() && GpuRenderingEnabled()
            ? "d3d11va"
            : "auto";
    }

    internal static void AddFfmpegHardwareDecodeArguments(Collection<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        arguments.Add("-hwaccel");
        arguments.Add(FfmpegHardwareDecodeMode());
    }

    private static TimeSpan PlaybackRefreshInterval()
    {
        return GpuRenderingEnabled()
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromMilliseconds(250);
    }

    internal static bool ForceFfmpegPlaybackEnabled()
    {
        return IsTruthy(Environment.GetEnvironmentVariable(ForceFfmpegPlaybackEnvironmentVariable)) ||
            IsTruthy(Environment.GetEnvironmentVariable("IPMULTIVIEWER_FORCE_FFMPEG_PLAYBACK"));
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    internal static string[] LowLatencyVlcMediaOptions(bool disableAudio)
    {
        var options = new List<string>
        {
            $":network-caching={LivePlaybackCacheMs}",
            $":live-caching={LivePlaybackCacheMs}",
            $":file-caching={LivePlaybackCacheMs}",
            $":sout-mux-caching={LivePlaybackCacheMs}",
            ":rtsp-tcp",
            ":ipv4-timeout=3000000",
            ":drop-late-frames",
            ":skip-frames",
            ":clock-jitter=0",
            ":clock-synchro=0"
        };
        if (disableAudio)
        {
            options.Insert(0, ":no-audio");
        }

        return options.ToArray();
    }

    internal static void AddLowLatencyVlcMediaOptions(Media media, bool disableAudio = false)
    {
        foreach (var option in LowLatencyVlcMediaOptions(disableAudio))
        {
            media.AddOption(option);
        }
    }

    private void RenderTiles()
    {
        CloseNextAlertOverlayWindows();
        DetachRenderedPlaybackSurfaces();
        TileGrid.Children.Clear();
        _tileStatusFrames.Clear();
        _tileBorders.Clear();
        _tileNameTextBlocks.Clear();
        _tileStatusTextBlocks.Clear();
        _tileAlertStacks.Clear();
        _tileSurfaceHosts.Clear();
        _tileSurfaceRenderKeys.Clear();
        _tileVideoHosts.Clear();
        _tileVideoPanels.Clear();
        _tileFfmpegImages.Clear();
        _tileSurfacePlaceholders.Clear();
        _tileAudioMeterHosts.Clear();
        _tileAudioMeterViews.Clear();
        _tileAudioMeterViewLookup.Clear();
        _lastAudioMeterTargetRefreshAt.Clear();
        _tileChromeRenderKeys.Clear();
        _tileStatusTextRenderKeys.Clear();
        _tileRenderedAlertKeys.Clear();
        _tileIndexStreamIds.Clear();
        _streamTileIndexes.Clear();

        var layout = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
            ?? _config.Layouts.FirstOrDefault();
        var size = GridSizeFor(layout?.Kind);
        TileGrid.Rows = size;
        TileGrid.Columns = size;

        var renderedStreamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streamLookup = StreamLookup();
        for (var index = 0; index < size * size; index++)
        {
            var tile = layout?.Tiles.FirstOrDefault(tile => tile.Index == index);
            var stream = StreamForTile(tile, renderedStreamIds, streamLookup);
            TileGrid.Children.Add(CreateTile(stream, index));
        }
    }

    private void DetachRenderedPlaybackSurfaces()
    {
        DisposeRenderedVideoHosts();

        foreach (var image in _tileFfmpegImages.Values)
        {
            image.Source = null;
        }

        foreach (var host in _tileSurfaceHosts.Values)
        {
            host.Children.Clear();
        }
    }

    private void DisposeRenderedVideoHosts()
    {
        foreach (var streamId in _tileVideoHosts.Keys.ToList())
        {
            DisposeRenderedVideoHost(streamId);
        }
    }

    private void DisposeRenderedVideoHost(string streamId)
    {
        if (_players.TryGetValue(streamId, out var player))
        {
            player.Hwnd = IntPtr.Zero;
        }

        if (_tileVideoHosts.Remove(streamId, out var host))
        {
            if (host.Parent is System.Windows.Controls.Panel parent)
            {
                parent.Children.Remove(host);
            }

            host.Child = null;
            host.Dispose();
        }

        if (_tileVideoPanels.Remove(streamId, out var panel))
        {
            panel.Dispose();
        }
    }

    private void RenderTilesAt(IEnumerable<int> tileIndexes)
    {
        var layout = ActiveLayout();
        var size = GridSizeFor(layout?.Kind);
        var tileCount = size * size;
        if (TileGrid.Children.Count != tileCount ||
            TileGrid.Rows != size ||
            TileGrid.Columns != size)
        {
            RenderTiles();
            return;
        }

        var indexes = tileIndexes
            .Where(index => index >= 0 && index < tileCount)
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        foreach (var index in indexes)
        {
            RemoveRenderedTileBindings(index);
        }

        var streamLookup = StreamLookup();
        foreach (var index in indexes)
        {
            var tile = layout?.Tiles.FirstOrDefault(tile => tile.Index == index);
            var stream = StreamForVisibleTile(tile, streamLookup);
            var replacement = CreateTile(stream, index);
            TileGrid.Children.RemoveAt(index);
            TileGrid.Children.Insert(index, replacement);
        }
    }

    private StreamConfig? StreamForVisibleTile(LayoutTileConfig? tile, IReadOnlyDictionary<string, StreamConfig> streamLookup)
    {
        if (string.IsNullOrWhiteSpace(tile?.StreamId))
        {
            return null;
        }

        return streamLookup.TryGetValue(tile.StreamId, out var stream) ? stream : null;
    }

    private void RemoveRenderedTileBindings(int tileIndex)
    {
        if (!_tileIndexStreamIds.Remove(tileIndex, out var streamId))
        {
            return;
        }

        RemoveNextAlertOverlayWindow(streamId);
        _streamTileIndexes.Remove(streamId);
        DisposeRenderedVideoHost(streamId);

        if (_tileFfmpegImages.Remove(streamId, out var image))
        {
            image.Source = null;
        }

        if (_tileSurfaceHosts.Remove(streamId, out var host))
        {
            host.Children.Clear();
        }

        _tileStatusFrames.Remove(streamId);
        _tileBorders.Remove(streamId);
        _tileNameTextBlocks.Remove(streamId);
        _tileStatusTextBlocks.Remove(streamId);
        _tileAlertStacks.Remove(streamId);
        _tileSurfaceRenderKeys.Remove(streamId);
        _tileSurfacePlaceholders.Remove(streamId);
        _tileAudioMeterHosts.Remove(streamId);
        _tileAudioMeterViews.Remove(streamId);
        _tileAudioMeterViewLookup.Remove(streamId);
        _lastAudioMeterTargetRefreshAt.Remove(streamId);
        _tileChromeRenderKeys.Remove(streamId);
        _tileStatusTextRenderKeys.Remove(streamId);
        _tileRenderedAlertKeys.Remove(streamId);
    }

    private StreamConfig? StreamForTile(
        LayoutTileConfig? tile,
        ISet<string> renderedStreamIds,
        IReadOnlyDictionary<string, StreamConfig>? streamLookup = null)
    {
        if (string.IsNullOrWhiteSpace(tile?.StreamId))
        {
            return null;
        }

        if (!renderedStreamIds.Add(tile.StreamId))
        {
            AppendDesktopLog($"Ignoring duplicate stream assignment while rendering layout {_config.ActiveLayoutId}: tile {tile.Index}, stream {tile.StreamId}");
            return null;
        }

        var lookup = streamLookup ?? StreamLookup();
        return lookup.TryGetValue(tile.StreamId, out var stream) ? stream : null;
    }

    private Border CreateTile(StreamConfig? stream, int index)
    {
        return BuildNextTile(stream, index);
    }

    private void RenderAlerts()
    {
        var activeCount = 0;
        foreach (var alert in _config.AlertHistory)
        {
            if (ActiveAlertStatuses.Contains(alert.Status))
            {
                activeCount++;
            }
        }

        SetTextIfChanged(AlertSummaryText, $"{activeCount} active alerts / {_config.Streams.Count} streams");
        if (_alertHistoryWindow?.IsVisible == true)
        {
            _alertHistoryWindow.RefreshAlerts();
        }
        UpdatePlaybackSummary();
        UpdateContinuousAlertSound();
    }

    private void UpdateAllTileChrome(IReadOnlyList<StreamConfig>? streams = null)
    {
        var layoutStreams = streams ?? ActiveLayoutStreams();
        var activeAlertsByStream = ActiveAlertsByStream(layoutStreams);
        foreach (var stream in layoutStreams)
        {
            UpdateTileChrome(stream, activeAlertsByStream);
        }
    }

    private void UpdateSingleTileChrome(IReadOnlyList<StreamConfig> streams, string streamId)
    {
        foreach (var stream in streams)
        {
            if (!string.Equals(stream.Id, streamId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            UpdateTileChrome(stream);
            return;
        }
    }

    private void UpdateTileChrome(
        StreamConfig stream,
        IReadOnlyDictionary<string, IReadOnlyList<AlertRecord>>? activeAlertsByStream = null)
    {
        var activeAlerts = ActiveAlertsFor(stream.Id, activeAlertsByStream);
        var alert = activeAlerts.Count > 0 ? activeAlerts[0] : null;
        var pendingIssue = alert is null ? PendingIssueFor(stream.Id, activeAlerts) : null;
        _playbackSnapshots.TryGetValue(stream.Id, out var snapshot);
        var isLoaded = IsPlaybackLoaded(stream.Id);
        var videoFreshness = PlaybackVideoFreshnessFor(stream.Id);
        var statusColor = pendingIssue is not null
            ? StatusColorForPendingIssue(pendingIssue)
            : StatusColorFor(stream, alert, isLoaded, snapshot, videoFreshness);
        var statusTextValue = pendingIssue is not null
            ? $"pending / {pendingIssue}"
            : NextStatusTextFor(stream, alert, isLoaded, snapshot, videoFreshness);
        var alertOverlayKey = AlertOverlayRenderKey(activeAlerts, limit: 5);
        UpdateTileStatusText(stream.Id, statusTextValue, statusColor);

        var shouldBlink = alert is not null || pendingIssue is not null;
        var renderKey = $"{statusColor}|{(shouldBlink ? (_blinkPhase ? "blink-on" : "blink-off") : "solid")}|{alertOverlayKey}";
        if (_tileChromeRenderKeys.TryGetValue(stream.Id, out var previousKey) &&
            string.Equals(previousKey, renderKey, StringComparison.Ordinal))
        {
            return;
        }

        _tileChromeRenderKeys[stream.Id] = renderKey;

        if (_tileStatusFrames.TryGetValue(stream.Id, out var frames))
        {
            foreach (var frame in frames)
            {
                ApplyFadeBlink(frame, shouldBlink, statusColor, _blinkPhase);
            }
        }

        if (_tileBorders.TryGetValue(stream.Id, out var tileBorder))
        {
            tileBorder.BorderBrush = BrushFromHex("#243044");
            tileBorder.BorderThickness = new Thickness(1);
            tileBorder.BeginAnimation(OpacityProperty, null);
            tileBorder.Opacity = 1.0;
        }

        RenderAlertOverlay(stream.Id, activeAlerts: activeAlerts);
    }

    private void UpdateTileStatusText(string streamId, string text, string statusColor)
    {
        var renderKey = $"{statusColor}|{text}";
        if (_tileStatusTextRenderKeys.TryGetValue(streamId, out var previousKey) &&
            string.Equals(previousKey, renderKey, StringComparison.Ordinal))
        {
            return;
        }

        _tileStatusTextRenderKeys[streamId] = renderKey;
        if (!_tileStatusTextBlocks.TryGetValue(streamId, out var statusText))
        {
            return;
        }

        SetTextIfChanged(statusText, text);

        var foreground = BrushFromHex(statusColor);
        if (!ReferenceEquals(statusText.Foreground, foreground))
        {
            statusText.Foreground = foreground;
        }
    }

    private void UpdateTileTitle(StreamConfig stream)
    {
        if (_tileNameTextBlocks.TryGetValue(stream.Id, out var title))
        {
            SetTextIfChanged(title, stream.Name);
        }
    }

    private static void ApplyFadeBlink(Border frame, bool enabled, string statusColor, bool blinkPhase)
    {
        var tag = enabled ? $"blink-color:{statusColor}:{blinkPhase}" : $"solid:{statusColor}";
        if (Equals(frame.Tag, tag))
        {
            return;
        }

        frame.Tag = tag;
        frame.BeginAnimation(OpacityProperty, null);
        frame.Opacity = 1.0;
        var baseColor = ColorFromHex(statusColor);
        if (!enabled)
        {
            frame.Background = new SolidColorBrush(baseColor);
            return;
        }

        var dimColor = Color.Multiply(baseColor, 0.28f);
        dimColor.A = 255;
        frame.Background = new SolidColorBrush(blinkPhase ? baseColor : dimColor);
    }

    private void RenderAlertOverlay(string streamId, bool force = false, IReadOnlyList<AlertRecord>? activeAlerts = null)
    {
        if (!_tileAlertStacks.TryGetValue(streamId, out var stack))
        {
            return;
        }

        var alerts = activeAlerts is null
            ? ActiveAlertsFor(streamId)
            : activeAlerts;
        var renderKey = AlertOverlayRenderKey(alerts, limit: 5);
        if (!force &&
            _tileRenderedAlertKeys.TryGetValue(streamId, out var previousKey) &&
            string.Equals(previousKey, renderKey, StringComparison.Ordinal))
        {
            return;
        }

        _tileRenderedAlertKeys[streamId] = renderKey;
        stack.Children.Clear();
        var alertCount = Math.Min(5, alerts.Count);
        for (var alertIndex = 0; alertIndex < alertCount; alertIndex++)
        {
            var alert = alerts[alertIndex];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"{AlertShortType(alert.Type)}  {alert.Message}",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            var acknowledgeButton = OverlayActionButton(
                "Ack");
            acknowledgeButton.ToolTip = "Acknowledge";
            acknowledgeButton.Click += (_, _) => SetAlertStatus(alert, "acknowledged");
            Grid.SetColumn(acknowledgeButton, 1);
            row.Children.Add(acknowledgeButton);

            var snoozeButton = OverlayActionButton(
                "Snooze");
            snoozeButton.ToolTip = "Snooze";
            snoozeButton.Click += (_, _) => SetAlertStatus(alert, "snoozed");
            Grid.SetColumn(snoozeButton, 2);
            row.Children.Add(snoozeButton);

            var resolveButton = OverlayActionButton(
                "Resolve");
            resolveButton.ToolTip = "Resolve";
            resolveButton.Click += (_, _) => SetAlertStatus(alert, "resolved");
            Grid.SetColumn(resolveButton, 3);
            row.Children.Add(resolveButton);

            var ignoreButton = OverlayActionButton(
                "Ignore");
            ignoreButton.ToolTip = "Ignore";
            ignoreButton.Click += (_, _) => SetAlertStatus(alert, "ignored");
            Grid.SetColumn(ignoreButton, 4);
            row.Children.Add(ignoreButton);

            var banner = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = BrushFromHex(AlertBackgroundFor(alert.Severity)),
                CornerRadius = new CornerRadius(0),
                Margin = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 4, 8, 4),
                Child = row
            };
            banner.SetBinding(FrameworkElement.WidthProperty, new Binding("ActualWidth") { Source = stack });
            stack.Children.Add(banner);
        }
        UpdateNextAlertReserve(streamId, alertCount);
    }

    private static string AlertOverlayRenderKey(IReadOnlyList<AlertRecord> activeAlerts, int limit = int.MaxValue)
    {
        var count = Math.Min(limit, activeAlerts.Count);
        if (count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append('|');
            }

            var alert = activeAlerts[index];
            builder
                .Append(alert.Id)
                .Append(':')
                .Append(alert.Type)
                .Append(':')
                .Append(alert.Severity)
                .Append(':')
                .Append(alert.Status)
                .Append(':')
                .Append(alert.Message);
        }

        return builder.ToString();
    }

    private void RenderAudioMeter(string streamId, bool force = false)
    {
        UpdateAudioMeterTargets(streamId, force);
    }

    private void RefreshAudioMeterTargets()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (streamId, host) in _tileAudioMeterHosts)
        {
            var refreshInterval = host.Visibility == Visibility.Visible
                ? AudioMeterTargetRefreshInterval
                : AudioMeterHiddenProbeInterval;
            if (!_lastAudioMeterTargetRefreshAt.TryGetValue(streamId, out var refreshedAt) ||
                now - refreshedAt >= refreshInterval)
            {
                UpdateAudioMeterTargets(streamId, force: false, now);
            }
        }
    }

    private void AudioMeterCompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized || _tileAudioMeterViews.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAudioMeterAnimationAt < AudioMeterAnimationMinInterval)
        {
            return;
        }

        _lastAudioMeterAnimationAt = now;
        foreach (var (streamId, views) in _tileAudioMeterViews)
        {
            if (!_tileAudioMeterHosts.TryGetValue(streamId, out var host) ||
                host.Visibility != Visibility.Visible)
            {
                continue;
            }

            AdvanceAudioMeterViews(views, immediate: false, now);
        }
    }

    private void UpdateAudioMeterTargets(string streamId, bool force = false)
    {
        UpdateAudioMeterTargets(streamId, force, DateTimeOffset.UtcNow);
    }

    private void UpdateAudioMeterTargets(string streamId, bool force, DateTimeOffset now)
    {
        if (!_tileAudioMeterHosts.TryGetValue(streamId, out var host))
        {
            return;
        }

        _lastAudioMeterTargetRefreshAt[streamId] = now;
        if (!TryGetFreshAudioSnapshot(streamId, out var snapshot))
        {
            host.Visibility = Visibility.Collapsed;
            if (_tileAudioMeterViews.TryGetValue(streamId, out var staleViews))
            {
                ResetAudioMeterViews(staleViews);
            }

            return;
        }

        var channels = AudioMeterChannels(snapshot);
        var wasVisible = host.Visibility == Visibility.Visible;
        var viewsRebuilt = false;
        var existingViews = _tileAudioMeterViews.TryGetValue(streamId, out var currentViews)
            ? currentViews
            : [];
        var existingViewLookup = _tileAudioMeterViewLookup.TryGetValue(streamId, out var currentViewLookup)
            ? currentViewLookup
            : null;
        if (!force &&
            existingViewLookup is not null &&
            AudioMeterViewsMatch(existingViewLookup, channels))
        {
            host.Visibility = Visibility.Visible;
        }
        else
        {
            host.Children.Clear();
            existingViews = CreateAudioMeterPanels(host, channels);
            _tileAudioMeterViews[streamId] = existingViews;
            existingViewLookup = AudioMeterViewLookup(existingViews);
            _tileAudioMeterViewLookup[streamId] = existingViewLookup;
            viewsRebuilt = true;
        }

        foreach (var channel in channels)
        {
            if (existingViewLookup is null ||
                !existingViewLookup.TryGetValue(channel.Channel, out var view))
            {
                continue;
            }

            var displayedInputDb = _nextMeterMode == MeterDisplayMode.Peak
                ? channel.PeakDb
                : channel.LevelDb;
            view.TargetLevelRatio = AudioMeterRatio(displayedInputDb);
            view.TargetPeakRatio = AudioMeterRatio(channel.PeakDb);
            view.Clipping = channel.ClippingSamples > 0 || channel.PeakDb >= -1;
            view.LastLevelDb = channel.LevelDb;
            view.LastPeakDb = channel.PeakDb;
            if (force || now - view.LastTooltipAt >= TimeSpan.FromSeconds(1))
            {
                var reading = AudioMeterScale.Evaluate(displayedInputDb, _nextMeterMode);
                view.Track.ToolTip = $"CH{channel.Channel}: {reading.DisplayText} / RMS {channel.LevelDb:0.0} dBFS / peak {channel.PeakDb:0.0} dBFS";
                view.LastTooltipAt = now;
            }
        }

        host.Visibility = Visibility.Visible;
        if (viewsRebuilt)
        {
            UpdateNextAudioMeterReserve(streamId, channels);
            ApplyNextMeterModeToStream(streamId);
        }
        if (double.IsFinite(snapshot.MediaTimeSeconds))
        {
            // Timestamped FFmpeg levels already describe the frame being shown.
            // Apply both attack and release immediately so display ballistics do not
            // re-introduce A/V drift after the PTS match.
            AdvanceAudioMeterViews(existingViews, immediate: true, now);
        }
        else if (force || viewsRebuilt || !wasVisible)
        {
            AdvanceAudioMeterViews(streamId, immediate: true);
        }
    }

    private static bool AudioMeterViewsMatch(AudioMeterView[] views, AudioChannelLevel[] channels)
    {
        if (views.Length != channels.Length)
        {
            return false;
        }

        foreach (var channel in channels)
        {
            if (FindAudioMeterView(views, channel.Channel) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AudioMeterViewsMatch(IReadOnlyDictionary<int, AudioMeterView> viewsByChannel, AudioChannelLevel[] channels)
    {
        if (viewsByChannel.Count != channels.Length)
        {
            return false;
        }

        foreach (var channel in channels)
        {
            if (!viewsByChannel.ContainsKey(channel.Channel))
            {
                return false;
            }
        }

        return true;
    }

    private static AudioMeterView? FindAudioMeterView(AudioMeterView[] views, int channel)
    {
        foreach (var view in views)
        {
            if (view.Channel == channel)
            {
                return view;
            }
        }

        return null;
    }

    private static Dictionary<int, AudioMeterView> AudioMeterViewLookup(AudioMeterView[] views)
    {
        var lookup = new Dictionary<int, AudioMeterView>(views.Length);
        foreach (var view in views)
        {
            lookup[view.Channel] = view;
        }

        return lookup;
    }

    private AudioMeterView[] CreateAudioMeterPanels(Grid host, AudioChannelLevel[] channels)
    {
        var views = new List<AudioMeterView>();
        var (leftChannels, rightChannels) = SplitAudioChannelsForSides(channels);
        if (leftChannels.Length > 0)
        {
            var leftPanel = CreateAudioMeterSidePanel(leftChannels, HorizontalAlignment.Left, views);
            Grid.SetColumn(leftPanel, 0);
            host.Children.Add(leftPanel);
        }

        if (rightChannels.Length > 0)
        {
            var rightPanel = CreateAudioMeterSidePanel(rightChannels, HorizontalAlignment.Right, views);
            Grid.SetColumn(rightPanel, 2);
            host.Children.Add(rightPanel);
        }

        return views.ToArray();
    }

    private bool TryGetFreshAudioSnapshot(string streamId, out AudioLevelSnapshot snapshot)
    {
        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var ffmpegSession) &&
            ffmpegSession.TryGetAudioSnapshot(out snapshot) &&
            DateTimeOffset.UtcNow - snapshot.LastSamplesAt <= TimeSpan.FromSeconds(3))
        {
            return true;
        }

        if (_audioMonitors.TryGetValue(streamId, out var monitor) &&
            monitor.TryGetSnapshot(out snapshot) &&
            DateTimeOffset.UtcNow - snapshot.LastSamplesAt <= TimeSpan.FromSeconds(3))
        {
            return true;
        }

        snapshot = EmptyAudioLevelSnapshot();
        return false;
    }

    private static (AudioChannelLevel[] Left, AudioChannelLevel[] Right) SplitAudioChannelsForSides(AudioChannelLevel[] channels)
    {
        if (channels.Length <= 1)
        {
            return (channels, Array.Empty<AudioChannelLevel>());
        }

        var leftCount = 0;
        var rightCount = 0;
        foreach (var channel in channels)
        {
            if (channel.Channel % 2 == 1)
            {
                leftCount++;
            }
            else
            {
                rightCount++;
            }
        }

        if (leftCount == 0 || rightCount == 0)
        {
            var splitIndex = (channels.Length + 1) / 2;
            var splitLeft = new AudioChannelLevel[splitIndex];
            var splitRight = new AudioChannelLevel[channels.Length - splitIndex];
            Array.Copy(channels, 0, splitLeft, 0, splitLeft.Length);
            Array.Copy(channels, splitIndex, splitRight, 0, splitRight.Length);
            return (splitLeft, splitRight);
        }

        var left = new AudioChannelLevel[leftCount];
        var right = new AudioChannelLevel[rightCount];
        var leftIndex = 0;
        var rightIndex = 0;
        foreach (var channel in channels)
        {
            if (channel.Channel % 2 == 1)
            {
                left[leftIndex++] = channel;
            }
            else
            {
                right[rightIndex++] = channel;
            }
        }

        return (left, right);
    }

    private void AdvanceAudioMeterViews(string streamId, bool immediate)
    {
        if (!_tileAudioMeterViews.TryGetValue(streamId, out var views))
        {
            return;
        }

        AdvanceAudioMeterViews(views, immediate);
    }

    private void AdvanceAudioMeterViews(AudioMeterView[] views, bool immediate)
    {
        AdvanceAudioMeterViews(views, immediate, DateTimeOffset.UtcNow);
    }

    private static void AdvanceAudioMeterViews(AudioMeterView[] views, bool immediate, DateTimeOffset now)
    {
        foreach (var view in views)
        {
            var elapsedMs = view.LastAnimationAt > DateTimeOffset.MinValue
                ? Math.Clamp((now - view.LastAnimationAt).TotalMilliseconds, 1, AudioMeterMaxAnimationDeltaMs)
                : AudioMeterNominalFrameMs;
            view.LastAnimationAt = now;
            var elapsedFrames = elapsedMs / AudioMeterNominalFrameMs;
            var levelBlend = immediate
                ? 1
                : 1 - Math.Pow(1 - AudioMeterLevelBlendPerFrame, elapsedFrames);
            var peakDecay = immediate
                ? 0
                : Math.Pow(AudioMeterPeakDecayPerFrame, elapsedFrames);

            view.DisplayedLevelRatio = immediate || view.TargetLevelRatio >= view.DisplayedLevelRatio
                ? view.TargetLevelRatio
                : view.DisplayedLevelRatio + (view.TargetLevelRatio - view.DisplayedLevelRatio) * levelBlend;
            view.DisplayedPeakRatio = immediate
                ? view.TargetPeakRatio
                : Math.Max(view.TargetPeakRatio, view.DisplayedPeakRatio * peakDecay);
            var levelRatio = Math.Clamp(view.DisplayedLevelRatio, 1 / AudioMeterHeight, 1);
            var peakTop = Math.Clamp(AudioMeterHeight - AudioMeterHeight * view.DisplayedPeakRatio, 0, AudioMeterHeight - 2);
            SetAudioMeterFillClip(view, levelRatio);
            view.PeakOffset.Y = peakTop;
            if (view.DisplayedClipping != view.Clipping)
            {
                view.PeakMarker.Background = view.Clipping ? BrushFromHex("#fed7aa") : BrushFromHex("#e2e8f0");
                view.DisplayedClipping = view.Clipping;
            }
        }
    }

    private static void ResetAudioMeterViews(AudioMeterView[] views)
    {
        foreach (var view in views)
        {
            view.TargetLevelRatio = 0;
            view.TargetPeakRatio = 0;
            view.DisplayedLevelRatio = 0;
            view.DisplayedPeakRatio = 0;
            view.Clipping = false;
            view.DisplayedClipping = false;
            view.LastAnimationAt = DateTimeOffset.MinValue;
            SetAudioMeterFillClip(view, 1 / AudioMeterHeight);
            view.PeakOffset.Y = AudioMeterHeight - 2;
            view.PeakMarker.Background = BrushFromHex("#e2e8f0");
        }
    }

    private static void SetAudioMeterFillClip(AudioMeterView view, double levelRatio)
    {
        var visibleHeight = AudioMeterHeight * Math.Clamp(levelRatio, 0, 1);
        view.FillClip.Rect = new Rect(0, AudioMeterHeight - visibleHeight, AudioMeterBarWidth, visibleHeight);
    }

    private const double AudioMeterHeight = 152;
    private const double AudioMeterBarWidth = 6;
    private const double AudioMeterTrackWidth = 10;
    private const double AudioMeterScaleWidth = 24;
    private const double AudioMeterChannelWidth = 16;
    private const double AudioMeterChannelFontSize = 7.5;
    private const int AudioMeterMaxChannels = 16;
    private static readonly (double TopDb, double BottomDb, string Color, double Opacity)[] AudioMeterColorZones =
    [
        (0, -6, "#ef4444", 0.28),
        (-6, -18, "#facc15", 0.22),
        (-18, -60, "#22c55e", 0.18)
    ];
    private static readonly TimeSpan AudioMeterTargetRefreshInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan AudioMeterHiddenProbeInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan AudioMeterAnimationMinInterval = TimeSpan.FromMilliseconds(33);
    private const double AudioMeterNominalFrameMs = 33;
    private const double AudioMeterMaxAnimationDeltaMs = 100;
    private const double AudioMeterLevelBlendPerFrame = 0.38;
    private const double AudioMeterPeakDecayPerFrame = 0.93;

    private static AudioChannelLevel[] AudioMeterChannels(AudioLevelSnapshot snapshot)
    {
        if (snapshot.Channels.Length == 0)
        {
            return [new AudioChannelLevel(1, snapshot.LevelDb, snapshot.PeakDb, snapshot.ClippingSamples)];
        }

        if (snapshot.Channels.Length <= AudioMeterMaxChannels)
        {
            return snapshot.Channels;
        }

        var channels = new AudioChannelLevel[AudioMeterMaxChannels];
        Array.Copy(snapshot.Channels, channels, AudioMeterMaxChannels);
        return channels;
    }

    private FrameworkElement CreateAudioMeterSidePanel(AudioChannelLevel[] channels, HorizontalAlignment alignment, ICollection<AudioMeterView> views)
    {
        var meterStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignment
        };
        var count = Math.Min(8, channels.Length);
        for (var index = 0; index < count; index++)
        {
            meterStack.Children.Add(CreateAudioMeter(channels[index].Channel, views));
        }

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (alignment == HorizontalAlignment.Right)
        {
            content.Children.Add(CreateAudioMeterScale(labelsOnRight: false));
            content.Children.Add(meterStack);
        }
        else
        {
            content.Children.Add(meterStack);
            content.Children.Add(CreateAudioMeterScale(labelsOnRight: true));
        }

        var panel = new Border
        {
            Background = BrushFromHex("#d9070b12"),
            BorderBrush = BrushFromHex("#66475566"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 6, 5, 5),
            Child = content
        };
        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = panel
        };
    }

    private FrameworkElement CreateAudioMeter(int channel, ICollection<AudioMeterView> views)
    {
        var trackCanvas = new Canvas
        {
            Width = AudioMeterTrackWidth,
            Height = AudioMeterHeight,
            ClipToBounds = true,
            Background = BrushFromHex("#080d16")
        };
        AddAudioMeterColorZones(trackCanvas);
        AddAudioMeterGrid(trackCanvas);
        var fillClip = new RectangleGeometry(new Rect(0, AudioMeterHeight - 1, AudioMeterBarWidth, 1));
        var meterFill = new Border
        {
            Width = AudioMeterBarWidth,
            Height = AudioMeterHeight,
            Background = CreateAudioMeterActiveBrush(_nextMeterMode),
            Clip = fillClip,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        Canvas.SetLeft(meterFill, (AudioMeterTrackWidth - AudioMeterBarWidth) / 2);
        Canvas.SetTop(meterFill, 0);
        trackCanvas.Children.Add(meterFill);
        var peakOffset = new TranslateTransform(0, AudioMeterHeight - 2);
        var peakMarker = new Border
        {
            Width = AudioMeterBarWidth,
            Height = 2,
            Background = BrushFromHex("#e2e8f0"),
            CornerRadius = new CornerRadius(1),
            RenderTransform = peakOffset,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        Canvas.SetLeft(peakMarker, (AudioMeterTrackWidth - AudioMeterBarWidth) / 2);
        Canvas.SetTop(peakMarker, 0);
        trackCanvas.Children.Add(peakMarker);
        var meterTrack = new Border
        {
            Width = AudioMeterTrackWidth,
            Height = AudioMeterHeight,
            Background = BrushFromHex("#101827"),
            BorderBrush = BrushFromHex("#64748b"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = trackCanvas,
            ToolTip = $"CH{channel}: waiting"
        };
        var label = new TextBlock
        {
            Text = $"CH{channel}",
            Foreground = BrushFromHex("#cbd5e1"),
            FontSize = AudioMeterChannelFontSize,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 3)
        };
        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = AudioMeterChannelWidth,
            Margin = new Thickness(0.5, 0, 0.5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(label);
        root.Children.Add(meterTrack);
        views.Add(new AudioMeterView(channel, meterTrack, meterFill, fillClip, peakMarker, peakOffset));
        return root;
    }

    private double AudioMeterRatio(double levelDb)
    {
        return AudioMeterScale.GetRatio(levelDb, _nextMeterMode);
    }

    private static Brush CreateAudioMeterActiveBrush(MeterDisplayMode mode)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        var redEnd = mode == MeterDisplayMode.Vu ? 0.048 : 0.10;
        var yellowStart = mode == MeterDisplayMode.Vu ? 0.049 : 0.11;
        var yellowEnd = mode == MeterDisplayMode.Vu ? 0.159 : 0.30;
        var greenStart = mode == MeterDisplayMode.Vu ? 0.160 : 0.31;
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#ef4444"), 0.00));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#ef4444"), redEnd));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#facc15"), yellowStart));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#facc15"), yellowEnd));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#22c55e"), greenStart));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#22c55e"), 1.00));
        brush.Freeze();
        return brush;
    }

    private void AddAudioMeterColorZones(Canvas canvas)
    {
        var zones = _nextMeterMode == MeterDisplayMode.Vu
            ? new (double TopDb, double BottomDb, string Color, double Opacity)[]
            {
                (-15, -18, "#ef4444", 0.28),
                (-18, -25, "#facc15", 0.22),
                (-25, -78, "#22c55e", 0.18)
            }
            : AudioMeterColorZones;
        foreach (var (topDb, bottomDb, color, opacity) in zones)
        {
            var top = AudioMeterYForDb(topDb);
            var bottom = AudioMeterYForDb(bottomDb);
            var zone = new Border
            {
                Width = AudioMeterBarWidth,
                Height = Math.Max(1, bottom - top),
                Background = BrushFromHex(color),
                Opacity = opacity,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(zone, (AudioMeterTrackWidth - AudioMeterBarWidth) / 2);
            Canvas.SetTop(zone, Math.Round(top));
            canvas.Children.Add(zone);
        }
    }

    private void AddAudioMeterGrid(Canvas canvas)
    {
        foreach (var meterTick in AudioMeterScale.GetTicks(_nextMeterMode))
        {
            var major = IsNextMeterMajorTick(meterTick.DisplayValue, _nextMeterMode);
            var y = AudioMeterHeight - AudioMeterHeight * meterTick.Ratio;
            var tick = new Border
            {
                Width = major ? AudioMeterTrackWidth : AudioMeterTrackWidth - 4,
                Height = 1,
                Background = major ? BrushFromHex("#7c8ba3") : BrushFromHex("#344155"),
                Opacity = major ? 0.72 : 0.48
            };
            Canvas.SetLeft(tick, 0);
            Canvas.SetTop(tick, Math.Round(y));
            canvas.Children.Add(tick);
        }
    }

    private FrameworkElement CreateAudioMeterScale(bool labelsOnRight)
    {
        var canvas = new Canvas
        {
            Width = AudioMeterScaleWidth,
            Height = AudioMeterHeight + 14,
            Margin = labelsOnRight ? new Thickness(3, 15, 0, 0) : new Thickness(0, 15, 3, 0)
        };
        foreach (var meterTick in AudioMeterScale.GetTicks(_nextMeterMode).Where(tick => IsNextMeterMajorTick(tick.DisplayValue, _nextMeterMode)))
        {
            var y = AudioMeterHeight - AudioMeterHeight * meterTick.Ratio;
            AddAudioMeterScaleTick(canvas, labelsOnRight, y, FormatNextMeterTick(meterTick.DisplayValue));
        }

        return canvas;
    }

    private static void AddAudioMeterScaleTick(Canvas canvas, bool labelsOnRight, double y, string text)
    {
        var tick = new Border
        {
            Width = 5,
            Height = 1,
            Background = BrushFromHex("#94a3b8")
        };
        Canvas.SetLeft(tick, labelsOnRight ? 0 : AudioMeterScaleWidth - 5);
        Canvas.SetTop(tick, Math.Round(y));
        canvas.Children.Add(tick);

        var label = new TextBlock
        {
            Text = text,
            Foreground = BrushFromHex("#dbeafe"),
            FontSize = 9,
            LineHeight = 9,
            TextAlignment = labelsOnRight ? TextAlignment.Left : TextAlignment.Right,
            Width = AudioMeterScaleWidth - 7
        };
        Canvas.SetLeft(label, labelsOnRight ? 7 : 0);
        Canvas.SetTop(label, Math.Clamp(y - 5, 0, AudioMeterHeight - 5));
        canvas.Children.Add(label);
    }

    private double AudioMeterYForDb(double db)
    {
        var ratio = AudioMeterScale.GetRatio(db, _nextMeterMode);
        return AudioMeterHeight - AudioMeterHeight * ratio;
    }

    private static Button OverlayActionButton(string text)
    {
        return new Button
        {
            Content = text,
            MinWidth = 34,
            Height = 22,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Background = BrushFromHex("#33111111"),
            BorderBrush = BrushFromHex("#66ffffff"),
            BorderThickness = new Thickness(1)
        };
    }

    private static string AlertShortType(string type)
    {
        return type switch
        {
            "video.no_signal" => "NO INPUT",
            "video.decode_error" => "DECODE",
            "video.black" => "BLACK",
            "video.freeze" => "FREEZE",
            "video.low_fps" => "LOW FPS",
            "audio.silence" => "NO AUDIO",
            "audio.clipping" => "CLIP",
            "audio.channel_missing" => "NO TRACK",
            _ => type.ToUpperInvariant()
        };
    }

    private static string AlertBackgroundFor(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "critical" or "error" => "#e01515",
            "warning" => "#d99a12",
            _ => "#2563eb"
        };
    }

    internal void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _config.GlobalMute = GlobalMuteCheck.IsChecked == true;
        _config.AlertSoundEnabled = AlertSoundCheck.IsChecked == true;
        _config.ServerUrl = ServerUrlText.Text.Trim().TrimEnd('/');
        _config.ClientToken = ClientTokenBox.Password.Trim();
        _config.ServerSyncEnabled = ServerSyncCheck.IsChecked == true;
        LocalConfigStore.FlushQueuedSave(_config);
        UpdateFooter($"Saved {LocalConfigStore.DefaultConfigPath}");
    }

    internal void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LocalConfigStore.FlushQueuedSave(_config);
        LoadConfig();
    }

    internal void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import IP MultiViewer config",
            Filter = "JSON config (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _isLoading = true;
            _config = LocalConfigStore.LoadFrom(dialog.FileName);
            ConfigureNextUiFromConfig();
            EnsureStandardLayouts();
            LocalConfigStore.FlushQueuedSave(_config);
            StreamsList.ItemsSource = _config.Streams;
            LayoutCombo.ItemsSource = _config.Layouts;
            LayoutCombo.SelectedItem = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
                ?? _config.Layouts.FirstOrDefault();
            StreamsList.SelectedIndex = _config.Streams.Count > 0 ? 0 : -1;
            GlobalMuteCheck.IsChecked = _config.GlobalMute;
            AlertSoundCheck.IsChecked = _config.AlertSoundEnabled;
            ServerUrlText.Text = _config.ServerUrl;
            ClientTokenBox.Password = _config.ClientToken;
            ServerSyncCheck.IsChecked = _config.ServerSyncEnabled;
            RenderAlerts();
            RenderTiles();
            UpdateFooter($"Imported {dialog.FileName}");
        }
        catch (Exception ex)
        {
            UpdateFooter($"Import failed: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
        }
    }

    internal void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export IP MultiViewer config",
            Filter = "JSON config (*.json)|*.json|All files (*.*)|*.*",
            FileName = "ip-multiviewer-config.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            LocalConfigStore.FlushQueuedSave(_config);
            LocalConfigStore.SaveTo(_config, dialog.FileName);
            UpdateFooter($"Exported {dialog.FileName}");
        }
        catch (Exception ex)
        {
            UpdateFooter($"Export failed: {ex.Message}");
        }
    }

    internal void GlobalMuteCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_config is null)
        {
            return;
        }

        _config.GlobalMute = GlobalMuteCheck.IsChecked == true;
        ApplyMuteState();
        UpdateFooter(_config.GlobalMute ? "Global mute enabled. Click Save to persist." : "Global mute disabled. Click Save to persist.");
    }

    internal void AlertSoundCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_config is null)
        {
            return;
        }

        _config.AlertSoundEnabled = AlertSoundCheck.IsChecked == true;
        LocalConfigStore.QueueSave(_config);
        UpdateContinuousAlertSound();
        UpdateFooter(_config.AlertSoundEnabled ? "Alert sound enabled." : "Alert sound muted.");
    }

    internal void ApplyServerButton_Click(object sender, RoutedEventArgs e)
    {
        var serverUrl = ServerUrlText.Text.Trim().TrimEnd('/');
        if (ServerSyncCheck.IsChecked == true &&
            (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            UpdateFooter("Server URL must be an absolute http or https URL.");
            return;
        }

        _config.ServerUrl = serverUrl;
        _config.ClientToken = ClientTokenBox.Password.Trim();
        _config.ServerSyncEnabled = ServerSyncCheck.IsChecked == true;
        LocalConfigStore.QueueSave(_config);
        SetTextIfChanged(ModeText, _config.ServerSyncEnabled ? $"Server sync: {_config.ServerUrl}" : "Standalone local mode");
        SetTextIfChanged(ServerSyncStatusText, _config.ServerSyncEnabled ? "server sync: enabled" : "server sync: disabled");
        SetTextIfChanged(ServerClientSummaryText, _config.ServerSyncEnabled ? "server clients: pending" : "server clients: unavailable");
        _ = SyncWithServerAsync(force: true);
    }

    private async void StartPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        await StartPlaybackForActiveLayoutStreamsAsync("Starting playback", restartUnhealthy: true);
    }

    private void QueueInitialPlaybackStart()
    {
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            await System.Threading.Tasks.Task.Delay(500);
            await StartPlaybackForActiveLayoutStreamsAsync("Auto-starting receivers", restartUnhealthy: false);
        }), DispatcherPriority.ApplicationIdle);
    }

    private static bool InitialPlaybackDisabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("IPMV_DISABLE_INITIAL_PLAYBACK"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    private async System.Threading.Tasks.Task StartPlaybackForActiveLayoutStreamsAsync(string actionLabel, bool restartUnhealthy)
    {
        if (_playbackStartInFlight)
        {
            return;
        }

        var streamIds = ActiveLayoutStreams()
            .Where(stream => stream.Enabled && CanPlayStream(stream))
            .Select(stream => stream.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var started = 0;
        var skipped = 0;
        var failed = 0;
        string? lastError = null;
        var operationGeneration = _playbackOperationGeneration;

        foreach (var streamId in streamIds)
        {
            _monitoringRequestedStreamIds.Add(streamId);
        }

        _playbackStartInFlight = true;
        StartPlaybackButton.IsEnabled = false;
        StopPlaybackButton.IsEnabled = true;
        UpdateFooter($"{actionLabel} for {streamIds.Count} layout streams...");

        try
        {
            var ffmpeg = FindFfmpeg();
            if (!string.IsNullOrWhiteSpace(ffmpeg) && streamIds.Any(streamId =>
                {
                    var stream = CurrentActivePlayableStream(streamId);
                    return stream is not null &&
                        ShouldRouteThroughFfmpegPlayback(stream) &&
                        ShouldStartPlayback(stream.Id, restartUnhealthy);
                }))
            {
                await System.Threading.Tasks.Task.Run(() => FfmpegPlaybackSession.WarmUpCapabilities(ffmpeg));
            }

            async System.Threading.Tasks.Task LaunchStreamAsync(string streamId)
            {
                try
                {
                    var launched = await StartStreamPlaybackCoordinatedAsync(
                        streamId,
                        restartUnhealthy,
                        expectedOperationGeneration: operationGeneration,
                        allowDuringBatch: true);
                    if (!IsPlaybackOperationCurrent(operationGeneration))
                    {
                        return;
                    }

                    if (launched)
                    {
                        started++;
                        if (restartUnhealthy)
                        {
                            _playbackRestartAttempts.Remove(streamId);
                        }
                    }
                    else
                    {
                        skipped++;
                    }

                    UpdateFooter($"{actionLabel}: {started} started, {skipped} skipped, {failed} failed...");
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    failed++;
                    if (_monitoringRequestedStreamIds.Contains(streamId))
                    {
                        if (IsPlaybackLoaded(streamId))
                        {
                            StopStreamPlayback(streamId, resetRestartBackoff: false);
                        }
                        _playbackSnapshots[streamId] = EmptyPlaybackSnapshot("error");
                        MarkPlaybackRestartAfterFailure(streamId);
                        var stream = CurrentActivePlayableStream(streamId);
                        if (stream is not null)
                        {
                            RefreshPlaybackSurface(stream);
                            UpdateTileChrome(stream);
                        }
                    }
                    AppendDesktopLog($"Start playback failed for {streamId}: {ex}");
                }
            }

            await System.Threading.Tasks.Task.WhenAll(streamIds.Select(LaunchStreamAsync));
            if (!IsPlaybackOperationCurrent(operationGeneration))
            {
                return;
            }

            UpdateAllTileChrome();
            UpdatePlaybackSummary();
            var summary = $"{actionLabel} complete: {started} started, {skipped} skipped, {failed} failed.";
            UpdateFooter(lastError is null ? summary : $"{summary} Last error: {lastError}");
        }
        finally
        {
            _playbackStartInFlight = false;
            StartPlaybackButton.IsEnabled = true;
            StopPlaybackButton.IsEnabled = true;
        }
    }

    private bool ShouldStartPlayback(string streamId, bool restartUnhealthy)
    {
        if (!IsPlaybackLoaded(streamId))
        {
            return true;
        }

        if (!restartUnhealthy || !_playbackSnapshots.TryGetValue(streamId, out var snapshot))
        {
            return false;
        }

        if (ShouldRestartLoadedPlayback(snapshot.State, restartUnhealthy))
        {
            return true;
        }

        var stream = _config.Streams.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, streamId, StringComparison.OrdinalIgnoreCase));
        return stream is not null && ReceiverNeedsRestart(streamId, snapshot, AlertProfileFor(stream));
    }

    internal static bool ShouldRestartLoadedPlayback(string state, bool restartUnhealthy)
    {
        return restartUnhealthy && IsRecoverablePlaybackState(state);
    }

    private StreamConfig? CurrentActivePlayableStream(string streamId)
    {
        var stream = _config.Streams.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, streamId, StringComparison.OrdinalIgnoreCase));
        return stream is not null &&
            stream.Enabled &&
            CanPlayStream(stream) &&
            IsStreamInActiveLayout(stream.Id)
                ? stream
                : null;
    }

    private async System.Threading.Tasks.Task<bool> StartStreamPlaybackCoordinatedAsync(
        string streamId,
        bool restartUnhealthy,
        object? expectedOwner = null,
        bool forceRestart = false,
        long? expectedOperationGeneration = null,
        bool allowDuringBatch = false)
    {
        await _playbackStartupSlots.WaitAsync();
        var startupSlotHeld = true;
        object? startedOwner = null;
        try
        {
            await _playbackLaunchGate.WaitAsync();
            try
            {
                if (ResolvePlaybackStartStream(
                        streamId,
                        restartUnhealthy,
                        expectedOwner,
                        forceRestart,
                        expectedOperationGeneration,
                        allowDuringBatch) is null)
                {
                    return false;
                }

                var launchDelay = _nextPlaybackLaunchAt - DateTimeOffset.UtcNow;
                if (launchDelay > TimeSpan.Zero)
                {
                    await System.Threading.Tasks.Task.Delay(launchDelay);
                }

                var stream = ResolvePlaybackStartStream(
                    streamId,
                    restartUnhealthy,
                    expectedOwner,
                    forceRestart,
                    expectedOperationGeneration,
                    allowDuringBatch);
                if (stream is null)
                {
                    return false;
                }

                try
                {
                    if (_playbackStarterOverride is not null)
                    {
                        await _playbackStarterOverride(stream);
                    }
                    else
                    {
                        StartStreamPlayback(stream);
                    }
                    startedOwner = PlaybackOwnerFor(streamId);
                }
                finally
                {
                    _nextPlaybackLaunchAt = DateTimeOffset.UtcNow.Add(PlaybackBatchStartSpacing);
                }
            }
            finally
            {
                _playbackLaunchGate.Release();
            }

            if (startedOwner is null)
            {
                return false;
            }

            // Bound concurrent process/player creation, but never hold a slot while
            // an external source is connecting or waiting to send its first frame.
            await WaitForPlaybackOwnerStartAttemptAsync(
                streamId,
                startedOwner,
                expectedOperationGeneration);
            _playbackStartupSlots.Release();
            startupSlotHeld = false;

            var startupOutcome = await WaitForPlaybackStartupOutcomeAsync(
                streamId,
                startedOwner,
                expectedOperationGeneration);
            if (startupOutcome == PlaybackStartupOutcome.Cancelled)
            {
                if (ReferenceEquals(PlaybackOwnerFor(streamId), startedOwner))
                {
                    StopStreamPlayback(streamId, resetRestartBackoff: false);
                }

                return false;
            }

            if (startupOutcome == PlaybackStartupOutcome.Terminal)
            {
                var terminalState = PlaybackOwnerState(startedOwner);
                if (ReferenceEquals(PlaybackOwnerFor(streamId), startedOwner))
                {
                    StopStreamPlayback(streamId, resetRestartBackoff: false);
                    if (_monitoringRequestedStreamIds.Contains(streamId))
                    {
                        _playbackSnapshots[streamId] = EmptyPlaybackSnapshot(
                            string.Equals(terminalState, "error", StringComparison.OrdinalIgnoreCase)
                                ? "error"
                                : "retrying");
                        MarkPlaybackRestartAfterFailure(streamId);
                        var stream = CurrentActivePlayableStream(streamId);
                        if (stream is not null)
                        {
                            RefreshPlaybackSurface(stream);
                            UpdateTileChrome(stream);
                        }
                    }
                }

                AppendDesktopLog(
                    $"Playback startup for {streamId} entered terminal state '{terminalState}'; monitoring remains enabled and a retry was scheduled.");
                return _monitoringRequestedStreamIds.Contains(streamId);
            }

            return true;
        }
        finally
        {
            if (startupSlotHeld)
            {
                _playbackStartupSlots.Release();
            }
        }
    }

    private async System.Threading.Tasks.Task WaitForPlaybackOwnerStartAttemptAsync(
        string streamId,
        object expectedOwner,
        long? expectedOperationGeneration)
    {
        var deadline = DateTimeOffset.UtcNow.Add(PlaybackOwnerStartAttemptTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((expectedOperationGeneration is long generation && !IsPlaybackOperationCurrent(generation)) ||
                !ReferenceEquals(PlaybackOwnerFor(streamId), expectedOwner) ||
                PlaybackOwnerStartAttempted(expectedOwner))
            {
                return;
            }

            await System.Threading.Tasks.Task.Delay(PlaybackStartupPollInterval);
        }
    }

    private static bool PlaybackOwnerStartAttempted(object owner)
    {
        try
        {
            return owner switch
            {
                FfmpegPlaybackSession session => session.IsProcessRunning || IsPlaybackStartupFailed(session.State),
                VlcMediaPlayer player => player.State != VLCState.NothingSpecial,
                _ => true
            };
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private StreamConfig? ResolvePlaybackStartStream(
        string streamId,
        bool restartUnhealthy,
        object? expectedOwner,
        bool forceRestart,
        long? expectedOperationGeneration,
        bool allowDuringBatch)
    {
        if ((expectedOperationGeneration is long generation && !IsPlaybackOperationCurrent(generation)) ||
            !_monitoringRequestedStreamIds.Contains(streamId) ||
            (!allowDuringBatch && _playbackStartInFlight))
        {
            return null;
        }

        var stream = CurrentActivePlayableStream(streamId);
        if (stream is null ||
            (expectedOwner is not null && !PlaybackOwnerMatches(streamId, expectedOwner)) ||
            (!forceRestart && !ShouldStartPlayback(streamId, restartUnhealthy)) ||
            (forceRestart && (!stream.AnalysisEnabled || !ShouldUseFfmpegPlayback(stream))))
        {
            return null;
        }

        return stream;
    }

    private async System.Threading.Tasks.Task<PlaybackStartupOutcome> WaitForPlaybackStartupOutcomeAsync(
        string streamId,
        object expectedOwner,
        long? expectedOperationGeneration)
    {
        var deadline = DateTimeOffset.UtcNow.Add(PlaybackStartupSlotTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (expectedOperationGeneration is long generation && !IsPlaybackOperationCurrent(generation))
            {
                return PlaybackStartupOutcome.Cancelled;
            }

            var currentOwner = PlaybackOwnerFor(streamId);
            if (!ReferenceEquals(currentOwner, expectedOwner))
            {
                return currentOwner is not null
                    ? PlaybackStartupOutcome.Superseded
                    : PlaybackStartupOutcome.Cancelled;
            }

            var (state, hasReceivedFrame) = expectedOwner switch
            {
                FfmpegPlaybackSession session => (session.State, session.HasFrame),
                VlcMediaPlayer player => (player.State.ToString(), false),
                _ => ("unknown", false)
            };
            if (IsPlaybackStartupFailed(state))
            {
                AppendDesktopLog($"Playback startup for {streamId} entered terminal state '{state}' before input became ready.");
                return PlaybackStartupOutcome.Terminal;
            }

            if (IsPlaybackStartupReady(state, hasReceivedFrame))
            {
                return PlaybackStartupOutcome.Ready;
            }

            var stream = _config.Streams.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, streamId, StringComparison.OrdinalIgnoreCase));
            if (stream is not null && IsPlaybackStartupArmed(stream, expectedOwner, state))
            {
                AppendDesktopLog($"Playback receiver for {streamId} is armed and waiting for input.");
                return PlaybackStartupOutcome.Armed;
            }

            await System.Threading.Tasks.Task.Delay(PlaybackStartupPollInterval);
        }

        if (expectedOperationGeneration is long finalGeneration && !IsPlaybackOperationCurrent(finalGeneration))
        {
            return PlaybackStartupOutcome.Cancelled;
        }

        var finalOwner = PlaybackOwnerFor(streamId);
        if (ReferenceEquals(finalOwner, expectedOwner))
        {
            AppendDesktopLog(
                $"Playback startup observation window elapsed for {streamId}; retained the connection attempt.");
            return PlaybackStartupOutcome.Pending;
        }

        return finalOwner is not null
            ? PlaybackStartupOutcome.Superseded
            : PlaybackStartupOutcome.Cancelled;
    }

    private static string PlaybackOwnerState(object owner)
    {
        return owner switch
        {
            FfmpegPlaybackSession session => session.State,
            VlcMediaPlayer player => player.State.ToString().ToLowerInvariant(),
            _ => "unknown"
        };
    }

    private static bool IsPlaybackStartupArmed(StreamConfig stream, object owner, string state)
    {
        if (!IsPassiveReceiverInput(stream.InputUrl, stream.InputProtocol) || IsPlaybackStartupFailed(state))
        {
            return false;
        }

        return owner switch
        {
            FfmpegPlaybackSession session => session.IsProcessRunning,
            VlcMediaPlayer => state.Equals("opening", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("buffering", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("playing", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    internal static bool IsPassiveReceiverInput(string inputUrl, string protocol)
    {
        var normalizedProtocol = (protocol ?? "").Trim().ToLowerInvariant();
        var scheme = Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri)
            ? uri.Scheme.ToLowerInvariant()
            : "";
        if (scheme == "srt" || normalizedProtocol.StartsWith("srt", StringComparison.Ordinal))
        {
            var srtMode = FfmpegPlaybackSession.EffectiveSrtMode(inputUrl, normalizedProtocol);
            return srtMode is "listener" or "rendezvous";
        }

        return scheme is "udp" or "rtp" ||
            normalizedProtocol is "udp" or "udp-listener" or "rtp" or "rtp-listener";
    }

    internal static bool IsPlaybackStartupReady(string state, bool hasReceivedFrame)
    {
        return !IsRecoverablePlaybackState(state) &&
            (hasReceivedFrame || string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsPlaybackStartupFailed(string state) => IsRecoverablePlaybackState(state);

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        var startWasInFlight = _playbackStartInFlight;
        var stopped = _players.Keys
            .Concat(_ffmpegPlaybackSessions.Keys)
            .Concat(_monitoringRequestedStreamIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        StopAllPlayback();
        RefreshPlaybackSurfaces();
        UpdateAllTileChrome();
        UpdatePlaybackSummary();
        UpdateFooter(startWasInFlight
            ? $"Playback start cancelled; stopped {stopped} streams."
            : $"Playback stopped: {stopped} streams.");
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenAlertHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _alertHistoryWindow ??= new AlertHistoryWindow(this);
        _alertHistoryWindow.RefreshAlerts();
        if (_alertHistoryWindow.IsVisible)
        {
            _alertHistoryWindow.Activate();
            return;
        }

        _alertHistoryWindow.Show();
        _alertHistoryWindow.Activate();
    }

    private void MainLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _layoutSelectionInProgress || MainLayoutCombo.SelectedItem is not LayoutConfig layout)
        {
            return;
        }

        ApplyLayoutSelection(layout);
    }

    private void TileAssignButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int tileIndex)
        {
            return;
        }

        var menu = new ContextMenu();
        var unassigned = new MenuItem { Header = "Unassigned" };
        unassigned.Click += (_, _) => AssignTileToStream(tileIndex, "");
        menu.Items.Add(unassigned);
        menu.Items.Add(new Separator());

        foreach (var stream in _config.Streams.OrderBy(stream => stream.Name))
        {
            var item = new MenuItem
            {
                Header = stream.Name,
                IsCheckable = true,
                IsChecked = IsStreamAssignedToTile(tileIndex, stream.Id)
            };
            item.Click += (_, _) => AssignTileToStream(tileIndex, stream.Id);
            menu.Items.Add(item);
        }

        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    internal void StreamsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateStreamEditor(StreamsList.SelectedItem as StreamConfig);
    }

    internal void ApplyStreamButton_Click(object sender, RoutedEventArgs e)
    {
        if (StreamsList.SelectedItem is not StreamConfig stream)
        {
            UpdateFooter("Select a stream before applying changes.");
            return;
        }

        var name = StreamNameText.Text.Trim();
        var protocol = StreamProtocolText.Text.Trim().ToLowerInvariant();
        var inputUrl = StreamUrlText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(protocol))
        {
            UpdateFooter("Stream name and protocol are required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(inputUrl))
        {
            UpdateFooter("Input URL is required.");
            return;
        }

        stream.Name = name;
        stream.InputProtocol = protocol;
        stream.Group = StreamGroupText.Text.Trim();
        stream.Tags = StreamTagsText.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        stream.Enabled = StreamEnabledCheck.IsChecked == true;
        stream.AnalysisEnabled = StreamAnalysisCheck.IsChecked == true;
        stream.AudioMonitorEnabled = StreamAudioCheck.IsChecked == true;
        stream.AlertMuted = StreamAlertMuteCheck.IsChecked == true;
        stream.AlertProfileId = (StreamAlertProfileCombo.SelectedItem as AlertProfileConfig)?.Id
            ?? _config.AlertProfiles.FirstOrDefault()?.Id
            ?? "default";
        if (!stream.Enabled)
        {
            StopStreamPlayback(stream.Id);
        }
        else if (IsPlaybackLoaded(stream.Id))
        {
            if (stream.AudioMonitorEnabled)
            {
                StartAudioMonitor(stream);
            }
            else
            {
                StopAudioMonitor(stream.Id);
            }

            if (stream.AnalysisEnabled)
            {
                StartVideoMonitor(stream);
            }
            else
            {
                StopVideoMonitor(stream.Id);
            }
        }

        if (!LooksMasked(inputUrl))
        {
            if (!string.Equals(stream.InputUrl, inputUrl, StringComparison.Ordinal))
            {
                StopStreamPlayback(stream.Id);
            }

            stream.InputUrl = inputUrl;
        }

        LocalConfigStore.QueueSave(_config);
        StreamsList.Items.Refresh();
        RenderAlerts();
        if (IsStreamInActiveLayout(stream.Id))
        {
            UpdateTileTitle(stream);
            RefreshPlaybackSurface(stream);
            UpdateTileChrome(stream);
        }

        ReconcileActiveLayoutMonitoring();
        UpdateFooter($"Updated {stream.Name}.");
    }

    internal void AddStreamButton_Click(object sender, RoutedEventArgs e)
    {
        var stream = new StreamConfig
        {
            Id = $"stream-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            Name = $"New Stream {_config.Streams.Count + 1}",
            InputUrl = "rtsp://user:<PASSWORD>@camera.example.com/live",
            InputProtocol = "rtsp",
            Group = "Default",
            Tags = ["new"]
        };

        _config.Streams.Add(stream);
        AssignStreamToFirstEmptyTile(stream.Id);
        LocalConfigStore.QueueSave(_config);
        StreamsList.SelectedItem = stream;
        RenderAlerts();
        RenderTiles();
        UpdateFooter($"Added {stream.Name}.");
    }

    internal void DeleteStreamButton_Click(object sender, RoutedEventArgs e)
    {
        if (StreamsList.SelectedItem is not StreamConfig stream)
        {
            UpdateFooter("Select a stream before deleting.");
            return;
        }

        StopStreamPlayback(stream.Id);
        _config.Streams.Remove(stream);
        foreach (var layout in _config.Layouts)
        {
            foreach (var tile in layout.Tiles.Where(tile => tile.StreamId == stream.Id))
            {
                tile.StreamId = "";
            }
        }

        var staleAlerts = _config.AlertHistory.Where(alert => alert.StreamId == stream.Id).ToList();
        foreach (var alert in staleAlerts)
        {
            _config.AlertHistory.Remove(alert);
        }

        LocalConfigStore.QueueSave(_config);
        StreamsList.SelectedIndex = _config.Streams.Count > 0 ? 0 : -1;
        RenderAlerts();
        RenderTiles();
        UpdateFooter($"Deleted {stream.Name}.");
    }

    internal void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _layoutSelectionInProgress || LayoutCombo.SelectedItem is not LayoutConfig layout)
        {
            return;
        }

        ApplyLayoutSelection(layout);
    }

    private void ApplyLayoutSelection(LayoutConfig layout)
    {
        if (_layoutSelectionInProgress)
        {
            return;
        }

        _layoutSelectionInProgress = true;
        try
        {
            _config.ActiveLayoutId = layout.Id;
            if (!ReferenceEquals(MainLayoutCombo.SelectedItem, layout))
            {
                MainLayoutCombo.SelectedItem = layout;
            }

            if (!ReferenceEquals(LayoutCombo.SelectedItem, layout))
            {
                LayoutCombo.SelectedItem = layout;
            }

            LocalConfigStore.QueueSave(_config);
            RenderTiles();
            ReconcileActiveLayoutMonitoring();
            UpdatePlaybackSummary();
            UpdateFooter($"Layout changed to {layout.Name}.");
        }
        finally
        {
            _layoutSelectionInProgress = false;
        }
    }

    private void UpdateFooter(string message)
    {
        SetTextIfChanged(FooterText, $"{message} Logs: {LocalConfigStore.LogDirectory}");
    }

    private static void AppendDesktopLog(string message)
    {
        try
        {
            Directory.CreateDirectory(LocalConfigStore.LogDirectory);
            var path = Path.Combine(LocalConfigStore.LogDirectory,
                "desktop.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never block monitoring operations.
        }
    }

    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("FFMPEG_BIN");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        if (string.Equals(configured, "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var path in paths)
        {
            var candidate = Path.Combine(path, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private FrameworkElement CreatePlaybackSurface(StreamConfig? stream)
    {
        var surface = new Grid
        {
            Background = Brushes.Black,
            ClipToBounds = true
        };

        SetPlaybackSurface(surface, stream);
        return surface;
    }

    private void EnsurePlaybackSurfaceChildren(Grid surface, StreamConfig stream)
    {
        if (ShouldUseLibVlcSurface(stream))
        {
            if (!_tileVideoHosts.TryGetValue(stream.Id, out var host))
            {
                var panel = new FormsPanel
                {
                    BackColor = System.Drawing.Color.Black,
                    Dock = FormsDockStyle.Fill
                };

                host = new WindowsFormsHost
                {
                    Child = panel,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(0),
                    Visibility = Visibility.Hidden,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true
                };
                BindSurfaceChildSize(host, surface);
                _tileVideoHosts[stream.Id] = host;
                _tileVideoPanels[stream.Id] = panel;
                surface.Children.Insert(0, host);
            }
            else if (!surface.Children.Contains(host))
            {
                surface.Children.Insert(0, host);
            }
        }
        else if (_tileVideoHosts.ContainsKey(stream.Id))
        {
            DisposeRenderedVideoHost(stream.Id);
        }

        if (!_tileFfmpegImages.TryGetValue(stream.Id, out var image))
        {
            image = new Image
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Hidden
            };
            BindSurfaceChildSize(image, surface);
            _tileFfmpegImages[stream.Id] = image;
            surface.Children.Add(image);
        }
        else if (!surface.Children.Contains(image))
        {
            surface.Children.Add(image);
        }

        if (!_tileSurfacePlaceholders.TryGetValue(stream.Id, out var placeholder))
        {
            placeholder = new TextBlock
            {
                Foreground = BrushFromHex("#6b7280"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _tileSurfacePlaceholders[stream.Id] = placeholder;
            surface.Children.Add(placeholder);
        }
        else if (!surface.Children.Contains(placeholder))
        {
            surface.Children.Add(placeholder);
        }
    }

    private bool ShouldUseLibVlcSurface(StreamConfig stream)
    {
        return _players.ContainsKey(stream.Id) || !ShouldUseFfmpegPlayback(stream);
    }

    private static void BindSurfaceChildSize(FrameworkElement child, FrameworkElement surface)
    {
        BindingOperations.SetBinding(
            child,
            FrameworkElement.WidthProperty,
            new Binding(nameof(ActualWidth)) { Source = surface });
        BindingOperations.SetBinding(
            child,
            FrameworkElement.HeightProperty,
            new Binding(nameof(ActualHeight)) { Source = surface });
    }

    private bool TryGetVideoPanelHandle(string streamId, out IntPtr hwnd)
    {
        if (_tileVideoPanels.TryGetValue(streamId, out var panel))
        {
            panel.CreateControl();
            hwnd = panel.Handle;
            return hwnd != IntPtr.Zero;
        }

        hwnd = IntPtr.Zero;
        return false;
    }

    private void SetPlaybackSurface(Grid surface, StreamConfig? stream)
    {
        if (stream is null)
        {
            surface.Children.Clear();
            surface.Children.Add(new TextBlock
            {
                Text = "No stream assigned",
                Foreground = BrushFromHex("#6b7280"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        EnsurePlaybackSurfaceChildren(surface, stream);
        var hasPlayer = _players.TryGetValue(stream.Id, out var player);
        var hasFfmpegSession = _ffmpegPlaybackSessions.TryGetValue(stream.Id, out var ffmpegSession);
        var monitoringRequested = _monitoringRequestedStreamIds.Contains(stream.Id);
        var snapshotAvailable = _playbackSnapshots.TryGetValue(stream.Id, out var snapshot);
        var clearPlaybackSurface = snapshotAvailable && ShouldClearPlaybackSurface(stream, snapshot!);
        var keepVlcVideoAttached = hasPlayer && !clearPlaybackSurface;
        var showVlcVideo = keepVlcVideoAttached &&
            snapshotAvailable &&
            HasPresentedVlcVideo(stream.Id, snapshot!) &&
            IsPlaybackVideoFreshForSurface(stream, snapshot!);
        var showFfmpegVideo = hasFfmpegSession &&
            snapshotAvailable &&
            string.Equals(snapshot!.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            ffmpegSession!.HasPresentedFrame &&
            !IsPlaybackVideoStaleForSurface(stream.Id, AlertProfileFor(stream));
        var surfaceKey = string.Join("|",
            hasPlayer ? "vlc" : "no-vlc",
            hasPlayer ? player!.GetHashCode().ToString(CultureInfo.InvariantCulture) : "no-player-ref",
            hasFfmpegSession ? "ffmpeg" : "no-ffmpeg",
            hasFfmpegSession ? ffmpegSession!.GetHashCode().ToString(CultureInfo.InvariantCulture) : "no-ffmpeg-ref",
            snapshotAvailable ? snapshot!.State : "no-snapshot",
            clearPlaybackSurface ? "clear-surface" : "keep-surface",
            showVlcVideo ? "has-vlc-presented-video" : "no-vlc-presented-video",
            showVlcVideo ? "show-vlc" : "hide-vlc",
            showFfmpegVideo ? "show-ffmpeg" : "hide-ffmpeg",
            hasFfmpegSession && ffmpegSession!.HasPresentedFrame ? "has-presented-frame" : "no-presented-frame",
            monitoringRequested ? "monitoring-requested" : "monitoring-stopped");
        if (_tileSurfaceRenderKeys.TryGetValue(stream.Id, out var previousSurfaceKey) &&
            string.Equals(previousSurfaceKey, surfaceKey, StringComparison.Ordinal))
        {
            return;
        }

        _tileSurfaceRenderKeys[stream.Id] = surfaceKey;
        if (_tileVideoHosts.TryGetValue(stream.Id, out var videoHost))
        {
            if (keepVlcVideoAttached &&
                player is not null &&
                TryGetVideoPanelHandle(stream.Id, out var hwnd) &&
                player.Hwnd != hwnd)
            {
                player.Hwnd = hwnd;
            }

            videoHost.Visibility = keepVlcVideoAttached ? Visibility.Visible : Visibility.Hidden;
        }

        if (_tileFfmpegImages.TryGetValue(stream.Id, out var image))
        {
            var desiredSource = hasFfmpegSession ? ffmpegSession!.Bitmap : null;
            if (!ReferenceEquals(image.Source, desiredSource))
            {
                image.Source = desiredSource;
            }

            image.Visibility = showFfmpegVideo ? Visibility.Visible : Visibility.Hidden;
        }

        if (_tileSurfacePlaceholders.TryGetValue(stream.Id, out var placeholder))
        {
            var loaded = hasPlayer || hasFfmpegSession;
            var placeholderText = clearPlaybackSurface
                ? "No signal"
                : loaded
                    ? "Playback starting"
                    : monitoringRequested
                        ? "Monitoring / retrying"
                        : "Playback stopped";
            SetTextIfChanged(placeholder, placeholderText);
            placeholder.Visibility = showVlcVideo || showFfmpegVideo ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void RefreshPlaybackSurfaces()
    {
        foreach (var stream in ActiveLayoutStreams())
        {
            RefreshPlaybackSurface(stream);
        }
    }

    private void RefreshPlaybackSurface(StreamConfig stream)
    {
        if (_tileSurfaceHosts.TryGetValue(stream.Id, out var host))
        {
            SetPlaybackSurface(host, stream);
        }
    }

    private void PopulateStreamEditor(StreamConfig? stream)
    {
        var enabled = stream is not null;
        StreamNameText.IsEnabled = enabled;
        StreamUrlText.IsEnabled = enabled;
        StreamProtocolText.IsEnabled = enabled;
        StreamGroupText.IsEnabled = enabled;
        StreamTagsText.IsEnabled = enabled;
        StreamAlertProfileCombo.IsEnabled = enabled;
        StreamEnabledCheck.IsEnabled = enabled;
        StreamAnalysisCheck.IsEnabled = enabled;
        StreamAudioCheck.IsEnabled = enabled;
        StreamAlertMuteCheck.IsEnabled = enabled;
        ApplyStreamButton.IsEnabled = enabled;

        StreamNameText.Text = stream?.Name ?? "";
        StreamUrlText.Text = stream is null ? "" : MaskStreamUrl(stream.InputUrl);
        StreamProtocolText.Text = stream?.InputProtocol ?? "";
        StreamGroupText.Text = stream?.Group ?? "";
        StreamTagsText.Text = stream is null ? "" : string.Join(", ", stream.Tags);
        StreamEnabledCheck.IsChecked = stream?.Enabled == true;
        StreamAnalysisCheck.IsChecked = stream?.AnalysisEnabled == true;
        StreamAudioCheck.IsChecked = stream?.AudioMonitorEnabled == true;
        StreamAlertMuteCheck.IsChecked = stream?.AlertMuted == true;
        StreamAlertProfileCombo.SelectedItem = stream is null
            ? null
            : _config.AlertProfiles.FirstOrDefault(profile => string.Equals(profile.Id, stream.AlertProfileId, StringComparison.OrdinalIgnoreCase))
                ?? _config.AlertProfiles.FirstOrDefault();
        DeleteStreamButton.IsEnabled = enabled;
    }

    internal void AlertProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        PopulateAlertProfileEditor(AlertProfileCombo.SelectedItem as AlertProfileConfig);
    }

    internal void AddAlertProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = CopyAlertProfile(new AlertProfileConfig(), CreateAlertProfileId(), UniqueAlertProfileName("New profile"));
        _config.AlertProfiles.Add(profile);
        RefreshAlertProfileSelectors(profile.Id);
        LocalConfigStore.QueueSave(_config);
        UpdateFooter($"Created alert profile {profile.Name}.");
    }

    internal void DuplicateAlertProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var source = AlertProfileCombo.SelectedItem as AlertProfileConfig ?? _config.AlertProfiles.FirstOrDefault();
        if (source is null)
        {
            AddAlertProfileButton_Click(sender, e);
            return;
        }

        var profile = CopyAlertProfile(source, CreateAlertProfileId(), UniqueAlertProfileName($"{source.Name} copy"));
        _config.AlertProfiles.Add(profile);
        RefreshAlertProfileSelectors(profile.Id);
        LocalConfigStore.QueueSave(_config);
        UpdateFooter($"Duplicated alert profile {source.Name}.");
    }

    internal void DeleteAlertProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (AlertProfileCombo.SelectedItem is not AlertProfileConfig profile)
        {
            UpdateFooter("Select an alert profile first.");
            return;
        }

        if (_config.AlertProfiles.Count <= 1)
        {
            UpdateFooter("Keep at least one alert profile.");
            return;
        }

        _config.AlertProfiles.Remove(profile);
        var replacement = _config.AlertProfiles.FirstOrDefault();
        foreach (var stream in _config.Streams.Where(stream => string.Equals(stream.AlertProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            stream.AlertProfileId = replacement?.Id ?? "default";
        }

        RefreshAlertProfileSelectors(replacement?.Id);
        LocalConfigStore.QueueSave(_config);
        UpdateFooter($"Deleted alert profile {profile.Name}.");
    }

    internal void ApplyAlertProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (AlertProfileCombo.SelectedItem is not AlertProfileConfig profile)
        {
            UpdateFooter("Select an alert profile first.");
            return;
        }

        profile.Name = UniqueAlertProfileName(AlertProfileNameText.Text, profile.Id);
        profile.BlackThreshold = ReadProfileDouble(BlackThresholdText, profile.BlackThreshold, 0, 1);
        profile.BlackPixelThreshold = ReadProfileDouble(BlackPixelThresholdText, profile.BlackPixelThreshold, 0, 1);
        profile.BlackPixelRatioThreshold = ReadProfileDouble(BlackPixelRatioText, profile.BlackPixelRatioThreshold, 0, 1);
        profile.MinBlackDurationSec = 0;
        profile.FreezeDifferenceThreshold = ReadProfileDouble(FreezeDifferenceText, profile.FreezeDifferenceThreshold, 0, 1);
        profile.MinFreezeDurationSec = ReadProfileDouble(MinFreezeDurationText, profile.MinFreezeDurationSec, 0.1, 3600);
        profile.MinFreezeFrames = ReadProfileInt(MinFreezeFramesText, profile.MinFreezeFrames, 1, 100000);
        profile.InputTimeoutSec = ReadProfileDouble(InputTimeoutText, profile.InputTimeoutSec, 1, 3600);
        profile.LowFpsThreshold = ReadProfileDouble(LowFpsThresholdText, profile.LowFpsThreshold, 0.1, 240);
        profile.MinLowFpsDurationSec = ReadProfileDouble(MinLowFpsDurationText, profile.MinLowFpsDurationSec, 0.1, 3600);
        profile.SilenceThresholdDb = ReadProfileDouble(SilenceThresholdText, profile.SilenceThresholdDb, -120, 0);
        profile.MinSilenceDurationSec = ReadProfileDouble(MinSilenceDurationText, profile.MinSilenceDurationSec, 0.1, 3600);
        profile.ClippingCountThreshold = ReadProfileInt(ClippingCountText, profile.ClippingCountThreshold, 1, 1000000);
        LocalConfigStore.QueueSave(_config);
        RefreshAlertProfileSelectors(profile.Id);
        UpdateFooter($"Updated alert profile {profile.Name}.");
    }

    private void PopulateAlertProfileEditor(AlertProfileConfig? profile)
    {
        profile ??= _config.AlertProfiles.FirstOrDefault();
        var enabled = profile is not null;
        AlertProfileNameText.IsEnabled = enabled;
        DuplicateAlertProfileButton.IsEnabled = enabled;
        DeleteAlertProfileButton.IsEnabled = enabled && _config.AlertProfiles.Count > 1;
        foreach (var control in new TextBox[]
        {
            BlackThresholdText,
            BlackPixelThresholdText,
            BlackPixelRatioText,
            FreezeDifferenceText,
            MinFreezeDurationText,
            MinFreezeFramesText,
            InputTimeoutText,
            LowFpsThresholdText,
            MinLowFpsDurationText,
            SilenceThresholdText,
            MinSilenceDurationText,
            ClippingCountText
        })
        {
            control.IsEnabled = enabled;
        }

        if (profile is null)
        {
            AlertProfileNameText.Text = "";
            return;
        }

        AlertProfileNameText.Text = profile.Name;
        BlackThresholdText.Text = FormatProfileNumber(profile.BlackThreshold);
        BlackPixelThresholdText.Text = FormatProfileNumber(profile.BlackPixelThreshold);
        BlackPixelRatioText.Text = FormatProfileNumber(profile.BlackPixelRatioThreshold);
        FreezeDifferenceText.Text = FormatProfileNumber(profile.FreezeDifferenceThreshold);
        MinFreezeDurationText.Text = FormatProfileNumber(profile.MinFreezeDurationSec);
        MinFreezeFramesText.Text = profile.MinFreezeFrames.ToString(CultureInfo.InvariantCulture);
        InputTimeoutText.Text = FormatProfileNumber(profile.InputTimeoutSec);
        LowFpsThresholdText.Text = FormatProfileNumber(profile.LowFpsThreshold);
        MinLowFpsDurationText.Text = FormatProfileNumber(profile.MinLowFpsDurationSec);
        SilenceThresholdText.Text = FormatProfileNumber(profile.SilenceThresholdDb);
        MinSilenceDurationText.Text = FormatProfileNumber(profile.MinSilenceDurationSec);
        ClippingCountText.Text = profile.ClippingCountThreshold.ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshAlertProfileSelectors(string? selectedProfileId)
    {
        var selectedStreamId = (StreamsList.SelectedItem as StreamConfig)?.Id;
        var selectedStream = _config.Streams.FirstOrDefault(stream => string.Equals(stream.Id, selectedStreamId, StringComparison.OrdinalIgnoreCase));
        var selectedProfile = string.IsNullOrWhiteSpace(selectedProfileId)
            ? _config.AlertProfiles.FirstOrDefault()
            : _config.AlertProfiles.FirstOrDefault(profile => string.Equals(profile.Id, selectedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? _config.AlertProfiles.FirstOrDefault();

        AlertProfileCombo.ItemsSource = null;
        StreamAlertProfileCombo.ItemsSource = null;
        AlertProfileCombo.ItemsSource = _config.AlertProfiles;
        StreamAlertProfileCombo.ItemsSource = _config.AlertProfiles;
        AlertProfileCombo.SelectedItem = selectedProfile;
        StreamAlertProfileCombo.SelectedItem = selectedStream is null
            ? selectedProfile
            : _config.AlertProfiles.FirstOrDefault(profile => string.Equals(profile.Id, selectedStream.AlertProfileId, StringComparison.OrdinalIgnoreCase))
                ?? selectedProfile;
        PopulateAlertProfileEditor(selectedProfile);
    }

    private string CreateAlertProfileId()
    {
        string id;
        do
        {
            id = $"profile-{Guid.NewGuid():N}"[..20];
        }
        while (_config.AlertProfiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }

    private string UniqueAlertProfileName(string? requested, string? currentProfileId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "New profile" : requested.Trim();
        var candidate = baseName;
        var index = 2;
        while (_config.AlertProfiles.Any(profile =>
            !string.Equals(profile.Id, currentProfileId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} {index}";
            index++;
        }

        return candidate;
    }

    private static AlertProfileConfig CopyAlertProfile(AlertProfileConfig source, string id, string name)
    {
        return new AlertProfileConfig
        {
            Id = id,
            Name = name,
            BlackThreshold = source.BlackThreshold,
            BlackPixelThreshold = source.BlackPixelThreshold,
            BlackPixelRatioThreshold = source.BlackPixelRatioThreshold,
            MinBlackDurationSec = 0,
            FreezeDifferenceThreshold = source.FreezeDifferenceThreshold,
            MinFreezeDurationSec = source.MinFreezeDurationSec,
            MinFreezeFrames = source.MinFreezeFrames,
            SilenceThresholdDb = source.SilenceThresholdDb,
            MinSilenceDurationSec = source.MinSilenceDurationSec,
            ClippingThresholdDb = source.ClippingThresholdDb,
            ClippingCountThreshold = source.ClippingCountThreshold,
            InputTimeoutSec = source.InputTimeoutSec,
            LowFpsThreshold = source.LowFpsThreshold,
            MinLowFpsDurationSec = source.MinLowFpsDurationSec
        };
    }

    private static string FormatProfileNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static double ReadProfileDouble(TextBox textBox, double fallback, double min, double max)
    {
        return double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private static int ReadProfileInt(TextBox textBox, int fallback, int min, int max)
    {
        return int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    internal IReadOnlyList<AlertRecord> AlertHistorySnapshot()
    {
        return _config.AlertHistory
            .OrderByDescending(alert => ActiveAlertStatuses.Contains(alert.Status))
            .ThenByDescending(alert => alert.UpdatedAt == default ? alert.StartedAt : alert.UpdatedAt)
            .ToList();
    }

    internal void SetAlertStatus(AlertRecord? alert, string status)
    {
        if (alert is null)
        {
            UpdateFooter("Select an alert first.");
            return;
        }

        alert.Status = status;
        alert.SnoozedUntil = string.Equals(status, "snoozed", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _config.AlertSnoozeMinutes))
            : null;
        alert.UpdatedAt = DateTimeOffset.UtcNow;
        alert.SyncedAt = null;
        alert.SourceClientId = _config.ClientId;
        LocalConfigStore.QueueSave(_config);
        RenderAlerts();
        UpdateAllTileChrome();
        UpdateFooter($"Alert {alert.Id} marked as {status}.");
    }

    private Dictionary<string, StreamConfig> StreamLookup()
    {
        var lookup = new Dictionary<string, StreamConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in _config.Streams)
        {
            if (!string.IsNullOrWhiteSpace(stream.Id) && !lookup.ContainsKey(stream.Id))
            {
                lookup[stream.Id] = stream;
            }
        }

        return lookup;
    }

    private List<StreamConfig> ActiveLayoutStreams()
    {
        return ActiveLayoutStreams(StreamLookup());
    }

    private List<StreamConfig> ActiveLayoutStreams(IReadOnlyDictionary<string, StreamConfig> streamLookup)
    {
        var layout = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
            ?? _config.Layouts.FirstOrDefault();
        if (layout is null)
        {
            return [];
        }

        var streams = new List<StreamConfig>(layout.Tiles.Count);
        var seenStreamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxVisibleTiles = VisibleTileCountFor(layout);
        foreach (var tile in layout.Tiles)
        {
            if (string.IsNullOrWhiteSpace(tile.StreamId) ||
                tile.Index < 0 ||
                tile.Index >= maxVisibleTiles ||
                !streamLookup.TryGetValue(tile.StreamId, out var stream) ||
                !seenStreamIds.Add(stream.Id))
            {
                continue;
            }

            streams.Add(stream);
        }

        return streams;
    }

    private bool IsStreamInActiveLayout(string streamId)
    {
        var layout = ActiveLayout();
        var maxVisibleTiles = VisibleTileCountFor(layout);
        return layout?.Tiles.Any(tile =>
            tile.Index >= 0 &&
            tile.Index < maxVisibleTiles &&
            string.Equals(tile.StreamId, streamId, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private void StartStreamPlayback(StreamConfig stream)
    {
        if (ShouldUseFfmpegPlayback(stream))
        {
            StartFfmpegStreamPlayback(stream);
            return;
        }

        var (libVlc, player, audioMonitor) = PrepareStreamPlayback(stream);
        var media = new Media(libVlc, stream.InputUrl, FromType.FromLocation);
        AddLowLatencyVlcMediaOptions(media, ShouldDisableLibVlcPlaybackAudio(stream, audioMonitor));
        var streamId = stream.Id;
        var streamName = stream.Name;

        _ = System.Threading.Tasks.Task.Run(() => StartLibVlcPlayback(streamId, streamName, player, audioMonitor, media));

        if (stream.AnalysisEnabled)
        {
            QueueVideoMonitorStart(stream);
        }
    }

    private bool ShouldUseFfmpegPlayback(StreamConfig stream)
    {
        if (string.IsNullOrWhiteSpace(FindFfmpeg()))
        {
            return false;
        }

        return ShouldRouteThroughFfmpegPlayback(stream);
    }

    private static bool ShouldRouteThroughFfmpegPlayback(StreamConfig stream)
    {
        if (ForceFfmpegPlaybackEnabled())
        {
            return true;
        }

        if (RequiresFfmpegPlayback(stream))
        {
            return true;
        }

        return stream.AnalysisEnabled && !GpuRenderingEnabled();
    }

    private static bool RequiresFfmpegPlayback(StreamConfig stream)
    {
        return stream.InputProtocol.StartsWith("srt", StringComparison.OrdinalIgnoreCase) ||
            stream.InputUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
    }

    private void StartFfmpegStreamPlayback(StreamConfig stream)
    {
        _playbackRestartAfter.Remove(stream.Id);
        StopStreamPlayback(stream.Id, resetRestartBackoff: false);
        ClearPlaybackAnalysis(stream.Id);
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg was not found.");
        var profile = AlertProfileFor(stream);
        FfmpegPlaybackSession? session = null;
        var renderSize = CurrentNextFfmpegRenderSizeFor(stream.Id);
        session = new FfmpegPlaybackSession(
            ffmpeg,
            stream.InputUrl,
            stream.InputProtocol,
            Dispatcher,
            profile.BlackPixelThreshold,
            profile.FreezeDifferenceThreshold,
            profile.BlackThreshold,
            profile.BlackPixelRatioThreshold,
            () =>
            {
                if (session is not null)
                {
                    QueueImmediateFfmpegBlackFrameEvaluation(stream.Id, session);
                }
            },
            () =>
            {
                if (session is not null)
                {
                    OnFirstFfmpegFramePresented(stream, session);
                }
            },
            renderSize.Width,
            renderSize.Height,
            observeHardwareBackend: true);

        _ffmpegPlaybackSessions[stream.Id] = session;
        if (stream.AnalysisEnabled)
        {
            _videoMonitors[stream.Id] = session;
        }

        var initialSnapshot = EmptyPlaybackSnapshot("starting");
        _playbackSnapshots[stream.Id] = initialSnapshot;
        _lastPlaybackProgressAt[stream.Id] = DateTimeOffset.UtcNow;
        _lastPlaybackProgressMarker[stream.Id] = PlaybackProgressMarker(initialSnapshot);
        RefreshPlaybackSurface(stream);
        UpdateTileChrome(stream);
        session.Start();
        AppendDesktopLog($"FFmpeg playback started for {stream.Id}: {MaskStreamUrl(stream.InputUrl)} / hwaccel={FfmpegHardwareDecodeMode()} / output={session.OutputWidth}x{session.OutputHeight}");
    }

    private void OnFirstFfmpegFramePresented(StreamConfig stream, FfmpegPlaybackSession session)
    {
        if (!_ffmpegPlaybackSessions.TryGetValue(stream.Id, out var currentSession) ||
            !ReferenceEquals(currentSession, session))
        {
            return;
        }

        var snapshot = session.CurrentPlaybackSnapshot();
        _playbackSnapshots[stream.Id] = snapshot;
        TrackPlaybackProgress(stream.Id, snapshot);
        RefreshPlaybackSurface(stream);
        UpdateTileChrome(stream);
        UpdatePlaybackSummary();
    }

    private void QueueImmediateFfmpegBlackFrameEvaluation(string streamId, FfmpegPlaybackSession session)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_ffmpegPlaybackSessions.TryGetValue(streamId, out var currentSession) ||
                !ReferenceEquals(currentSession, session))
            {
                return;
            }

            lock (_immediateVideoAlertEvaluationSync)
            {
                _pendingImmediateBlackFrameDetections[streamId] = DateTimeOffset.UtcNow;
                _pendingImmediateVideoAlertEvaluations.Add(streamId);
            }

            RunImmediateVideoAlertEvaluation(streamId);
        }), DispatcherPriority.Normal);
    }

    private void StartLibVlcPlayback(string streamId, string streamName, VlcMediaPlayer player, AudioLevelMonitor? audioMonitor, Media media)
    {
        var started = false;
        Exception? error = null;
        try
        {
            started = player.Play(media);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            media.Dispose();
        }

        if (started)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_players.TryGetValue(streamId, out var currentPlayer) || !ReferenceEquals(currentPlayer, player))
                {
                    audioMonitor?.Dispose();
                    player.Dispose();
                    return;
                }

                var stream = _config.Streams.FirstOrDefault(candidate => candidate.Id == streamId);
                if (stream is not null)
                {
                    RefreshPlaybackSurface(stream);
                    UpdateTileChrome(stream);
                    UpdatePlaybackSummary();
                }
            }, DispatcherPriority.ContextIdle);
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_players.TryGetValue(streamId, out var currentPlayer) || !ReferenceEquals(currentPlayer, player))
            {
                audioMonitor?.Dispose();
                player.Dispose();
                return;
            }

            var stream = _config.Streams.FirstOrDefault(candidate => candidate.Id == streamId);
            if (stream is null)
            {
                _players.Remove(streamId);
                _audioMonitors.Remove(streamId);
                player.Dispose();
                audioMonitor?.Dispose();
                return;
            }

            CleanupFailedPlaybackStart(stream, player, audioMonitor);
            var reason = error is null ? "LibVLC returned false." : error.Message;
            AppendDesktopLog($"Start playback failed for {streamId}: {reason}");
            UpdateFooter($"Start playback failed for {streamName}: {reason}");
            UpdatePlaybackSummary();
        }, DispatcherPriority.Background);
    }

    private (LibVLC LibVlc, VlcMediaPlayer Player, AudioLevelMonitor? AudioMonitor) PrepareStreamPlayback(StreamConfig stream)
    {
        _playbackRestartAfter.Remove(stream.Id);
        StopStreamPlayback(stream.Id, resetRestartBackoff: false);
        ClearPlaybackAnalysis(stream.Id);
        var libVlc = EnsureLibVlc();
        var player = new VlcMediaPlayer(libVlc)
        {
            Mute = ShouldMuteLibVlcPlaybackAudio(stream)
        };
        AudioLevelMonitor? audioMonitor = null;
        if (ShouldAttachAudioMonitor(stream))
        {
            audioMonitor = new AudioLevelMonitor();
            audioMonitor.Attach(player);
        }

        _players[stream.Id] = player;
        if (audioMonitor is not null)
        {
            _audioMonitors[stream.Id] = audioMonitor;
        }

        var initialSnapshot = BuildSnapshot(player, "starting");
        _playbackSnapshots[stream.Id] = initialSnapshot;
        var now = DateTimeOffset.UtcNow;
        _lastPlaybackProgressAt[stream.Id] = now;
        _lastPlaybackTimeMs[stream.Id] = player.Time;
        _lastPlaybackProgressMarker[stream.Id] = PlaybackProgressMarker(initialSnapshot);
        RefreshPlaybackSurface(stream);
        UpdateTileChrome(stream);
        return (libVlc, player, audioMonitor);
    }

    private void CleanupFailedPlaybackStart(StreamConfig stream, VlcMediaPlayer player, AudioLevelMonitor? audioMonitor)
    {
        _players.Remove(stream.Id);
        _audioMonitors.Remove(stream.Id);
        _playbackSnapshots[stream.Id] = EmptyPlaybackSnapshot(
            _monitoringRequestedStreamIds.Contains(stream.Id) ? "retrying" : "stopped");
        MarkPlaybackRestartAfterFailure(stream.Id);
        RefreshPlaybackSurface(stream);
        UpdateTileChrome(stream);
        player.Dispose();
        audioMonitor?.Dispose();
    }

    private void StopStreamPlayback(string streamId, bool resetRestartBackoff = true)
    {
        _playbackRestartAfter.Remove(streamId);
        if (resetRestartBackoff)
        {
            _playbackRestartAttempts.Remove(streamId);
            _monitoringRequestedStreamIds.Remove(streamId);
        }
        _tileSurfaceRenderKeys.Remove(streamId);
        if (_ffmpegPlaybackSessions.Remove(streamId, out var ffmpegSession))
        {
            if (_videoMonitors.TryGetValue(streamId, out var monitor) && ReferenceEquals(monitor, ffmpegSession))
            {
                _videoMonitors.Remove(streamId);
            }

            ffmpegSession.Dispose();
            StopAudioMonitor(streamId);
            _playbackSnapshots[streamId] = EmptyPlaybackSnapshot("stopped");
            ClearPlaybackAnalysis(streamId);
            var ffmpegStream = _config.Streams.FirstOrDefault(candidate => candidate.Id == streamId);
            if (ffmpegStream is not null)
            {
                RefreshPlaybackSurface(ffmpegStream);
                UpdateTileChrome(ffmpegStream);
            }

            return;
        }

        if (!_players.Remove(streamId, out var player))
        {
            StopAudioMonitor(streamId);
            StopVideoMonitor(streamId);
            ClearPlaybackAnalysis(streamId);
            return;
        }

        player.Stop();
        player.Dispose();
        StopAudioMonitor(streamId);
        StopVideoMonitor(streamId);
        _playbackSnapshots[streamId] = EmptyPlaybackSnapshot("stopped");
        ClearPlaybackAnalysis(streamId);
        var stream = _config.Streams.FirstOrDefault(candidate => candidate.Id == streamId);
        if (stream is not null)
        {
            RefreshPlaybackSurface(stream);
            UpdateTileChrome(stream);
        }
    }

    private void StopAllPlayback()
    {
        CancelPendingPlaybackOperations();
        _monitoringRequestedStreamIds.Clear();
        _playbackRestartAfter.Clear();
        _playbackRestartAttempts.Clear();
        var streamIds = new HashSet<string>(_players.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var streamId in _ffmpegPlaybackSessions.Keys)
        {
            streamIds.Add(streamId);
        }

        foreach (var streamId in streamIds)
        {
            StopStreamPlayback(streamId, resetRestartBackoff: false);
        }
    }

    private void CancelPendingPlaybackOperations()
    {
        unchecked
        {
            _playbackOperationGeneration++;
        }
    }

    private bool IsPlaybackOperationCurrent(long expectedGeneration) =>
        _playbackOperationGeneration == expectedGeneration;

    private void StopPlaybackOutsideActiveLayout(IReadOnlySet<string>? activeStreamIds = null)
    {
        activeStreamIds ??= ActiveMonitoredStreamIdSet();
        var loadedStreamIds = _players.Keys
            .Concat(_ffmpegPlaybackSessions.Keys)
            .Concat(_monitoringRequestedStreamIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(streamId => !activeStreamIds.Contains(streamId))
            .ToList();
        foreach (var streamId in loadedStreamIds)
        {
            StopStreamPlayback(streamId);
        }
    }

    private HashSet<string> ActiveMonitoredStreamIdSet()
    {
        return ActiveLayoutStreams()
            .Where(stream => stream.Enabled)
            .Select(stream => stream.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool ResolveMonitoringAlertsOutsideActiveLayout(IReadOnlySet<string>? activeStreamIds = null)
    {
        activeStreamIds ??= ActiveMonitoredStreamIdSet();
        var changed = false;
        foreach (var alert in _config.AlertHistory.Where(alert =>
            !activeStreamIds.Contains(alert.StreamId) &&
            MonitoringAlertTypes.Contains(alert.Type) &&
            ActiveAlertStatuses.Contains(alert.Status)))
        {
            alert.Status = "resolved";
            alert.SnoozedUntil = null;
            alert.UpdatedAt = DateTimeOffset.UtcNow;
            alert.SyncedAt = null;
            alert.SourceClientId = _config.ClientId;
            changed = true;
        }

        foreach (var key in _issueSince.Keys.ToList())
        {
            var separator = key.IndexOf(':');
            var streamId = separator < 0 ? key : key[..separator];
            if (!activeStreamIds.Contains(streamId))
            {
                _issueSince.Remove(key);
            }
        }

        return changed;
    }

    private void ReconcileActiveLayoutMonitoring()
    {
        var monitoredStreamIds = ActiveMonitoredStreamIdSet();
        StopPlaybackOutsideActiveLayout(monitoredStreamIds);
        if (ResolveMonitoringAlertsOutsideActiveLayout(monitoredStreamIds))
        {
            LocalConfigStore.QueueSave(_config);
            RenderAlerts();
            UpdateAllTileChrome();
        }
    }

    private void StartAudioMonitor(StreamConfig stream)
    {
        if (_ffmpegPlaybackSessions.ContainsKey(stream.Id))
        {
            return;
        }

        if (_audioMonitors.ContainsKey(stream.Id) ||
            !CanPlayStream(stream) ||
            !ShouldAttachAudioMonitor(stream))
        {
            return;
        }

        try
        {
            if (!_players.TryGetValue(stream.Id, out var player))
            {
                return;
            }

            var monitor = new AudioLevelMonitor();
            monitor.Attach(player);
            _audioMonitors[stream.Id] = monitor;
        }
        catch
        {
            StopAudioMonitor(stream.Id);
        }
    }

    private void StopAudioMonitor(string streamId)
    {
        if (!_audioMonitors.Remove(streamId, out var monitor))
        {
            return;
        }

        monitor.Dispose();
    }

    private void StartVideoMonitor(StreamConfig stream)
    {
        if (TryUsePlaybackAsVideoMonitor(stream))
        {
            return;
        }

        if (QueueSharedAnalysisPlaybackRestart(stream))
        {
            return;
        }

        if (QueueSidecarVideoAnalysisMonitorStart(stream))
        {
            return;
        }

        if (_videoMonitors.ContainsKey(stream.Id) || !CanPlayStream(stream))
        {
            return;
        }

        AppendDesktopLog($"Video analysis monitor deferred for {stream.Id}: no shared playback analysis session is available, avoiding a second source connection.");
    }

    private void QueueVideoMonitorStart(StreamConfig stream)
    {
        if (TryUsePlaybackAsVideoMonitor(stream))
        {
            return;
        }

        if (QueueSharedAnalysisPlaybackRestart(stream))
        {
            return;
        }

        if (QueueSidecarVideoAnalysisMonitorStart(stream))
        {
            return;
        }

        if (_videoMonitors.ContainsKey(stream.Id) || !CanPlayStream(stream))
        {
            return;
        }

        AppendDesktopLog($"Video analysis monitor deferred for {stream.Id}: no shared playback analysis session is available, avoiding a second source connection.");
    }

    private void StopVideoMonitor(string streamId)
    {
        if (!_videoMonitors.Remove(streamId, out var monitor))
        {
            _videoMonitorShareKeys.Remove(streamId);
            return;
        }

        _videoMonitorShareKeys.Remove(streamId, out var shareKey);
        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var session) && ReferenceEquals(session, monitor))
        {
            return;
        }

        if (_videoMonitors.Values.Any(existing => ReferenceEquals(existing, monitor)))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(shareKey) &&
            _sharedVideoMonitors.TryGetValue(shareKey, out var sharedMonitor) &&
            ReferenceEquals(sharedMonitor, monitor))
        {
            _sharedVideoMonitors.Remove(shareKey);
        }

        monitor.Dispose();
    }

    private bool TryUsePlaybackAsVideoMonitor(StreamConfig stream)
    {
        if (_videoMonitors.ContainsKey(stream.Id))
        {
            return true;
        }

        if (!_ffmpegPlaybackSessions.TryGetValue(stream.Id, out var session))
        {
            return false;
        }

        _videoMonitors[stream.Id] = session;
        AppendDesktopLog($"Video analysis monitor reused playback session for {stream.Id}.");
        return true;
    }

    private bool QueueSidecarVideoAnalysisMonitorStart(StreamConfig stream)
    {
        if (!GpuRenderingEnabled() ||
            !stream.AnalysisEnabled ||
            _videoMonitors.ContainsKey(stream.Id) ||
            !CanPlayStream(stream) ||
            string.IsNullOrWhiteSpace(FindFfmpeg()) ||
            PlaybackOwnerFor(stream.Id) is not VlcMediaPlayer expectedPlayer)
        {
            return false;
        }

        if (!_pendingVideoMonitorStarts.Add(stream.Id))
        {
            return true;
        }

        var streamId = stream.Id;
        var streamName = stream.Name;
        var operationGeneration = _playbackOperationGeneration;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                while (_playbackStartInFlight && IsPlaybackOperationCurrent(operationGeneration))
                {
                    await System.Threading.Tasks.Task.Delay(PlaybackStartupPollInterval);
                }

                if (!IsPlaybackOperationCurrent(operationGeneration) ||
                    !await WaitForPlaybackOwnerReadyAsync(streamId, expectedPlayer, operationGeneration))
                {
                    AppendDesktopLog($"GPU sidecar analysis start deferred for {streamId}: primary playback was not ready.");
                    return;
                }

                await StartSidecarVideoAnalysisMonitorCoordinatedAsync(streamId, expectedPlayer, operationGeneration);
            }
            catch (Exception ex)
            {
                if (IsPlaybackOperationCurrent(operationGeneration))
                {
                    AppendDesktopLog($"GPU sidecar analysis start failed for {streamId}: {MaskStreamUrl(ex.Message)}");
                    UpdateFooter($"Video analysis start failed for {streamName}: {ex.Message}");
                }
            }
            finally
            {
                _pendingVideoMonitorStarts.Remove(streamId);
                RequeueSidecarVideoAnalysisForReplacementOwner(streamId, expectedPlayer, operationGeneration);
            }
        }), DispatcherPriority.Background);
        return true;
    }

    private async System.Threading.Tasks.Task<bool> WaitForPlaybackOwnerReadyAsync(
        string streamId,
        VlcMediaPlayer expectedPlayer,
        long operationGeneration)
    {
        while (IsPlaybackOperationCurrent(operationGeneration) &&
            ReferenceEquals(PlaybackOwnerFor(streamId), expectedPlayer))
        {
            var state = expectedPlayer.State.ToString();
            if (string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IsRecoverablePlaybackState(state))
            {
                return false;
            }

            await System.Threading.Tasks.Task.Delay(PlaybackStartupPollInterval);
        }

        return false;
    }

    private void RequeueSidecarVideoAnalysisForReplacementOwner(
        string streamId,
        VlcMediaPlayer expectedPlayer,
        long operationGeneration)
    {
        if (!IsPlaybackOperationCurrent(operationGeneration) ||
            _videoMonitors.ContainsKey(streamId) ||
            PlaybackOwnerFor(streamId) is not VlcMediaPlayer currentPlayer ||
            ReferenceEquals(currentPlayer, expectedPlayer))
        {
            return;
        }

        var stream = CurrentActivePlayableStream(streamId);
        if (stream is not null && stream.AnalysisEnabled)
        {
            QueueSidecarVideoAnalysisMonitorStart(stream);
        }
    }

    private async System.Threading.Tasks.Task<bool> StartSidecarVideoAnalysisMonitorCoordinatedAsync(
        string streamId,
        VlcMediaPlayer expectedPlayer,
        long operationGeneration)
    {
        if (!IsPlaybackOperationCurrent(operationGeneration))
        {
            return false;
        }

        await _playbackStartupSlots.WaitAsync();
        var startupSlotHeld = true;
        IVideoAnalysisMonitor? startedMonitor = null;
        try
        {
            await _playbackLaunchGate.WaitAsync();
            try
            {
                if (!IsPlaybackOperationCurrent(operationGeneration) ||
                    CurrentSidecarAnalysisStream(streamId, expectedPlayer) is null)
                {
                    return false;
                }

                var launchDelay = _nextPlaybackLaunchAt - DateTimeOffset.UtcNow;
                if (launchDelay > TimeSpan.Zero)
                {
                    await System.Threading.Tasks.Task.Delay(launchDelay);
                }

                var stream = CurrentSidecarAnalysisStream(streamId, expectedPlayer);
                if (!IsPlaybackOperationCurrent(operationGeneration) || stream is null)
                {
                    return false;
                }

                try
                {
                    if (!TryStartSidecarVideoAnalysisMonitor(stream) ||
                        !_videoMonitors.TryGetValue(streamId, out startedMonitor))
                    {
                        return false;
                    }
                }
                finally
                {
                    _nextPlaybackLaunchAt = DateTimeOffset.UtcNow.Add(PlaybackBatchStartSpacing);
                }
            }
            finally
            {
                _playbackLaunchGate.Release();
            }

            // Sidecar readiness is an external media concern. Once its process is
            // registered, let the remaining playback and analysis owners launch.
            _playbackStartupSlots.Release();
            startupSlotHeld = false;

            var deadline = DateTimeOffset.UtcNow.Add(PlaybackStartupSlotTimeout);
            var terminal = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!IsPlaybackOperationCurrent(operationGeneration))
                {
                    break;
                }

                if (!_videoMonitors.TryGetValue(streamId, out var currentMonitor) ||
                    !ReferenceEquals(currentMonitor, startedMonitor))
                {
                    return true;
                }

                if (IsRecoverablePlaybackState(startedMonitor.State))
                {
                    AppendDesktopLog($"GPU sidecar analysis startup for {streamId} entered terminal state '{startedMonitor.State}'.");
                    terminal = true;
                    break;
                }

                if (string.Equals(startedMonitor.State, "playing", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                await System.Threading.Tasks.Task.Delay(PlaybackStartupPollInterval);
            }

            if (terminal &&
                _videoMonitors.TryGetValue(streamId, out var timedOutMonitor) &&
                ReferenceEquals(timedOutMonitor, startedMonitor))
            {
                StopVideoMonitor(streamId);
                AppendDesktopLog($"GPU sidecar analysis startup failed for {streamId}; stopped the terminal monitor.");
                return false;
            }

            if (_videoMonitors.TryGetValue(streamId, out var pendingMonitor) &&
                ReferenceEquals(pendingMonitor, startedMonitor) &&
                IsPlaybackOperationCurrent(operationGeneration))
            {
                var stream = CurrentActivePlayableStream(streamId);
                if (stream is not null && IsPassiveReceiverInput(stream.InputUrl, stream.InputProtocol))
                {
                    AppendDesktopLog(
                        $"GPU sidecar analysis receiver for {streamId} remains armed for passive input.");
                    return true;
                }

                StopVideoMonitor(streamId);
                AppendDesktopLog(
                    $"GPU sidecar analysis startup timed out for {streamId}; stopped the active connection attempt so periodic recovery can retry it.");
            }

            return false;
        }
        finally
        {
            if (startupSlotHeld)
            {
                _playbackStartupSlots.Release();
            }
        }
    }

    private StreamConfig? CurrentSidecarAnalysisStream(string streamId, VlcMediaPlayer expectedPlayer)
    {
        var stream = CurrentActivePlayableStream(streamId);
        return stream is not null &&
            GpuRenderingEnabled() &&
            stream.AnalysisEnabled &&
            !_videoMonitors.ContainsKey(streamId) &&
            ReferenceEquals(PlaybackOwnerFor(streamId), expectedPlayer) &&
            string.Equals(expectedPlayer.State.ToString(), "playing", StringComparison.OrdinalIgnoreCase) &&
            !ShouldUseFfmpegPlayback(stream)
                ? stream
                : null;
    }

    private bool TryStartSidecarVideoAnalysisMonitor(StreamConfig stream)
    {
        if (!GpuRenderingEnabled() ||
            !stream.AnalysisEnabled ||
            _videoMonitors.ContainsKey(stream.Id) ||
            !CanPlayStream(stream))
        {
            return false;
        }

        var ffmpeg = FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return false;
        }

        var profile = AlertProfileFor(stream);
        var shareKey = SidecarAnalysisShareKey(stream, profile);
        if (_sharedVideoMonitors.TryGetValue(shareKey, out var sharedMonitor))
        {
            _videoMonitors[stream.Id] = sharedMonitor;
            _videoMonitorShareKeys[stream.Id] = shareKey;
            AppendDesktopLog($"GPU rendering sidecar analysis monitor reused for {stream.Id}: shared input {MaskStreamUrl(stream.InputUrl)}.");
            return true;
        }

        var monitor = new FfmpegVideoAnalysisMonitor(
            ffmpeg,
            stream.InputUrl,
            stream.InputProtocol,
            profile.BlackPixelThreshold,
            profile.FreezeDifferenceThreshold,
            profile.BlackThreshold,
            profile.BlackPixelRatioThreshold,
            null);
        monitor.AddBlackFrameDetectedCallback(() => QueueImmediateVideoAlertEvaluationsForMonitor(monitor));
        try
        {
            monitor.Start();
            _videoMonitors[stream.Id] = monitor;
            _videoMonitorShareKeys[stream.Id] = shareKey;
            _sharedVideoMonitors[shareKey] = monitor;
            AppendDesktopLog($"GPU rendering sidecar analysis monitor started for {stream.Id}: playback remains LibVLC embedded GPU output.");
            return true;
        }
        catch (Exception ex)
        {
            monitor.Dispose();
            AppendDesktopLog($"GPU rendering sidecar analysis monitor failed for {stream.Id}: {MaskStreamUrl(ex.Message)}");
            return false;
        }
    }

    private static bool ShouldAttachAudioMonitor(StreamConfig stream)
    {
        return stream.AudioMonitorEnabled;
    }

    private bool ShouldDisableLibVlcPlaybackAudio(StreamConfig stream, AudioLevelMonitor? audioMonitor)
    {
        return audioMonitor is null;
    }

    private static bool ShouldMuteLibVlcPlaybackAudio(StreamConfig stream)
    {
        return true;
    }

    private void QueueImmediateVideoAlertEvaluationsForMonitor(IVideoAnalysisMonitor monitor)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var streamIds = _videoMonitors
                .Where(entry => ReferenceEquals(entry.Value, monitor))
                .Select(entry => entry.Key)
                .ToList();
            foreach (var streamId in streamIds)
            {
                QueueImmediateVideoAlertEvaluation(streamId, blackFrameDetected: true);
            }
        }), DispatcherPriority.Normal);
    }

    private static string SidecarAnalysisShareKey(StreamConfig stream, AlertProfileConfig profile)
    {
        return string.Join(
            "\n",
            "ffmpeg-sidecar-v1",
            stream.InputProtocol.Trim().ToLowerInvariant(),
            stream.InputUrl.Trim(),
            Math.Clamp(profile.BlackPixelThreshold, 0.0, 1.0).ToString("R", CultureInfo.InvariantCulture),
            Math.Clamp(profile.FreezeDifferenceThreshold, 0.0, 1.0).ToString("R", CultureInfo.InvariantCulture),
            Math.Clamp(profile.BlackThreshold, 0.0, 1.0).ToString("R", CultureInfo.InvariantCulture),
            Math.Clamp(profile.BlackPixelRatioThreshold, 0.0, 1.0).ToString("R", CultureInfo.InvariantCulture));
    }

    private bool QueueSharedAnalysisPlaybackRestart(StreamConfig stream)
    {
        if (!stream.AnalysisEnabled ||
            !_players.ContainsKey(stream.Id) ||
            !ShouldUseFfmpegPlayback(stream))
        {
            return false;
        }

        var expectedOwner = PlaybackOwnerFor(stream.Id);
        if (expectedOwner is null)
        {
            return false;
        }

        var operationGeneration = _playbackOperationGeneration;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                while (_playbackStartInFlight && IsPlaybackOperationCurrent(operationGeneration))
                {
                    await System.Threading.Tasks.Task.Delay(PlaybackStartupPollInterval);
                }

                if (!IsPlaybackOperationCurrent(operationGeneration))
                {
                    return;
                }

                var restarted = await StartStreamPlaybackCoordinatedAsync(
                    stream.Id,
                    restartUnhealthy: true,
                    expectedOwner,
                    forceRestart: true,
                    expectedOperationGeneration: operationGeneration,
                    allowDuringBatch: false);
                if (restarted)
                {
                    AppendDesktopLog($"Restarted {stream.Id} with shared FFmpeg playback/analysis to avoid a second source connection.");
                }
            }
            catch (Exception ex)
            {
                if (IsPlaybackOperationCurrent(operationGeneration))
                {
                    AppendDesktopLog($"Shared analysis playback restart failed for {stream.Id}: {ex}");
                    UpdateFooter($"Shared analysis restart failed for {stream.Name}: {ex.Message}");
                }
            }
        }), DispatcherPriority.Background);
        return true;
    }

    private void ApplyMuteState()
    {
        foreach (var (streamId, player) in _players)
        {
            var stream = _config.Streams.FirstOrDefault(stream => stream.Id == streamId);
            player.Mute = stream is null || ShouldMuteLibVlcPlaybackAudio(stream);
            if (stream?.AudioMonitorEnabled == true)
            {
                StartAudioMonitor(stream);
            }
            else
            {
                StopAudioMonitor(streamId);
            }

            if (stream?.AnalysisEnabled == true)
            {
                StartVideoMonitor(stream);
            }
            else
            {
                StopVideoMonitor(streamId);
            }
        }
        RefreshPlaybackMetadata();
    }

    private void UpdatePlaybackSummary(IReadOnlyList<StreamConfig>? streams = null)
    {
        var active = 0;
        foreach (var player in _players.Values)
        {
            if (IsActivePlaybackState(player.State))
            {
                active++;
            }
        }

        foreach (var session in _ffmpegPlaybackSessions.Values)
        {
            if (string.Equals(session.State, "playing", StringComparison.OrdinalIgnoreCase))
            {
                active++;
            }
        }

        var errors = 0;
        foreach (var snapshot in _playbackSnapshots.Values)
        {
            if (string.Equals(snapshot.State, "error", StringComparison.OrdinalIgnoreCase))
            {
                errors++;
            }
        }

        var layoutStreams = streams ?? ActiveLayoutStreams();
        var maxVideoAgeMs = -1.0;
        foreach (var stream in layoutStreams)
        {
            var ageMs = TryGetVideoFreshnessMs(stream.Id);
            if (ageMs > maxVideoAgeMs)
            {
                maxVideoAgeMs = ageMs;
            }
        }

        var videoFreshness = maxVideoAgeMs >= 0
            ? $" / video age max {maxVideoAgeMs:0}ms"
            : "";
        var monitored = _players.Keys
            .Concat(_ffmpegPlaybackSessions.Keys)
            .Concat(_monitoringRequestedStreamIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        SetTextIfChanged(PlaybackSummaryText, $"{active}/{monitored} active · {errors} err");
        PlaybackSummaryText.ToolTip = $"{active} active / {monitored} monitoring / {layoutStreams.Count} layout streams / {errors} errors{videoFreshness}";
        UpdateNextRenderBackendStatus();
    }

    private double TryGetVideoFreshnessMs(string streamId)
    {
        return PlaybackVideoFreshnessFor(streamId).AgeMs;
    }

    private PlaybackVideoFreshness PlaybackVideoFreshnessFor(string streamId)
    {
        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var ffmpegSession) &&
            ffmpegSession.LastFramePresentedAt > DateTimeOffset.MinValue)
        {
            var ageMs = Math.Max(0, (DateTimeOffset.UtcNow - ffmpegSession.LastFramePresentedAt).TotalMilliseconds);
            return new PlaybackVideoFreshness(ageMs, ffmpegSession.LastRenderDelayMs);
        }

        return _lastVideoPresentedAt.TryGetValue(streamId, out var lastPresentedAt)
            ? new PlaybackVideoFreshness(Math.Max(0, (DateTimeOffset.UtcNow - lastPresentedAt).TotalMilliseconds), -1)
            : new PlaybackVideoFreshness(-1, -1);
    }

    private List<AlertRecord> ActiveAlertsFor(string streamId)
    {
        var alerts = new List<AlertRecord>();
        foreach (var alert in _config.AlertHistory)
        {
            if (string.Equals(alert.StreamId, streamId, StringComparison.OrdinalIgnoreCase) &&
                ActiveAlertStatuses.Contains(alert.Status))
            {
                alerts.Add(alert);
            }
        }

        SortActiveAlerts(alerts);
        return alerts;
    }

    private Dictionary<string, IReadOnlyList<AlertRecord>> ActiveAlertsByStream(IReadOnlyList<StreamConfig> streams)
    {
        var byStream = new Dictionary<string, IReadOnlyList<AlertRecord>>(streams.Count, StringComparer.OrdinalIgnoreCase);
        var alertStreams = new Dictionary<string, List<AlertRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in streams)
        {
            if (!string.IsNullOrWhiteSpace(stream.Id) && !byStream.ContainsKey(stream.Id))
            {
                byStream[stream.Id] = Array.Empty<AlertRecord>();
            }
        }

        if (byStream.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<AlertRecord>>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var alert in _config.AlertHistory)
        {
            if (!byStream.ContainsKey(alert.StreamId) ||
                !ActiveAlertStatuses.Contains(alert.Status))
            {
                continue;
            }

            if (!alertStreams.TryGetValue(alert.StreamId, out var alerts))
            {
                alerts = [];
                alertStreams[alert.StreamId] = alerts;
            }

            alerts.Add(alert);
        }

        foreach (var (streamId, alerts) in alertStreams)
        {
            if (alerts.Count > 1)
            {
                SortActiveAlerts(alerts);
            }

            byStream[streamId] = alerts;
        }

        return byStream;
    }

    private IReadOnlyList<AlertRecord> ActiveAlertsFor(
        string streamId,
        IReadOnlyDictionary<string, IReadOnlyList<AlertRecord>>? activeAlertsByStream)
    {
        if (activeAlertsByStream is not null &&
            activeAlertsByStream.TryGetValue(streamId, out var alerts))
        {
            return alerts;
        }

        return ActiveAlertsFor(streamId);
    }

    private AlertRecord? ActiveAlertFor(string streamId)
    {
        return ActiveAlertsFor(streamId).FirstOrDefault();
    }

    private string? PendingIssueFor(string streamId)
    {
        return PendingIssueFor(streamId, ActiveAlertsFor(streamId));
    }

    private string? PendingIssueFor(string streamId, IReadOnlyList<AlertRecord> activeAlerts)
    {
        foreach (var type in PendingIssueTypes)
        {
            if (ContainsAlertType(activeAlerts, type))
            {
                continue;
            }

            if (_issueSince.ContainsKey(IssueKey(streamId, type)))
            {
                return type;
            }
        }

        return null;
    }

    private static void SortActiveAlerts(List<AlertRecord> alerts)
    {
        alerts.Sort(CompareActiveAlerts);
    }

    private static int CompareActiveAlerts(AlertRecord left, AlertRecord right)
    {
        var severity = SeverityRank(right.Severity).CompareTo(SeverityRank(left.Severity));
        return severity != 0 ? severity : right.StartedAt.CompareTo(left.StartedAt);
    }

    private static bool ContainsAlertType(IReadOnlyList<AlertRecord> alerts, string type)
    {
        for (var index = 0; index < alerts.Count; index++)
        {
            var alert = alerts[index];
            if (string.Equals(alert.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureStandardLayouts()
    {
        EnsureLayout("layout-2x2", "Default 2x2", "2x2", 2);
        EnsureLayout("layout-3x3", "Default 3x3", "3x3", 3);
        EnsureLayout("layout-4x4", "Default 4x4", "4x4", 4);
        if (_config.Layouts.All(layout => layout.Id != _config.ActiveLayoutId))
        {
            _config.ActiveLayoutId = _config.Layouts.FirstOrDefault()?.Id ?? "layout-2x2";
        }
    }

    private void EnsureLayout(string id, string name, string kind, int size)
    {
        var layout = _config.Layouts.FirstOrDefault(layout => layout.Id == id);
        if (layout is null)
        {
            layout = new LayoutConfig { Id = id, Name = name, Kind = kind };
            _config.Layouts.Add(layout);
        }

        layout.Name = name;
        layout.Kind = kind;
        for (var index = 0; index < size * size; index++)
        {
            if (layout.Tiles.Any(tile => tile.Index == index))
            {
                continue;
            }

            layout.Tiles.Add(new LayoutTileConfig
            {
                Index = index,
                Row = index / size,
                Column = index % size,
                StreamId = _config.Streams.ElementAtOrDefault(index)?.Id ?? ""
            });
        }

        NormalizeLayoutTiles(layout, size);
    }

    private void NormalizeLayoutTiles(LayoutConfig layout, int size)
    {
        var maxTiles = size * size;
        foreach (var duplicate in layout.Tiles
            .GroupBy(tile => tile.Index)
            .SelectMany(group => group.Skip(1))
            .Concat(layout.Tiles.Where(tile => tile.Index < 0 || tile.Index >= maxTiles))
            .Distinct()
            .ToList())
        {
            layout.Tiles.Remove(duplicate);
        }

        var orderedTiles = layout.Tiles.OrderBy(tile => tile.Index).ToList();
        if (!layout.Tiles.SequenceEqual(orderedTiles))
        {
            layout.Tiles.Clear();
            foreach (var tile in orderedTiles)
            {
                layout.Tiles.Add(tile);
            }
        }

        var validStreamIds = _config.Streams.Select(stream => stream.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedStreamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in layout.Tiles)
        {
            tile.Row = tile.Index / size;
            tile.Column = tile.Index % size;
            tile.RowSpan = Math.Max(1, tile.RowSpan);
            tile.ColSpan = Math.Max(1, tile.ColSpan);
            if (string.IsNullOrWhiteSpace(tile.StreamId))
            {
                tile.StreamId = "";
                continue;
            }

            if (!validStreamIds.Contains(tile.StreamId) || !usedStreamIds.Add(tile.StreamId))
            {
                tile.StreamId = "";
            }
        }
    }

    private void AssignStreamToFirstEmptyTile(string streamId)
    {
        var layout = ActiveLayout();
        var emptyTile = layout?.Tiles.OrderBy(tile => tile.Index).FirstOrDefault(tile => string.IsNullOrWhiteSpace(tile.StreamId));
        if (emptyTile is not null)
        {
            emptyTile.StreamId = streamId;
        }
    }

    private void AssignStreamToTile(string streamId, int tileIndex)
    {
        var layout = ActiveLayout();
        if (layout is null)
        {
            return;
        }

        var sourceTile = string.IsNullOrWhiteSpace(streamId)
            ? null
            : layout.Tiles.FirstOrDefault(tile => string.Equals(tile.StreamId, streamId, StringComparison.OrdinalIgnoreCase));

        if (tileIndex < 0)
        {
            if (sourceTile is not null)
            {
                sourceTile.StreamId = "";
            }

            return;
        }

        var targetTile = layout.Tiles.FirstOrDefault(tile => tile.Index == tileIndex);
        if (targetTile is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(streamId))
        {
            targetTile.StreamId = "";
            return;
        }

        if (sourceTile is not null && !ReferenceEquals(sourceTile, targetTile))
        {
            sourceTile.StreamId = targetTile.StreamId;
        }

        targetTile.StreamId = streamId;
    }

    private HashSet<int> AffectedTileIndexesForAssignment(int tileIndex, string streamId)
    {
        var affected = new HashSet<int> { tileIndex };
        var layout = ActiveLayout();
        if (layout is null || string.IsNullOrWhiteSpace(streamId))
        {
            return affected;
        }

        var sourceTile = layout.Tiles.FirstOrDefault(tile => string.Equals(tile.StreamId, streamId, StringComparison.OrdinalIgnoreCase));
        if (sourceTile is not null)
        {
            affected.Add(sourceTile.Index);
        }

        return affected;
    }

    private void AssignTileToStream(int tileIndex, string streamId)
    {
        var affectedTileIndexes = AffectedTileIndexesForAssignment(tileIndex, streamId);
        AssignStreamToTile(streamId, tileIndex);
        LocalConfigStore.QueueSave(_config);
        RenderTilesAt(affectedTileIndexes);
        ReconcileActiveLayoutMonitoring();
        UpdatePlaybackSummary();
        UpdateFooter(string.IsNullOrWhiteSpace(streamId)
            ? $"Tile {tileIndex + 1} unassigned."
            : $"Tile {tileIndex + 1} assigned.");
    }

    private bool IsStreamAssignedToTile(int tileIndex, string streamId)
    {
        var layout = ActiveLayout();
        return layout?.Tiles.Any(tile =>
            tile.Index == tileIndex &&
            string.Equals(tile.StreamId, streamId, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private LayoutConfig? ActiveLayout()
    {
        return _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
            ?? _config.Layouts.FirstOrDefault();
    }

    private static int GridSizeFor(string? kind)
    {
        return kind?.ToLowerInvariant() switch
        {
            "3x3" => 3,
            "4x4" => 4,
            _ => 2
        };
    }

    private static int VisibleTileCountFor(LayoutConfig? layout)
    {
        var size = GridSizeFor(layout?.Kind);
        return size * size;
    }

    private string StatusColorFor(StreamConfig? stream, AlertRecord? alert, bool isLoaded, PlaybackSnapshot? snapshot)
    {
        return StatusColorFor(stream, alert, isLoaded, snapshot, stream is null ? default : PlaybackVideoFreshnessFor(stream.Id));
    }

    private string StatusColorFor(
        StreamConfig? stream,
        AlertRecord? alert,
        bool isLoaded,
        PlaybackSnapshot? snapshot,
        PlaybackVideoFreshness videoFreshness)
    {
        if (stream is null || !stream.Enabled)
        {
            return "#6b7280";
        }

        if (alert is null && !isLoaded)
        {
            return stream is not null && _monitoringRequestedStreamIds.Contains(stream.Id)
                ? "#f59e0b"
                : "#6b7280";
        }

        if (alert is null && snapshot is not null && !string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.State.ToLowerInvariant() switch
            {
                "opening" or "buffering" or "starting" or "rendering" => "#f59e0b",
                "stopped" or "ended" or "error" => "#ef4444",
                _ => "#6b7280"
            };
        }

        if (alert is null &&
            snapshot is not null &&
            string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            snapshot.DisplayedPictures <= 0 &&
            (stream is null || !HasPresentedPlaybackVideo(stream.Id, snapshot)))
        {
            return "#f59e0b";
        }

        if (alert is null &&
            stream is not null &&
            snapshot is not null &&
            string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            HasPresentedPlaybackVideo(stream.Id, snapshot) &&
            !IsPlaybackVideoFreshForStatus(videoFreshness))
        {
            return "#f59e0b";
        }

        return alert?.Severity.ToLowerInvariant() switch
        {
            "critical" or "error" => "#ef4444",
            "warning" => "#f59e0b",
            _ => "#22c55e"
        };
    }

    private static string StatusColorForPendingIssue(string type)
    {
        return type switch
        {
            "video.no_signal" or "video.black" => "#ef4444",
            _ => "#f59e0b"
        };
    }

    private string StatusTextFor(StreamConfig? stream, AlertRecord? alert, bool isLoaded, PlaybackSnapshot? snapshot)
    {
        return StatusTextFor(stream, alert, isLoaded, snapshot, stream is null ? default : PlaybackVideoFreshnessFor(stream.Id));
    }

    private string StatusTextFor(
        StreamConfig? stream,
        AlertRecord? alert,
        bool isLoaded,
        PlaybackSnapshot? snapshot,
        PlaybackVideoFreshness videoFreshness)
    {
        if (stream is null)
        {
            return "offline / unassigned";
        }

        if (!stream.Enabled)
        {
            return "offline / disabled";
        }

        if (alert is not null)
        {
            return $"{alert.Status} / {alert.Type}";
        }

        if (!isLoaded)
        {
            return _monitoringRequestedStreamIds.Contains(stream.Id)
                ? "monitoring / retrying"
                : "standby / playback stopped";
        }

        if (snapshot is null)
        {
            return "starting / metadata pending";
        }

        if (!string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase))
        {
            return $"monitoring / {snapshot.State}";
        }

        if (!HasPresentedPlaybackVideo(stream.Id, snapshot))
        {
            return "monitoring / rendering";
        }

        var freshnessText = VideoFreshnessText(videoFreshness);
        return IsPlaybackVideoFreshForStatus(videoFreshness)
            ? $"OK / {freshnessText}"
            : $"monitoring / {freshnessText}";
    }

    private bool IsPlaybackVideoFreshForStatus(string streamId)
    {
        return IsPlaybackVideoFreshForStatus(PlaybackVideoFreshnessFor(streamId));
    }

    private static bool IsPlaybackVideoFreshForStatus(PlaybackVideoFreshness videoFreshness)
    {
        return videoFreshness.AgeMs < 0 || videoFreshness.AgeMs <= PlaybackVideoFreshnessWarning.TotalMilliseconds;
    }

    private bool HasPresentedPlaybackVideo(string streamId, PlaybackSnapshot snapshot)
    {
        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var ffmpegSession))
        {
            return ffmpegSession.HasPresentedFrame;
        }

        return HasPresentedVlcVideo(streamId, snapshot);
    }

    private bool HasPresentedVlcVideo(string streamId, PlaybackSnapshot snapshot)
    {
        return string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            (snapshot.DisplayedPictures > 0 || _lastVideoPresentedAt.ContainsKey(streamId));
    }

    private bool ShouldClearPlaybackSurface(StreamConfig stream, PlaybackSnapshot snapshot)
    {
        var profile = AlertProfileFor(stream);
        return IsDecodeError(snapshot) ||
            IsNoSignal(snapshot) ||
            IsOpeningTimedOut(stream.Id, snapshot, profile) ||
            IsPlaybackVideoStaleForSurface(stream.Id, profile);
    }

    private bool IsPlaybackVideoFreshForSurface(StreamConfig stream, PlaybackSnapshot snapshot)
    {
        return string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            !IsPlaybackVideoStaleForSurface(stream.Id, AlertProfileFor(stream));
    }

    private bool IsPlaybackVideoStaleForSurface(string streamId, AlertProfileConfig profile)
    {
        if (!_lastVideoPresentedAt.TryGetValue(streamId, out var lastPresentedAt))
        {
            return false;
        }

        return (DateTimeOffset.UtcNow - lastPresentedAt).TotalSeconds >= Math.Max(1, profile.InputTimeoutSec);
    }

    private string VideoFreshnessText(string streamId)
    {
        return VideoFreshnessText(PlaybackVideoFreshnessFor(streamId));
    }

    private static string VideoFreshnessText(PlaybackVideoFreshness videoFreshness)
    {
        if (videoFreshness.AgeMs >= 0)
        {
            var renderLag = videoFreshness.RenderDelayMs >= 0
                ? $" / render {videoFreshness.RenderDelayMs:0}ms"
                : "";
            return $"video {videoFreshness.AgeMs:0}ms{renderLag}";
        }

        return "video pending";
    }

    private static int SeverityRank(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "critical" => 4,
            "error" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0
        };
    }

    private static bool LooksMasked(string value)
    {
        return value.Contains("****", StringComparison.Ordinal) ||
            value.Contains("<REDACTED>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanPlayStream(StreamConfig stream)
    {
        return stream.Enabled &&
            !string.IsNullOrWhiteSpace(stream.InputUrl) &&
            !LooksMasked(stream.InputUrl) &&
            !stream.InputUrl.Contains("<PASSWORD>", StringComparison.OrdinalIgnoreCase) &&
            !stream.InputUrl.Contains("<PASSPHRASE>", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(stream.InputUrl, UriKind.Absolute, out _);
    }

    private string PlaybackStatusText(StreamConfig stream)
    {
        if (_playbackSnapshots.TryGetValue(stream.Id, out var snapshot))
        {
            var fps = snapshot.Fps > 0 ? snapshot.Fps.ToString("0.##") : "unavailable";
            var time = snapshot.TimeMs >= 0 ? TimeSpan.FromMilliseconds(snapshot.TimeMs).ToString(@"hh\:mm\:ss") : "unknown";
            var length = snapshot.LengthMs > 0 ? TimeSpan.FromMilliseconds(snapshot.LengthMs).ToString(@"hh\:mm\:ss") : "live";
            var audio = snapshot.Muted ? "muted" : "on";
            var audioTracks = snapshot.AudioTrackCount >= 0 ? snapshot.AudioTrackCount.ToString() : "unavailable";
            var audioLevel = "unavailable";
            if (_audioMonitors.TryGetValue(stream.Id, out var monitor))
            {
                audioLevel = monitor.TryGetSnapshot(out var audioSnapshot)
                    ? $"{audioSnapshot.LevelDb:0.0} dBFS"
                    : $"waiting ({monitor.State})";
            }

            var luma = "unavailable";
            if (_videoMonitors.TryGetValue(stream.Id, out var videoMonitor))
            {
                luma = videoMonitor.TryGetSnapshot(out var videoSnapshot)
                    ? $"{videoSnapshot.Luma:0.000} / dark {videoSnapshot.DarkRatio:P0} / diff {videoSnapshot.Difference:0.000}"
                    : $"waiting ({videoMonitor.State})";
            }

            var videoOutput = _ffmpegPlaybackSessions.TryGetValue(stream.Id, out var ffmpegSession)
                ? $" / video out: presented {ffmpegSession.BitmapFramesPresented}, coalesced {ffmpegSession.BitmapFramesCoalesced}, dropped {ffmpegSession.BitmapFramesDropped}"
                : "";

            return $"playback: {snapshot.State} / fps: {fps} / time: {time}/{length} / audio: {audio} / tracks: {audioTracks} / level: {audioLevel} / luma: {luma}{videoOutput}";
        }

        return IsPlaybackLoaded(stream.Id)
            ? "playback: starting"
            : _monitoringRequestedStreamIds.Contains(stream.Id)
                ? "playback: retrying"
                : "playback: stopped";
    }

    private void RefreshPlaybackMetadata()
    {
        _blinkPhase = !_blinkPhase;
        var now = DateTimeOffset.UtcNow;
        var runSlowRefresh = now - _lastSlowPlaybackRefreshAt >= TimeSpan.FromSeconds(1);
        if (runSlowRefresh)
        {
            _lastSlowPlaybackRefreshAt = now;
        }

        var streamLookup = StreamLookup();
        var activeLayoutStreams = ActiveLayoutStreams(streamLookup);
        var activeLayoutStreamLookup = new Dictionary<string, StreamConfig>(activeLayoutStreams.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var stream in activeLayoutStreams)
        {
            if (!string.IsNullOrWhiteSpace(stream.Id) && !activeLayoutStreamLookup.ContainsKey(stream.Id))
            {
                activeLayoutStreamLookup[stream.Id] = stream;
            }
        }

        foreach (var (streamId, player) in _players)
        {
            try
            {
                var snapshot = BuildSnapshot(player, null);
                _playbackSnapshots[streamId] = snapshot;
                TrackPlaybackProgress(streamId, snapshot);
                if (runSlowRefresh)
                {
                    SchedulePlaybackRestartIfNeeded(streamId, snapshot);
                }

                if (activeLayoutStreamLookup.TryGetValue(streamId, out var stream))
                {
                    RefreshPlaybackSurface(stream);
                }
            }
            catch
            {
                _playbackSnapshots[streamId] = EmptyPlaybackSnapshot("error");
            }
        }

        foreach (var (streamId, session) in _ffmpegPlaybackSessions)
        {
            try
            {
                var snapshot = session.CurrentPlaybackSnapshot();
                _playbackSnapshots[streamId] = snapshot;
                TrackPlaybackProgress(streamId, snapshot);
                if (runSlowRefresh)
                {
                    SchedulePlaybackRestartIfNeeded(streamId, snapshot);
                }

                if (activeLayoutStreamLookup.TryGetValue(streamId, out var stream))
                {
                    RefreshPlaybackSurface(stream);
                }
            }
            catch
            {
                _playbackSnapshots[streamId] = EmptyPlaybackSnapshot("error");
            }
        }

        if (runSlowRefresh)
        {
            ScheduleOwnerlessPlaybackRestarts();
        }

        var alertStateChanged = false;
        if (runSlowRefresh)
        {
            alertStateChanged = EvaluatePlaybackAlerts(activeLayoutStreams);
            alertStateChanged |= RefreshSnoozedAlerts();
        }

        UpdatePlaybackSummary(activeLayoutStreams);
        UpdateAllTileChrome(activeLayoutStreams);
        if (alertStateChanged)
        {
            LocalConfigStore.QueueSave(_config);
            RenderAlerts();
        }

        if (runSlowRefresh)
        {
            _ = SyncWithServerAsync(force: alertStateChanged);
        }
    }

    private void QueueImmediateVideoAlertEvaluation(string streamId, bool blackFrameDetected = false)
    {
        lock (_immediateVideoAlertEvaluationSync)
        {
            if (blackFrameDetected)
            {
                _pendingImmediateBlackFrameDetections[streamId] = DateTimeOffset.UtcNow;
            }

            if (!_pendingImmediateVideoAlertEvaluations.Add(streamId))
            {
                return;
            }
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            RunImmediateVideoAlertEvaluation(streamId);
        }), DispatcherPriority.Background);
    }

    private void RunImmediateVideoAlertEvaluation(string streamId)
    {
        lock (_immediateVideoAlertEvaluationSync)
        {
            _pendingImmediateVideoAlertEvaluations.Remove(streamId);
        }

        var activeLayoutStreams = ActiveLayoutStreams();
        var changed = EvaluatePlaybackAlerts(activeLayoutStreams, streamId);
        UpdatePlaybackSummary(activeLayoutStreams);
        UpdateSingleTileChrome(activeLayoutStreams, streamId);
        if (!changed)
        {
            return;
        }

        LocalConfigStore.QueueSave(_config);
        RenderAlerts();
        _ = SyncWithServerAsync(force: true);
    }

    private bool EvaluatePlaybackAlerts(IReadOnlyList<StreamConfig>? activeLayoutStreams = null, string? focusStreamId = null)
    {
        var changed = false;
        activeLayoutStreams ??= ActiveLayoutStreams();
        foreach (var stream in activeLayoutStreams)
        {
            if (focusStreamId is not null && !string.Equals(stream.Id, focusStreamId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!stream.Enabled ||
                !stream.AnalysisEnabled ||
                (!IsPlaybackLoaded(stream.Id) && !_monitoringRequestedStreamIds.Contains(stream.Id)))
            {
                ClearPendingImmediateBlackFrameDetection(stream.Id);
                continue;
            }

            var profile = AlertProfileFor(stream);
            if (!_playbackSnapshots.TryGetValue(stream.Id, out var snapshot))
            {
                ClearPendingImmediateBlackFrameDetection(stream.Id);
                changed |= ResolveActiveAlert(stream.Id, "video.no_signal");
                changed |= ResolveActiveAlert(stream.Id, "video.low_fps");
                changed |= ResolveActiveAlert(stream.Id, "video.decode_error");
                changed |= ResolveActiveAlert(stream.Id, "video.black");
                changed |= ResolveActiveAlert(stream.Id, "video.freeze");
                changed |= ResolveActiveAlert(stream.Id, "audio.channel_missing");
                ClearIssueSince(stream.Id, "video.no_signal");
                ClearIssueSince(stream.Id, "video.opening");
                ClearIssueSince(stream.Id, "video.low_fps");
                ClearIssueSince(stream.Id, "video.black");
                ClearIssueSince(stream.Id, "video.freeze");
                ClearIssueSince(stream.Id, "audio.channel_missing");
                continue;
            }

            var decodeError = IsDecodeError(snapshot);
            var analysisUnavailable = !decodeError && IsVideoAnalysisUnavailable(stream.Id, snapshot, profile);
            LogVideoAnalysisState(stream.Id, profile, snapshot, analysisUnavailable);
            var noSignal = !decodeError && (IsNoSignal(snapshot) || IsOpeningTimedOut(stream.Id, snapshot, profile) || IsPlaybackTimedOut(stream.Id, profile));
            var immediateBlackFrameDetected = HasPendingImmediateBlackFrameDetection(stream.Id, profile);
            if (analysisUnavailable && IsAnalysisMissingOrEnded(stream.Id))
            {
                if (IssueDurationSec(stream.Id, "video.analysis_restart") >= Math.Max(3, profile.InputTimeoutSec))
                {
                    RestartVideoMonitor(stream);
                    ClearIssueSince(stream.Id, "video.analysis_restart");
                }
            }
            else
            {
                ClearIssueSince(stream.Id, "video.analysis_restart");
            }

            if (decodeError)
            {
                changed |= RaiseAlert(stream, "video.decode_error", "error", AlertMessageDecodeError);
                ClearIssueSince(stream.Id, "video.no_signal");
                ClearIssueSince(stream.Id, "video.black");
                ClearIssueSince(stream.Id, "video.freeze");
                ClearPendingImmediateBlackFrameDetection(stream.Id);
                changed |= ResolveActiveAlert(stream.Id, "video.no_signal");
                changed |= ResolveActiveAlert(stream.Id, "video.black");
                changed |= ResolveActiveAlert(stream.Id, "video.freeze");
            }
            else
            {
                changed |= ResolveActiveAlert(stream.Id, "video.decode_error");
            }

            if (noSignal)
            {
                if (IssueDurationSec(stream.Id, "video.no_signal") >= profile.InputTimeoutSec)
                {
                    changed |= RaiseAlert(stream, "video.no_signal", "error", AlertMessageNoSignal);
                }
                ClearIssueSince(stream.Id, "video.black");
                ClearIssueSince(stream.Id, "video.freeze");
                ClearPendingImmediateBlackFrameDetection(stream.Id);
                changed |= ResolveActiveAlert(stream.Id, "video.black");
                changed |= ResolveActiveAlert(stream.Id, "video.freeze");
            }
            else
            {
                ClearIssueSince(stream.Id, "video.no_signal");
                ClearIssueSince(stream.Id, "video.opening");
                changed |= ResolveActiveAlert(stream.Id, "video.no_signal");
            }

            if (!decodeError && !noSignal && (immediateBlackFrameDetected || IsBlackVideo(stream.Id, profile)))
            {
                MarkIssueSince(stream.Id, "video.black");
                ClearPendingImmediateBlackFrameDetection(stream.Id);
                changed |= RaiseAlert(stream, "video.black", "error", AlertMessageBlack);
            }
            else if (!decodeError && !noSignal && analysisUnavailable)
            {
                ClearIssueSince(stream.Id, "video.black");
            }
            else if (!decodeError)
            {
                ClearIssueSince(stream.Id, "video.black");
                changed |= ResolveActiveAlert(stream.Id, "video.black");
            }

            if (!decodeError && !noSignal && IsFreezeVideo(stream.Id, profile))
            {
                if (IssueDurationSec(stream.Id, "video.freeze") >= profile.MinFreezeDurationSec)
                {
                    changed |= RaiseAlert(stream, "video.freeze", "warning", AlertMessageFreeze);
                }
            }
            else if (!decodeError && !noSignal && analysisUnavailable)
            {
                ClearIssueSince(stream.Id, "video.freeze");
            }
            else if (!decodeError)
            {
                ClearIssueSince(stream.Id, "video.freeze");
                changed |= ResolveActiveAlert(stream.Id, "video.freeze");
            }

            if (IsLowFps(snapshot, profile))
            {
                if (IssueDurationSec(stream.Id, "video.low_fps") >= profile.MinLowFpsDurationSec)
                {
                    changed |= RaiseAlert(stream, "video.low_fps", "warning", $"{AlertMessageLowFpsPrefix}: {snapshot.Fps:0.##} fps");
                }
            }
            else
            {
                ClearIssueSince(stream.Id, "video.low_fps");
                changed |= ResolveActiveAlert(stream.Id, "video.low_fps");
            }

            if (stream.AudioMonitorEnabled && IsAudioChannelMissing(snapshot))
            {
                if (IssueDurationSec(stream.Id, "audio.channel_missing") >= profile.MinSilenceDurationSec)
                {
                    changed |= RaiseAlert(stream, "audio.channel_missing", "warning", AlertMessageAudioChannelMissing);
                }
            }
            else
            {
                ClearIssueSince(stream.Id, "audio.channel_missing");
                changed |= ResolveActiveAlert(stream.Id, "audio.channel_missing");
            }

            if (stream.AudioMonitorEnabled && IsAudioSilent(stream.Id, profile))
            {
                if (IssueDurationSec(stream.Id, "audio.silence") >= profile.MinSilenceDurationSec)
                {
                    changed |= RaiseAlert(stream, "audio.silence", "warning", AlertMessageAudioSilence);
                }
            }
            else
            {
                ClearIssueSince(stream.Id, "audio.silence");
                changed |= ResolveActiveAlert(stream.Id, "audio.silence");
            }

            if (stream.AudioMonitorEnabled && IsAudioClipping(stream.Id, profile))
            {
                changed |= RaiseAlert(stream, "audio.clipping", "warning", AlertMessageAudioClipping);
            }
            else
            {
                changed |= ResolveActiveAlert(stream.Id, "audio.clipping");
            }
        }

        foreach (var stream in activeLayoutStreams)
        {
            if (focusStreamId is not null && !string.Equals(stream.Id, focusStreamId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsPlaybackLoaded(stream.Id))
            {
                continue;
            }

            changed |= ResolveActiveAlert(stream.Id, "video.no_signal");
            changed |= ResolveActiveAlert(stream.Id, "video.low_fps");
            changed |= ResolveActiveAlert(stream.Id, "video.decode_error");
            changed |= ResolveActiveAlert(stream.Id, "video.black");
            changed |= ResolveActiveAlert(stream.Id, "video.freeze");
            changed |= ResolveActiveAlert(stream.Id, "audio.channel_missing");
            changed |= ResolveActiveAlert(stream.Id, "audio.silence");
            changed |= ResolveActiveAlert(stream.Id, "audio.clipping");
            ClearIssueSince(stream.Id, "video.no_signal");
            ClearIssueSince(stream.Id, "video.opening");
            ClearIssueSince(stream.Id, "video.low_fps");
            ClearIssueSince(stream.Id, "video.black");
            ClearIssueSince(stream.Id, "video.freeze");
            ClearIssueSince(stream.Id, "audio.channel_missing");
            ClearIssueSince(stream.Id, "audio.silence");
        }

        return changed;
    }

    private AlertProfileConfig AlertProfileFor(StreamConfig stream)
    {
        return _config.AlertProfiles.FirstOrDefault(profile => profile.Id == stream.AlertProfileId)
            ?? _config.AlertProfiles.FirstOrDefault()
            ?? new AlertProfileConfig();
    }

    private bool RaiseAlert(StreamConfig stream, string type, string severity, string message)
    {
        if (ActiveAlert(stream.Id, type) is not null)
        {
            return false;
        }

        message = AlertMessage(type, message);
        var now = DateTimeOffset.UtcNow;
        _config.AlertHistory.Add(new AlertRecord
        {
            Id = $"alert-{now:yyyyMMddHHmmssfff}",
            StreamId = stream.Id,
            Type = type,
            Severity = severity,
            Status = "open",
            Message = message,
            StartedAt = now,
            UpdatedAt = now,
            SourceClientId = _config.ClientId
        });
        return true;
    }

    private static string AlertMessage(string type, string fallback)
    {
        return type switch
        {
            "video.decode_error" => AlertMessageDecodeError,
            "video.no_signal" => AlertMessageNoSignal,
            "video.low_fps" => LowFpsMessage(fallback),
            "video.black" => AlertMessageBlack,
            "video.freeze" => AlertMessageFreeze,
            "audio.channel_missing" => AlertMessageAudioChannelMissing,
            "audio.silence" => AlertMessageAudioSilence,
            "audio.clipping" => AlertMessageAudioClipping,
            _ => fallback
        };
    }

    private static string LowFpsMessage(string fallback)
    {
        var match = Regex.Match(fallback, @"([0-9]+(?:[.,][0-9]+)?)\s*fps", RegexOptions.IgnoreCase);
        return match.Success ? $"{AlertMessageLowFpsPrefix}: {match.Groups[1].Value} fps" : AlertMessageLowFpsPrefix;
    }

    private bool RefreshSnoozedAlerts()
    {
        var changed = false;
        foreach (var alert in _config.AlertHistory.Where(alert =>
            string.Equals(alert.Status, "snoozed", StringComparison.OrdinalIgnoreCase) &&
            alert.SnoozedUntil is not null &&
            alert.SnoozedUntil <= DateTimeOffset.UtcNow))
        {
            alert.Status = "open";
            alert.SnoozedUntil = null;
            alert.UpdatedAt = DateTimeOffset.UtcNow;
            alert.SyncedAt = null;
            alert.SourceClientId = _config.ClientId;
            changed = true;
        }

        return changed;
    }

    private void PrewarmAlertSoundWaves()
    {
        _ = AlertSoundWave("error");
        _ = AlertSoundWave("warning");
        _ = AlertSoundWave("info");
    }

    private void UpdateContinuousAlertSound()
    {
        try
        {
            var audibleAlert = AudibleOpenAlerts()
                .OrderByDescending(alert => SeverityRank(alert.Severity))
                .ThenByDescending(alert => alert.UpdatedAt == default ? alert.StartedAt : alert.UpdatedAt)
                .FirstOrDefault();
            if (audibleAlert is null)
            {
                StopContinuousAlertSound();
                return;
            }

            var soundKey = AlertSoundKey(audibleAlert.Severity);
            if (string.Equals(_activeAlertSoundKey, soundKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopContinuousAlertSound();
            var wave = AlertSoundWave(soundKey);
            _activeAlertSoundStream = new MemoryStream(wave, writable: false);
            _activeAlertSoundPlayer = new SoundPlayer(_activeAlertSoundStream);
            _activeAlertSoundPlayer.Load();
            _activeAlertSoundPlayer.PlayLooping();
            _activeAlertSoundKey = soundKey;
        }
        catch (Exception ex)
        {
            StopContinuousAlertSound();
            AppendDesktopLog($"Alert sound failed: {ex.Message}");
        }
    }

    private IEnumerable<AlertRecord> AudibleOpenAlerts()
    {
        if (!_config.AlertSoundEnabled)
        {
            return [];
        }

        return _config.AlertHistory
            .Where(alert => string.Equals(alert.Status, "open", StringComparison.OrdinalIgnoreCase))
            .Where(alert => _config.Streams.Any(stream =>
                string.Equals(stream.Id, alert.StreamId, StringComparison.OrdinalIgnoreCase) &&
                !stream.AlertMuted));
    }

    private void StopContinuousAlertSound()
    {
        try
        {
            _activeAlertSoundPlayer?.Stop();
            _activeAlertSoundPlayer?.Dispose();
            _activeAlertSoundStream?.Dispose();
        }
        catch
        {
            // Alert sounds are best-effort and must not interrupt monitoring.
        }
        finally
        {
            _activeAlertSoundPlayer = null;
            _activeAlertSoundStream = null;
            _activeAlertSoundKey = null;
        }
    }

    private static string AlertSoundKey(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "critical" or "error" => "error",
            "warning" => "warning",
            _ => "info"
        };
    }

    private byte[] AlertSoundWave(string soundKey)
    {
        if (_alertSoundWaves.TryGetValue(soundKey, out var cached))
        {
            return cached;
        }

        var pattern = soundKey switch
        {
            "error" => new[] { (Frequency: 880.0, DurationMs: 140), (Frequency: 0.0, DurationMs: 60), (Frequency: 1175.0, DurationMs: 140), (Frequency: 0.0, DurationMs: 60), (Frequency: 880.0, DurationMs: 180), (Frequency: 0.0, DurationMs: 360) },
            "warning" => new[] { (Frequency: 660.0, DurationMs: 170), (Frequency: 0.0, DurationMs: 90), (Frequency: 660.0, DurationMs: 170), (Frequency: 0.0, DurationMs: 420) },
            _ => new[] { (Frequency: 523.25, DurationMs: 180), (Frequency: 0.0, DurationMs: 620) }
        };
        cached = BuildAlertWave(pattern);
        _alertSoundWaves[soundKey] = cached;
        return cached;
    }

    private static byte[] BuildAlertWave(IReadOnlyList<(double Frequency, int DurationMs)> pattern)
    {
        const int sampleRate = 44100;
        const short bitsPerSample = 16;
        const short channels = 1;
        const double amplitude = 0.28;
        var samples = new List<short>();
        foreach (var (frequency, durationMs) in pattern)
        {
            var count = Math.Max(1, sampleRate * durationMs / 1000);
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i / 220.0, (count - i) / 220.0));
                var value = frequency <= 0
                    ? 0
                    : Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * envelope;
                samples.Add((short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue));
            }
        }

        var dataSize = samples.Count * sizeof(short);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static string CurrentApplicationVersion()
    {
        var informationalVersion = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+')[0];
        }

        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }

        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private async System.Threading.Tasks.Task SyncWithServerAsync(bool force)
    {
        if (_serverSyncInFlight ||
            !_config.ServerSyncEnabled ||
            string.IsNullOrWhiteSpace(_config.ServerUrl) ||
            !Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out _))
        {
            return;
        }

        if (!force && DateTimeOffset.UtcNow - _lastServerSyncAt < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _serverSyncInFlight = true;
        try
        {
            var baseUrl = _config.ServerUrl.TrimEnd('/');
            var registration = new ServerClientRegistration(
                _config.ClientId,
                _config.ClientName,
                CurrentApplicationVersion(),
                "desktop-windows",
                new Dictionary<string, bool>
                {
                    ["standalone_config"] = true,
                    ["playback"] = true,
                    ["local_alerts"] = true,
                    ["next_ui"] = true,
                    ["meter_mode_switch"] = true
                });
            await PostJsonAsync($"{baseUrl}/clients/register", registration);

            var heartbeat = new ServerClientHeartbeat(
                _config.ClientId,
                _config.ActiveLayoutId,
                IsAnyPlaybackLoaded() ? "monitoring" : "idle",
                _config.AlertHistory.Count(alert => ActiveAlertStatuses.Contains(alert.Status)),
                DateTimeOffset.UtcNow);
            await PostJsonAsync($"{baseUrl}/clients/heartbeat", heartbeat);
            var streams = await GetJsonAsync<List<StreamConfig>>($"{baseUrl}/streams") ?? [];
            var layouts = await GetJsonAsync<List<LayoutConfig>>($"{baseUrl}/layouts") ?? [];
            var alertProfiles = await GetJsonAsync<List<AlertProfileConfig>>($"{baseUrl}/alert-profiles") ?? [];
            var clients = await GetJsonAsync<List<ServerClientStatus>>($"{baseUrl}/clients") ?? [];
            var configChanged = ApplyServerConfig(streams, layouts, alertProfiles);

            var uploaded = 0;
            foreach (var alert in _config.AlertHistory.Where(alert => alert.SyncedAt is null).ToList())
            {
                var payload = new ServerAlert(
                    alert.Id,
                    alert.StreamId,
                    alert.Type,
                    alert.Severity,
                    alert.Status,
                    alert.Message,
                    alert.StartedAt,
                    alert.UpdatedAt == default ? DateTimeOffset.UtcNow : alert.UpdatedAt,
                    alert.SnoozedUntil,
                    string.IsNullOrWhiteSpace(alert.SourceClientId) ? _config.ClientId : alert.SourceClientId);
                await PostJsonAsync($"{baseUrl}/client-alerts", payload);
                alert.SyncedAt = DateTimeOffset.UtcNow;
                uploaded++;
            }
            var serverAlerts = await GetJsonAsync<List<ServerAlert>>($"{baseUrl}/alerts") ?? [];
            var alertsChanged = ApplyServerAlerts(serverAlerts);

            if (uploaded > 0 || configChanged || alertsChanged)
            {
                LocalConfigStore.QueueSave(_config);
            }

            _lastServerSyncAt = DateTimeOffset.UtcNow;
            SetTextIfChanged(ServerSyncStatusText, $"server sync: ok / config: {streams.Count} streams, {layouts.Count} layouts, {alertProfiles.Count} profiles / alerts: {serverAlerts.Count} pulled, {uploaded} uploaded / {DateTimeOffset.Now:HH:mm:ss}");
            SetTextIfChanged(ServerClientSummaryText, ServerClientSummary(clients));
        }
        catch (Exception ex)
        {
            SetTextIfChanged(ServerSyncStatusText, $"server sync: failed / {ex.Message}");
            SetTextIfChanged(ServerClientSummaryText, "server clients: unavailable");
        }
        finally
        {
            _serverSyncInFlight = false;
        }
    }

    private bool ApplyServerConfig(
        IReadOnlyCollection<StreamConfig> streams,
        IReadOnlyCollection<LayoutConfig> layouts,
        IReadOnlyCollection<AlertProfileConfig> alertProfiles)
    {
        var selectedStreamId = (StreamsList.SelectedItem as StreamConfig)?.Id;
        var changed = false;

        if (streams.Count > 0)
        {
            var existingByID = _config.Streams.ToDictionary(stream => stream.Id, StringComparer.OrdinalIgnoreCase);
            var merged = new ObservableCollection<StreamConfig>();
            foreach (var incoming in streams.OrderBy(stream => stream.Name, StringComparer.OrdinalIgnoreCase))
            {
                existingByID.TryGetValue(incoming.Id, out var existing);
                var inputUrl = incoming.InputUrl ?? "";
                var receivedMaskedURL = LooksMasked(inputUrl);
                var preservedLocalURL = existing is not null &&
                    !LooksMasked(existing.InputUrl) &&
                    !string.IsNullOrWhiteSpace(existing.InputUrl);
                var effectiveURL = receivedMaskedURL && preservedLocalURL
                    ? existing!.InputUrl
                    : inputUrl;
                var enabled = incoming.Enabled;
                if (receivedMaskedURL && !preservedLocalURL)
                {
                    enabled = false;
                }

                if (existing is not null &&
                    (IsPlaybackLoaded(existing.Id) || _monitoringRequestedStreamIds.Contains(existing.Id)) &&
                    (!string.Equals(existing.InputUrl, effectiveURL, StringComparison.Ordinal) || !enabled))
                {
                    StopStreamPlayback(existing.Id);
                }

                merged.Add(new StreamConfig
                {
                    Id = incoming.Id,
                    Name = incoming.Name,
                    Enabled = enabled,
                    InputUrl = effectiveURL,
                    InputProtocol = incoming.InputProtocol,
                    Group = incoming.Group,
                    Tags = incoming.Tags ?? [],
                    AudioMonitorEnabled = incoming.AudioMonitorEnabled,
                    AnalysisEnabled = incoming.AnalysisEnabled,
                    AlertMuted = existing?.AlertMuted ?? incoming.AlertMuted,
                    AlertProfileId = string.IsNullOrWhiteSpace(incoming.AlertProfileId) ? "default" : incoming.AlertProfileId
                });
            }

            foreach (var removedID in _players.Keys
                         .Concat(_ffmpegPlaybackSessions.Keys)
                         .Concat(_monitoringRequestedStreamIds)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(streamID => merged.All(stream => !string.Equals(stream.Id, streamID, StringComparison.OrdinalIgnoreCase)))
                         .ToList())
            {
                StopStreamPlayback(removedID);
            }

            if (JsonDifferent(_config.Streams, merged))
            {
                _config.Streams = merged;
                changed = true;
            }
        }

        if (layouts.Count > 0)
        {
            var merged = new ObservableCollection<LayoutConfig>(
                layouts.Select(layout => new LayoutConfig
                {
                    Id = layout.Id,
                    Name = layout.Name,
                    Kind = string.IsNullOrWhiteSpace(layout.Kind) ? "2x2" : layout.Kind,
                    Tiles = new ObservableCollection<LayoutTileConfig>(
                        (layout.Tiles ?? [])
                            .OrderBy(tile => tile.Index)
                            .Select(tile => new LayoutTileConfig
                            {
                                Index = tile.Index,
                                StreamId = tile.StreamId,
                                Row = tile.Row,
                                Column = tile.Column,
                                RowSpan = Math.Max(1, tile.RowSpan),
                                ColSpan = Math.Max(1, tile.ColSpan)
                            }))
                }));
            if (JsonDifferent(_config.Layouts, merged))
            {
                _config.Layouts = merged;
                changed = true;
            }
        }

        if (alertProfiles.Count > 0)
        {
            var merged = new ObservableCollection<AlertProfileConfig>(
                alertProfiles.Select(NormalizeAlertProfile));
            if (JsonDifferent(_config.AlertProfiles, merged))
            {
                _config.AlertProfiles = merged;
                changed = true;
            }
        }

        EnsureStandardLayouts();
        if (_config.Layouts.All(layout => layout.Id != _config.ActiveLayoutId))
        {
            _config.ActiveLayoutId = _config.Layouts.FirstOrDefault()?.Id ?? "layout-2x2";
            changed = true;
        }

        if (changed)
        {
            RefreshConfigBindings(selectedStreamId);
        }

        return changed;
    }

    private bool ApplyServerAlerts(IReadOnlyCollection<ServerAlert> serverAlerts)
    {
        var changed = false;
        var syncedAt = DateTimeOffset.UtcNow;
        var byID = _config.AlertHistory.ToDictionary(alert => alert.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in serverAlerts)
        {
            if (string.IsNullOrWhiteSpace(incoming.Id) ||
                string.IsNullOrWhiteSpace(incoming.StreamId) ||
                string.IsNullOrWhiteSpace(incoming.Type))
            {
                continue;
            }

            var incomingRecord = new AlertRecord
            {
                Id = incoming.Id,
                StreamId = incoming.StreamId,
                Type = incoming.Type,
                Severity = incoming.Severity,
                Status = incoming.Status,
                Message = incoming.Message,
                StartedAt = incoming.StartedAt == default ? syncedAt : incoming.StartedAt,
                UpdatedAt = incoming.UpdatedAt == default ? syncedAt : incoming.UpdatedAt,
                SnoozedUntil = incoming.SnoozedUntil,
                SourceClientId = incoming.SourceClientId ?? "",
                SyncedAt = syncedAt
            };

            if (byID.TryGetValue(incoming.Id, out var existing))
            {
                var localUpdatedAt = existing.UpdatedAt == default ? existing.StartedAt : existing.UpdatedAt;
                if (incomingRecord.UpdatedAt >= localUpdatedAt && AlertContentDifferent(existing, incomingRecord))
                {
                    CopyAlert(existing, incomingRecord);
                    changed = true;
                }
                continue;
            }

            _config.AlertHistory.Add(incomingRecord);
            changed = true;
        }

        changed |= DeduplicateLocalActiveAlerts();
        if (changed)
        {
            RenderAlerts();
            UpdateAllTileChrome();
        }
        return changed;
    }

    private bool DeduplicateLocalActiveAlerts()
    {
        var changed = false;
        var groups = _config.AlertHistory
            .Where(alert => ActiveAlertStatuses.Contains(alert.Status))
            .GroupBy(alert => $"{alert.StreamId}\u001f{alert.Type}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var keeper = group
                .OrderByDescending(alert => SeverityRank(alert.Severity))
                .ThenByDescending(alert => alert.UpdatedAt == default ? alert.StartedAt : alert.UpdatedAt)
                .First();
            foreach (var duplicate in group.Where(alert => !ReferenceEquals(alert, keeper)).ToList())
            {
                _config.AlertHistory.Remove(duplicate);
                changed = true;
            }
        }

        return changed;
    }

    private static void CopyAlert(AlertRecord target, AlertRecord source)
    {
        target.StreamId = source.StreamId;
        target.Type = source.Type;
        target.Severity = source.Severity;
        target.Status = source.Status;
        target.Message = source.Message;
        target.StartedAt = source.StartedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.SnoozedUntil = source.SnoozedUntil;
        target.SyncedAt = source.SyncedAt;
        target.SourceClientId = source.SourceClientId;
    }

    private static bool AlertContentDifferent(AlertRecord left, AlertRecord right)
    {
        return !string.Equals(left.StreamId, right.StreamId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.Severity, right.Severity, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.Status, right.Status, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.Message, right.Message, StringComparison.Ordinal) ||
            left.StartedAt != right.StartedAt ||
            left.UpdatedAt != right.UpdatedAt ||
            left.SnoozedUntil != right.SnoozedUntil ||
            !string.Equals(left.SourceClientId, right.SourceClientId, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshConfigBindings(string? selectedStreamId)
    {
        _isLoading = true;
        try
        {
            StreamsList.ItemsSource = _config.Streams;
            LayoutCombo.ItemsSource = _config.Layouts;
            LayoutCombo.SelectedItem = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
                ?? _config.Layouts.FirstOrDefault();
            MainLayoutCombo.ItemsSource = _config.Layouts;
            MainLayoutCombo.SelectedItem = LayoutCombo.SelectedItem;
            StreamAlertProfileCombo.ItemsSource = _config.AlertProfiles;
            AlertProfileCombo.ItemsSource = _config.AlertProfiles;
            AlertProfileCombo.SelectedItem ??= _config.AlertProfiles.FirstOrDefault();
            PopulateAlertProfileEditor(AlertProfileCombo.SelectedItem as AlertProfileConfig);
            StreamsList.SelectedItem = _config.Streams.FirstOrDefault(stream => string.Equals(stream.Id, selectedStreamId, StringComparison.OrdinalIgnoreCase))
                ?? _config.Streams.FirstOrDefault();
            RenderAlerts();
            RenderTiles();
            ReconcileActiveLayoutMonitoring();
            UpdatePlaybackSummary();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private static AlertProfileConfig NormalizeAlertProfile(AlertProfileConfig profile)
    {
        var defaults = new AlertProfileConfig();
        return new AlertProfileConfig
        {
            Id = string.IsNullOrWhiteSpace(profile.Id) ? defaults.Id : profile.Id,
            Name = string.IsNullOrWhiteSpace(profile.Name) ? defaults.Name : profile.Name,
            BlackThreshold = profile.BlackThreshold > 0 ? profile.BlackThreshold : defaults.BlackThreshold,
            BlackPixelThreshold = profile.BlackPixelThreshold > 0 ? profile.BlackPixelThreshold : defaults.BlackPixelThreshold,
            BlackPixelRatioThreshold = profile.BlackPixelRatioThreshold > 0 ? profile.BlackPixelRatioThreshold : defaults.BlackPixelRatioThreshold,
            MinBlackDurationSec = 0,
            FreezeDifferenceThreshold = profile.FreezeDifferenceThreshold > 0 ? profile.FreezeDifferenceThreshold : defaults.FreezeDifferenceThreshold,
            MinFreezeDurationSec = profile.MinFreezeDurationSec > 0 ? profile.MinFreezeDurationSec : defaults.MinFreezeDurationSec,
            MinFreezeFrames = profile.MinFreezeFrames > 0 ? profile.MinFreezeFrames : defaults.MinFreezeFrames,
            SilenceThresholdDb = profile.SilenceThresholdDb < 0 ? profile.SilenceThresholdDb : defaults.SilenceThresholdDb,
            MinSilenceDurationSec = profile.MinSilenceDurationSec > 0 ? profile.MinSilenceDurationSec : defaults.MinSilenceDurationSec,
            ClippingThresholdDb = profile.ClippingThresholdDb < 0 ? profile.ClippingThresholdDb : defaults.ClippingThresholdDb,
            ClippingCountThreshold = profile.ClippingCountThreshold > 0 ? profile.ClippingCountThreshold : defaults.ClippingCountThreshold,
            InputTimeoutSec = profile.InputTimeoutSec > 0 ? profile.InputTimeoutSec : defaults.InputTimeoutSec,
            LowFpsThreshold = profile.LowFpsThreshold > 0 ? profile.LowFpsThreshold : defaults.LowFpsThreshold,
            MinLowFpsDurationSec = profile.MinLowFpsDurationSec > 0 ? profile.MinLowFpsDurationSec : defaults.MinLowFpsDurationSec
        };
    }

    private static bool JsonDifferent<T>(T left, T right)
    {
        return !string.Equals(
            JsonSerializer.Serialize(left, ServerJsonOptions),
            JsonSerializer.Serialize(right, ServerJsonOptions),
            StringComparison.Ordinal);
    }

    private async System.Threading.Tasks.Task<T?> GetJsonAsync<T>(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_config.ClientToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ClientToken);
        }

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(stream, ServerJsonOptions);
    }

    private async System.Threading.Tasks.Task PostJsonAsync(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ServerJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(_config.ClientToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ClientToken);
        }

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Content = content;
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static string ServerClientSummary(IReadOnlyCollection<ServerClientStatus> clients)
    {
        if (clients.Count == 0)
        {
            return "server clients: none";
        }

        var online = clients.Count(client => client.Online);
        var offline = clients.Count - online;
        var agents = clients.Count(client => string.Equals(client.Mode, "agent", StringComparison.OrdinalIgnoreCase));
        var desktops = clients.Count(client => string.Equals(client.Mode, "desktop-windows", StringComparison.OrdinalIgnoreCase));
        var activeAlerts = clients.Sum(client => client.ActiveAlerts);
        return $"server clients: {online} online / {offline} offline / agents {agents} / desktops {desktops} / active alerts {activeAlerts}";
    }

    private bool ResolveActiveAlert(string streamId, string type)
    {
        var changed = false;
        var now = DateTimeOffset.UtcNow;
        foreach (var alert in _config.AlertHistory.Where(alert =>
            string.Equals(alert.StreamId, streamId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(alert.Type, type, StringComparison.OrdinalIgnoreCase) &&
            ActiveAlertStatuses.Contains(alert.Status)))
        {
            if (string.Equals(type, "video.black", StringComparison.OrdinalIgnoreCase) &&
                now - alert.StartedAt < ImmediateBlackAlertMinimumVisibleDuration)
            {
                continue;
            }

            alert.Status = "resolved";
            alert.SnoozedUntil = null;
            alert.UpdatedAt = now;
            alert.SyncedAt = null;
            alert.SourceClientId = _config.ClientId;
            changed = true;
        }

        return changed;
    }

    private AlertRecord? ActiveAlert(string streamId, string type)
    {
        return _config.AlertHistory.FirstOrDefault(alert =>
            string.Equals(alert.StreamId, streamId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(alert.Type, type, StringComparison.OrdinalIgnoreCase) &&
            ActiveAlertStatuses.Contains(alert.Status));
    }

    private void MarkIssueSince(string streamId, string type)
    {
        var key = IssueKey(streamId, type);
        _issueSince.TryAdd(key, DateTimeOffset.UtcNow);
    }

    private void ClearIssueSince(string streamId, string type)
    {
        _issueSince.Remove(IssueKey(streamId, type));
    }

    private bool HasPendingImmediateBlackFrameDetection(string streamId, AlertProfileConfig profile)
    {
        lock (_immediateVideoAlertEvaluationSync)
        {
            if (!_pendingImmediateBlackFrameDetections.TryGetValue(streamId, out var detectedAt))
            {
                return false;
            }

            var maxAge = TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec));
            if (DateTimeOffset.UtcNow - detectedAt <= maxAge)
            {
                return true;
            }

            _pendingImmediateBlackFrameDetections.Remove(streamId);
            return false;
        }
    }

    private void ClearPendingImmediateBlackFrameDetection(string streamId)
    {
        lock (_immediateVideoAlertEvaluationSync)
        {
            _pendingImmediateBlackFrameDetections.Remove(streamId);
        }
    }

    private double IssueDurationSec(string streamId, string type)
    {
        var key = IssueKey(streamId, type);
        if (!_issueSince.TryGetValue(key, out var startedAt))
        {
            _issueSince[key] = DateTimeOffset.UtcNow;
            return 0;
        }

        return Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
    }

    private static double ClippingThresholdDb(AlertProfileConfig profile)
    {
        return profile.ClippingThresholdDb < 0 ? profile.ClippingThresholdDb : -1;
    }

    private static string IssueKey(string streamId, string type)
    {
        return $"{streamId}:{type}";
    }

    private void TrackPlaybackProgress(string streamId, PlaybackSnapshot snapshot)
    {
        if (snapshot.DisplayedPictures > 0 &&
            (!_lastDisplayedPictures.TryGetValue(streamId, out var lastDisplayedPictures) ||
                snapshot.DisplayedPictures > lastDisplayedPictures))
        {
            _lastDisplayedPictures[streamId] = snapshot.DisplayedPictures;
            _lastVideoPresentedAt[streamId] = snapshot.UpdatedAt;
        }

        if (!IsMonitoringPlaybackState(snapshot.State))
        {
            return;
        }

        var marker = PlaybackProgressMarker(snapshot);
        if (!_lastPlaybackProgressMarker.TryGetValue(streamId, out var lastMarker) ||
            marker > lastMarker ||
            !_lastPlaybackTimeMs.TryGetValue(streamId, out var lastTimeMs) ||
            snapshot.TimeMs > lastTimeMs ||
            snapshot.TimeMs == 0 && lastTimeMs < 0)
        {
            _lastPlaybackTimeMs[streamId] = snapshot.TimeMs;
            _lastPlaybackProgressMarker[streamId] = marker;
            _lastPlaybackProgressAt[streamId] = DateTimeOffset.UtcNow;
            if (string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
                snapshot.DisplayedPictures > 0)
            {
                _playbackRestartAttempts.Remove(streamId);
            }
        }
    }

    private bool IsPlaybackTimedOut(string streamId, AlertProfileConfig profile)
    {
        if (!IsPlaybackLoaded(streamId) ||
            !_playbackSnapshots.TryGetValue(streamId, out var snapshot) ||
            !IsMonitoringPlaybackState(snapshot.State))
        {
            return false;
        }

        if (!_lastPlaybackProgressAt.TryGetValue(streamId, out var lastProgressAt))
        {
            _lastPlaybackProgressAt[streamId] = DateTimeOffset.UtcNow;
            return false;
        }

        return (DateTimeOffset.UtcNow - lastProgressAt).TotalSeconds >= Math.Max(1, profile.InputTimeoutSec);
    }

    private bool IsOpeningTimedOut(string streamId, PlaybackSnapshot snapshot, AlertProfileConfig profile)
    {
        if (!string.Equals(snapshot.State, "opening", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(snapshot.State, "buffering", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(snapshot.State, "starting", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IssueDurationSec(streamId, "video.opening") >= Math.Max(1, profile.InputTimeoutSec);
    }

    private bool IsVideoAnalysisUnavailable(string streamId, PlaybackSnapshot snapshot, AlertProfileConfig profile)
    {
        if (!string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !TryGetVideoLuma(streamId, profile, out var luma) ||
            DateTimeOffset.UtcNow - luma.UpdatedAt > TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec));
    }

    private bool IsPlaybackLoaded(string streamId)
    {
        return _players.ContainsKey(streamId) || _ffmpegPlaybackSessions.ContainsKey(streamId);
    }

    private bool IsAnyPlaybackLoaded()
    {
        return _monitoringRequestedStreamIds.Count > 0 ||
            _players.Count > 0 ||
            _ffmpegPlaybackSessions.Count > 0;
    }

    private object? PlaybackOwnerFor(string streamId)
    {
        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var ffmpegSession))
        {
            return ffmpegSession;
        }

        return _players.TryGetValue(streamId, out var player) ? player : null;
    }

    private bool PlaybackOwnerMatches(string streamId, object expectedOwner)
    {
        var currentOwner = PlaybackOwnerFor(streamId);
        return ReferenceEquals(expectedOwner, MissingPlaybackOwner)
            ? currentOwner is null && _playbackRestartAfter.ContainsKey(streamId)
            : ReferenceEquals(currentOwner, expectedOwner);
    }

    internal static bool ShouldRestartReceiver(string state, bool openingTimedOut, bool playbackTimedOut)
    {
        return IsRecoverablePlaybackState(state) || openingTimedOut || playbackTimedOut;
    }

    private bool ReceiverNeedsRestart(string streamId, PlaybackSnapshot snapshot, AlertProfileConfig profile)
    {
        var stream = _config.Streams.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, streamId, StringComparison.OrdinalIgnoreCase));
        if (stream is not null && IsPassiveReceiverAwaitingInput(stream, snapshot))
        {
            return false;
        }

        return ShouldRestartReceiver(
            snapshot.State,
            IsOpeningTimedOut(streamId, snapshot, profile),
            IsPlaybackTimedOut(streamId, profile));
    }

    private bool IsPassiveReceiverAwaitingInput(StreamConfig stream, PlaybackSnapshot snapshot)
    {
        if (!IsPassiveReceiverInput(stream.InputUrl, stream.InputProtocol) ||
            (!string.Equals(snapshot.State, "starting", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(snapshot.State, "opening", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(snapshot.State, "buffering", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_ffmpegPlaybackSessions.TryGetValue(stream.Id, out var session))
        {
            return session.IsProcessRunning && !session.HasFrame;
        }

        return _players.TryGetValue(stream.Id, out var player) && IsActivePlaybackState(player.State);
    }

    private void SchedulePlaybackRestartIfNeeded(string streamId, PlaybackSnapshot snapshot)
    {
        if (_playbackStartInFlight || !_monitoringRequestedStreamIds.Contains(streamId))
        {
            return;
        }

        var stream = _config.Streams.FirstOrDefault(candidate => string.Equals(candidate.Id, streamId, StringComparison.OrdinalIgnoreCase));
        if (stream is null ||
            !stream.Enabled ||
            !CanPlayStream(stream) ||
            !IsStreamInActiveLayout(streamId))
        {
            _playbackRestartAfter.Remove(streamId);
            _playbackRestartAttempts.Remove(streamId);
            return;
        }

        var profile = AlertProfileFor(stream);
        if (!ReceiverNeedsRestart(streamId, snapshot, profile))
        {
            _playbackRestartAfter.Remove(streamId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_playbackRestartAfter.TryGetValue(streamId, out var restartAfter))
        {
            var restartAttempts = _playbackRestartAttempts.TryGetValue(streamId, out var attempts) ? attempts : 0;
            _playbackRestartAfter[streamId] = now.Add(PlaybackRestartDelay(stream, restartAttempts));
            return;
        }

        if (now < restartAfter)
        {
            return;
        }

        if (!_pendingPlaybackRestarts.Add(streamId))
        {
            return;
        }

        var nextRestartAttempt = (_playbackRestartAttempts.TryGetValue(streamId, out var currentAttempts) ? currentAttempts : 0) + 1;
        _playbackRestartAttempts[streamId] = nextRestartAttempt;
        _playbackRestartAfter[streamId] = now.Add(PlaybackRestartDelay(stream, nextRestartAttempt));
        var restartReason = snapshot.State;
        var expectedOwner = PlaybackOwnerFor(streamId) ?? MissingPlaybackOwner;
        var operationGeneration = _playbackOperationGeneration;

        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RestartSingleStreamPlaybackAsync(streamId, restartReason, expectedOwner, operationGeneration);
            }
            finally
            {
                _pendingPlaybackRestarts.Remove(streamId);
            }
        }), DispatcherPriority.Background);
    }

    private void ScheduleOwnerlessPlaybackRestarts()
    {
        foreach (var streamId in _playbackRestartAfter.Keys.ToArray())
        {
            if (IsPlaybackLoaded(streamId))
            {
                continue;
            }

            var snapshot = _playbackSnapshots.TryGetValue(streamId, out var currentSnapshot)
                ? currentSnapshot
                : EmptyPlaybackSnapshot("error");
            SchedulePlaybackRestartIfNeeded(streamId, snapshot);
        }
    }

    private void MarkPlaybackRestartAfterFailure(string streamId)
    {
        var stream = CurrentActivePlayableStream(streamId);
        if (stream is null || !_monitoringRequestedStreamIds.Contains(streamId))
        {
            _playbackRestartAfter.Remove(streamId);
            _playbackRestartAttempts.Remove(streamId);
            return;
        }

        var restartAttempts = _playbackRestartAttempts.TryGetValue(streamId, out var attempts) ? attempts : 0;
        _playbackRestartAfter[streamId] = DateTimeOffset.UtcNow.Add(PlaybackRestartDelay(stream, restartAttempts));
    }

    private TimeSpan PlaybackRestartDelay(StreamConfig stream, int restartAttempts)
    {
        return TimeSpan.FromSeconds(PlaybackRestartDelaySecondsForStream(
            AlertProfileFor(stream).InputTimeoutSec,
            restartAttempts,
            stream.Id));
    }

    internal static double PlaybackRestartDelaySeconds(double inputTimeoutSec, int restartAttempts)
    {
        var baseSeconds = Math.Max(5.0, inputTimeoutSec);
        var multiplier = Math.Pow(2, Math.Clamp(restartAttempts, 0, 4));
        return Math.Min(60.0, baseSeconds * multiplier);
    }

    internal static double PlaybackRestartDelaySecondsForStream(double inputTimeoutSec, int restartAttempts, string streamId)
    {
        var baseDelay = PlaybackRestartDelaySeconds(inputTimeoutSec, restartAttempts);
        if (baseDelay >= 60 || string.IsNullOrWhiteSpace(streamId))
        {
            return baseDelay;
        }

        uint hash = 2166136261;
        foreach (var character in streamId)
        {
            hash ^= char.ToUpperInvariant(character);
            hash *= 16777619;
        }

        var maximumJitter = Math.Min(2.0, baseDelay * 0.25);
        var jitterFraction = (hash % 1024) / 1023.0;
        return Math.Min(60.0, baseDelay + maximumJitter * jitterFraction);
    }

    private async System.Threading.Tasks.Task RestartSingleStreamPlaybackAsync(
        string streamId,
        string reason,
        object expectedOwner,
        long operationGeneration)
    {
        if (!IsPlaybackOperationCurrent(operationGeneration) ||
            _playbackStartInFlight ||
            !_monitoringRequestedStreamIds.Contains(streamId) ||
            !_playbackRestartAfter.ContainsKey(streamId))
        {
            return;
        }

        if (!PlaybackOwnerMatches(streamId, expectedOwner))
        {
            return;
        }

        var stream = _config.Streams.FirstOrDefault(candidate => string.Equals(candidate.Id, streamId, StringComparison.OrdinalIgnoreCase));
        if (stream is null ||
            !stream.Enabled ||
            !CanPlayStream(stream) ||
            !IsStreamInActiveLayout(streamId))
        {
            _playbackRestartAfter.Remove(streamId);
            _playbackRestartAttempts.Remove(streamId);
            return;
        }

        if (!_playbackSnapshots.TryGetValue(streamId, out var currentSnapshot) ||
            !ReceiverNeedsRestart(streamId, currentSnapshot, AlertProfileFor(stream)))
        {
            _playbackRestartAfter.Remove(streamId);
            return;
        }

        try
        {
            var restarted = await StartStreamPlaybackCoordinatedAsync(
                streamId,
                restartUnhealthy: true,
                expectedOwner,
                forceRestart: false,
                expectedOperationGeneration: operationGeneration,
                allowDuringBatch: false);
            if (!IsPlaybackOperationCurrent(operationGeneration))
            {
                return;
            }

            if (restarted && IsPlaybackLoaded(streamId))
            {
                AppendDesktopLog($"Restarted receiver for {stream.Id} after playback state '{reason}'.");
                _playbackRestartAfter.Remove(streamId);
                UpdateFooter($"Receiver restarted for {stream.Name}.");
            }
            else if (restarted &&
                _monitoringRequestedStreamIds.Contains(streamId) &&
                _playbackRestartAfter.ContainsKey(streamId))
            {
                AppendDesktopLog(
                    $"Receiver retry for {stream.Id} ended before input became ready; monitoring remains enabled for the next retry.");
                UpdateFooter($"Monitoring {stream.Name}; waiting to retry the receiver.");
            }
            else if (!PlaybackOwnerMatches(streamId, expectedOwner) ||
                !_playbackSnapshots.TryGetValue(streamId, out var latestSnapshot) ||
                !ReceiverNeedsRestart(streamId, latestSnapshot, AlertProfileFor(stream)))
            {
                _playbackRestartAfter.Remove(streamId);
            }
        }
        catch (Exception ex)
        {
            if (!IsPlaybackOperationCurrent(operationGeneration))
            {
                return;
            }

            var currentOwner = PlaybackOwnerFor(streamId);
            if (IsPlaybackOperationCurrent(operationGeneration) &&
                _playbackRestartAttempts.ContainsKey(streamId) &&
                (currentOwner is null || PlaybackOwnerMatches(streamId, expectedOwner)))
            {
                var restartAttempts = _playbackRestartAttempts.TryGetValue(streamId, out var attempts) ? attempts : 0;
                _playbackRestartAfter[streamId] = DateTimeOffset.UtcNow.Add(PlaybackRestartDelay(stream, restartAttempts));
            }

            AppendDesktopLog($"Receiver restart failed for {stream.Id}: {ex}");
            UpdateFooter($"Receiver restart failed for {stream.Name}: {ex.Message}");
        }
    }

    private bool IsAnalysisMissingOrEnded(string streamId)
    {
        if (!_videoMonitors.TryGetValue(streamId, out var monitor))
        {
            return true;
        }

        return monitor.State.Equals("waiting", StringComparison.OrdinalIgnoreCase) ||
            monitor.State.Equals("stopped", StringComparison.OrdinalIgnoreCase) ||
            monitor.State.Equals("ended", StringComparison.OrdinalIgnoreCase) ||
            monitor.State.Equals("error", StringComparison.OrdinalIgnoreCase);
    }

    private void RestartVideoMonitor(StreamConfig stream)
    {
        StopVideoMonitor(stream.Id);
        StartVideoMonitor(stream);
    }

    private void LogVideoAnalysisState(string streamId, AlertProfileConfig profile, PlaybackSnapshot snapshot, bool unavailable)
    {
        var hasMonitor = _videoMonitors.TryGetValue(streamId, out var monitor);
        if (!string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            !(hasMonitor && string.Equals(monitor!.Kind, "ffmpeg-playback", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (_lastVideoAnalysisLogAt.TryGetValue(streamId, out var lastLogAt) &&
            DateTimeOffset.UtcNow - lastLogAt < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastVideoAnalysisLogAt[streamId] = DateTimeOffset.UtcNow;
        if (TryGetVideoLuma(streamId, profile, out var luma))
        {
            var kind = _videoMonitors.TryGetValue(streamId, out var activeMonitor) ? activeMonitor.Kind : "none";
            AppendDesktopLog(
                $"Video analysis {streamId}: kind={kind}, state={snapshot.State}, luma={luma.Luma:0.000}, dark={luma.DarkRatio:0.000}, diff={luma.Difference:0.0000}, stable={luma.StableFrames}, unavailable={unavailable}");
            return;
        }

        var monitorState = hasMonitor ? $"{monitor!.Kind}/{monitor.State}" : "missing";
        var lastError = monitor is not null && !string.IsNullOrWhiteSpace(monitor.LastError)
            ? $", error={monitor.LastError}"
            : "";
        AppendDesktopLog($"Video analysis {streamId}: no snapshot, monitor={monitorState}, unavailable={unavailable}{lastError}");
    }

    private void ClearPlaybackAnalysis(string streamId)
    {
        _lastPlaybackTimeMs.Remove(streamId);
        _lastPlaybackProgressMarker.Remove(streamId);
        _lastPlaybackProgressAt.Remove(streamId);
        _lastVideoAnalysisLogAt.Remove(streamId);
        _lastDisplayedPictures.Remove(streamId);
        _lastVideoPresentedAt.Remove(streamId);
        ClearIssueSince(streamId, "video.no_signal");
        ClearIssueSince(streamId, "video.opening");
        ClearIssueSince(streamId, "video.analysis_restart");
        ClearIssueSince(streamId, "video.black");
        ClearIssueSince(streamId, "video.freeze");
        ClearIssueSince(streamId, "video.low_fps");
        ClearIssueSince(streamId, "video.decode_error");
        ClearIssueSince(streamId, "audio.channel_missing");
        ClearIssueSince(streamId, "audio.silence");
        ClearIssueSince(streamId, "audio.clipping");
        lock (_immediateVideoAlertEvaluationSync)
        {
            _pendingImmediateVideoAlertEvaluations.Remove(streamId);
            _pendingImmediateBlackFrameDetections.Remove(streamId);
        }
    }

    private static PlaybackSnapshot BuildSnapshot(VlcMediaPlayer player, string? overrideState)
    {
        MediaStats? stats = null;
        try
        {
            stats = player.Media?.Statistics;
        }
        catch
        {
            stats = null;
        }

        var displayedPictures = stats?.DisplayedPictures ?? 0;
        var state = overrideState ?? player.State.ToString().ToLowerInvariant();
        if (string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase) && displayedPictures <= 0)
        {
            state = "rendering";
        }

        return new PlaybackSnapshot(
            state,
            player.Fps,
            player.Time,
            player.Length,
            player.Mute,
            player.AudioTrackCount,
            player.AudioTrack,
            stats?.ReadBytes ?? 0,
            stats?.DemuxReadBytes ?? 0,
            stats?.DecodedVideo ?? 0,
            displayedPictures,
            stats?.LostPictures ?? 0,
            stats?.DecodedAudio ?? 0,
            stats?.PlayedAudioBuffers ?? 0,
            stats?.InputBitrate ?? 0,
            stats?.DemuxBitrate ?? 0,
            DateTimeOffset.UtcNow);
    }

    private static PlaybackSnapshot EmptyPlaybackSnapshot(string state)
    {
        return new PlaybackSnapshot(state, 0, -1, -1, true, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow);
    }

    private static AudioLevelSnapshot EmptyAudioLevelSnapshot()
    {
        return new AudioLevelSnapshot(-120, -120, 0, DateTimeOffset.MinValue, []);
    }

    private static long PlaybackProgressMarker(PlaybackSnapshot snapshot)
    {
        var marker = Math.Max(0, snapshot.TimeMs);
        marker = Math.Max(marker, Math.Max(0, snapshot.ReadBytes));
        marker = Math.Max(marker, Math.Max(0, snapshot.DemuxReadBytes));
        marker = Math.Max(marker, Math.Max(0, snapshot.DecodedVideo));
        marker = Math.Max(marker, Math.Max(0, snapshot.DisplayedPictures));
        marker = Math.Max(marker, Math.Max(0, snapshot.DecodedAudio));
        return Math.Max(marker, Math.Max(0, snapshot.PlayedAudioBuffers));
    }

    private static bool IsActivePlaybackState(VLCState state)
    {
        return state is VLCState.Opening or VLCState.Buffering or VLCState.Playing;
    }

    private static bool IsDecodeError(PlaybackSnapshot snapshot)
    {
        return string.Equals(snapshot.State, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNoSignal(PlaybackSnapshot snapshot)
    {
        return snapshot.State.ToLowerInvariant() switch
        {
            "waiting" or "stopped" or "ended" or "retrying" => true,
            _ => false
        };
    }

    private static bool IsMonitoringPlaybackState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "starting" or "opening" or "buffering" or "rendering" or "playing" or "retrying" => true,
            _ => false
        };
    }

    private static bool IsRecoverablePlaybackState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "waiting" or "stopped" or "ended" or "error" or "retrying" => true,
            _ => false
        };
    }

    private bool IsBlackVideo(string streamId, AlertProfileConfig profile)
    {
        return TryGetVideoLuma(streamId, profile, out var snapshot) &&
            DateTimeOffset.UtcNow - snapshot.UpdatedAt <= TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec)) &&
            snapshot.Luma <= Math.Clamp(profile.BlackThreshold, 0.0, 1.0) &&
            snapshot.DarkRatio >= Math.Clamp(profile.BlackPixelRatioThreshold, 0.0, 1.0);
    }

    private bool IsFreezeVideo(string streamId, AlertProfileConfig profile)
    {
        return TryGetVideoLuma(streamId, profile, out var snapshot) &&
            DateTimeOffset.UtcNow - snapshot.UpdatedAt <= TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec)) &&
            snapshot.StableFrames >= Math.Max(1, profile.MinFreezeFrames);
    }

    private bool TryGetVideoLuma(string streamId, AlertProfileConfig profile, out VideoLumaSnapshot snapshot)
    {
        snapshot = new VideoLumaSnapshot(0, 0, 1, 0, DateTimeOffset.MinValue);
        if (!_videoMonitors.TryGetValue(streamId, out var monitor))
        {
            return false;
        }

        monitor.DarkPixelThreshold = Math.Clamp(profile.BlackPixelThreshold, 0.0, 1.0);
        monitor.StabilityDifferenceThreshold = Math.Clamp(profile.FreezeDifferenceThreshold, 0.0, 1.0);
        if (monitor is IImmediateBlackFrameMonitor immediateBlackMonitor)
        {
            immediateBlackMonitor.BlackLumaThreshold = Math.Clamp(profile.BlackThreshold, 0.0, 1.0);
            immediateBlackMonitor.BlackPixelRatioThreshold = Math.Clamp(profile.BlackPixelRatioThreshold, 0.0, 1.0);
        }

        if (monitor.TryGetSnapshot(out snapshot) &&
            DateTimeOffset.UtcNow - snapshot.UpdatedAt <= TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec)))
        {
            return true;
        }

        return false;
    }

    private static bool IsAudioChannelMissing(PlaybackSnapshot snapshot)
    {
        return string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            snapshot.AudioTrackCount == 0;
    }

    private bool IsAudioSilent(string streamId, AlertProfileConfig profile)
    {
        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var session))
        {
            if (session.TryGetAudioSnapshot(out var ffmpegSnapshot))
            {
                return DateTimeOffset.UtcNow - ffmpegSnapshot.LastSamplesAt <= TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec)) &&
                    ffmpegSnapshot.LevelDb <= profile.SilenceThresholdDb;
            }

            return false;
        }

        return _audioMonitors.TryGetValue(streamId, out var monitor) &&
            monitor.TryGetSnapshot(out var snapshot) &&
            DateTimeOffset.UtcNow - snapshot.LastSamplesAt <= TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec)) &&
            snapshot.LevelDb <= profile.SilenceThresholdDb;
    }

    private bool IsAudioClipping(string streamId, AlertProfileConfig profile)
    {
        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var session))
        {
            return session.TryGetAudioSnapshot(out var ffmpegSnapshot) &&
                DateTimeOffset.UtcNow - ffmpegSnapshot.LastSamplesAt <= TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec)) &&
                ffmpegSnapshot.PeakDb >= ClippingThresholdDb(profile);
        }

        return _audioMonitors.TryGetValue(streamId, out var monitor) &&
            monitor.TryGetSnapshot(out var snapshot) &&
            DateTimeOffset.UtcNow - snapshot.LastSamplesAt <= TimeSpan.FromSeconds(Math.Max(2, profile.InputTimeoutSec)) &&
            snapshot.PeakDb >= ClippingThresholdDb(profile) &&
            snapshot.ClippingSamples >= profile.ClippingCountThreshold;
    }

    private static bool IsLowFps(PlaybackSnapshot snapshot, AlertProfileConfig profile)
    {
        return string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            snapshot.Fps > 0 &&
            snapshot.Fps < profile.LowFpsThreshold;
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseNextAlertOverlayWindows();
        CompositionTarget.Rendering -= AudioMeterCompositionTarget_Rendering;
        _playbackTimer.Stop();
        _audioMeterTimer.Stop();
        StopContinuousAlertSound();
        CloseAuxiliaryWindows();
        StopAllPlayback();
        _httpClient.Dispose();
        _libVlc?.Dispose();
        base.OnClosed(e);
        Application.Current?.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try
        {
            LocalConfigStore.FlushQueuedSave(_config);
        }
        catch (Exception ex)
        {
            AppendDesktopLog($"Final configuration save failed: {ex.Message}");
        }

        CloseAuxiliaryWindows();
        base.OnClosing(e);
    }

    private void CloseAuxiliaryWindows()
    {
        try
        {
            _settingsWindow.ForceClose();
        }
        catch (InvalidOperationException)
        {
            // Already closed during shutdown.
        }

        try
        {
            _alertHistoryWindow?.ForceClose();
            _alertHistoryWindow = null;
        }
        catch (InvalidOperationException)
        {
            // Already closed during shutdown.
        }
    }

    private static TextBlock MutedText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = BrushFromHex("#9ca3af"),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static Brush BrushFromHex(string hex)
    {
        lock (ColorResourceSync)
        {
            if (BrushCache.TryGetValue(hex, out var cached))
            {
                return cached;
            }

            var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            BrushCache[hex] = brush;
            return brush;
        }
    }

    private static Color ColorFromHex(string hex)
    {
        lock (ColorResourceSync)
        {
            if (ColorCache.TryGetValue(hex, out var cached))
            {
                return cached;
            }

            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            ColorCache[hex] = color;
            return color;
        }
    }

    internal static bool SetTextIfChanged(TextBlock textBlock, string text)
    {
        if (string.Equals(textBlock.Text, text, StringComparison.Ordinal))
        {
            return false;
        }

        textBlock.Text = text;
        return true;
    }

    internal static string MaskStreamUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var masked = raw;
        var schemeIndex = masked.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            var authorityStart = schemeIndex + 3;
            var authorityEnd = masked.IndexOf('/', authorityStart);
            if (authorityEnd < 0)
            {
                authorityEnd = masked.Length;
            }

            var atIndex = masked.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
            if (atIndex >= authorityStart)
            {
                var userInfo = masked[authorityStart..atIndex];
                var replacement = userInfo.Contains(':', StringComparison.Ordinal)
                    ? $"{userInfo.Split(':', 2)[0]}:****"
                    : "****";
                masked = masked[..authorityStart] + replacement + masked[atIndex..];
            }
        }

        return Regex.Replace(
            masked,
            @"(?i)([?&](?:access_token|auth|key|passphrase|password|secret|token)=)[^&]*",
            "$1<REDACTED>");
    }
}

internal sealed record PlaybackSnapshot(
    string State,
    float Fps,
    long TimeMs,
    long LengthMs,
    bool Muted,
    int AudioTrackCount,
    int AudioTrack,
    long ReadBytes,
    long DemuxReadBytes,
    int DecodedVideo,
    int DisplayedPictures,
    int LostPictures,
    int DecodedAudio,
    int PlayedAudioBuffers,
    float InputBitrate,
    float DemuxBitrate,
    DateTimeOffset UpdatedAt);

internal sealed record AudioLevelSnapshot(
    double LevelDb,
    double PeakDb,
    int ClippingSamples,
    DateTimeOffset LastSamplesAt,
    AudioChannelLevel[] Channels,
    double MediaTimeSeconds = double.NaN);

internal sealed record AudioChannelLevel(
    int Channel,
    double LevelDb,
    double PeakDb,
    int ClippingSamples);

internal sealed class AudioMeterView
{
    public AudioMeterView(
        int channel,
        Border track,
        Border fill,
        RectangleGeometry fillClip,
        Border peakMarker,
        TranslateTransform peakOffset)
    {
        Channel = channel;
        Track = track;
        Fill = fill;
        FillClip = fillClip;
        PeakMarker = peakMarker;
        PeakOffset = peakOffset;
    }

    public int Channel { get; }
    public Border Track { get; }
    public Border Fill { get; }
    public RectangleGeometry FillClip { get; }
    public Border PeakMarker { get; }
    public TranslateTransform PeakOffset { get; }
    public double TargetLevelRatio { get; set; }
    public double TargetPeakRatio { get; set; }
    public double DisplayedLevelRatio { get; set; }
    public double DisplayedPeakRatio { get; set; }
    public bool Clipping { get; set; }
    public bool DisplayedClipping { get; set; }
    public double LastLevelDb { get; set; }
    public double LastPeakDb { get; set; }
    public DateTimeOffset LastTooltipAt { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastAnimationAt { get; set; } = DateTimeOffset.MinValue;
}

internal sealed record VideoLumaSnapshot(
    double Luma,
    double DarkRatio,
    double Difference,
    int StableFrames,
    DateTimeOffset UpdatedAt);

internal sealed class AlertOverlayWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double actualWidth ? actualWidth : 0;
        var subtract = parameter is double parameterValue ? parameterValue : 0;
        return Math.Max(0, width - subtract);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal interface IVideoAnalysisMonitor : IDisposable
{
    string Kind { get; }
    string State { get; }
    string LastError { get; }
    double DarkPixelThreshold { get; set; }
    double StabilityDifferenceThreshold { get; set; }
    void Start();
    bool TryGetSnapshot(out VideoLumaSnapshot snapshot);
}

internal interface IImmediateBlackFrameMonitor
{
    double BlackLumaThreshold { get; set; }
    double BlackPixelRatioThreshold { get; set; }
}

internal sealed class FfmpegPlaybackSession : IVideoAnalysisMonitor, IImmediateBlackFrameMonitor
{
    private const int Width = 426;
    private const int Height = 240;
    private const int BytesPerPixel = 3;
    private const int Stride = Width * BytesPerPixel;
    private const int FrameBytes = Stride * Height;
    private const double TargetFps = 30;
    private const double FpsObservationWindowSeconds = 1.0;
    private const double SilenceFloorDb = -120;
    private const int TargetAnalysisSampleColumns = 54;
    private const int TargetAnalysisSampleRows = 30;
    private const int MaxAudioStatsChannels = 16;
    private const int MaxAudioTimelineSnapshots = 256;
    private const double AudioSyncLeadToleranceSeconds = 0.025;
    private const double AudioSyncMaxAgeSeconds = 0.150;
    private const int MaxVideoPtsBufferedOrdinals = 128;
    private const long MaxVideoPtsExtrapolationFrames = 60;
    private static readonly TimeSpan VideoPtsWaitTimeout = TimeSpan.FromMilliseconds(75);
    private static readonly RegexOptions AudioStatsRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan AudioStatsRegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex AudioMetadataFrameRegex = new(
        @"^frame:\s*\d+\s+pts:\s*-?\d+\s+pts_time:\s*(-?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex ChannelRmsMetadataRegex = new(
        @"lavfi\.astats\.(\d+)\.RMS_level=(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex ChannelPeakMetadataRegex = new(
        @"lavfi\.astats\.(\d+)\.Peak_level=(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex OverallRmsMetadataRegex = new(
        @"lavfi\.astats\.Overall\.RMS_level=(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex OverallPeakMetadataRegex = new(
        @"lavfi\.astats\.Overall\.Peak_level=(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex FallbackRmsLogRegex = new(
        @"RMS level dB:\s*(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex FallbackPeakLogRegex = new(
        @"Peak level dB:\s*(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex HardwarePixelFormatRegex = new(
        @"pix_fmt:\s*(d3d11|dxva2_vld|cuda|qsv|vaapi|videotoolbox(?:_vld)?)\b",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex ShowInfoVideoPtsRegex = new(
        @"showinfo.*?\bn:\s*(\d+).*?\bpts_time:\s*(-?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly ConcurrentDictionary<string, Lazy<bool>> VideoPtsStatsSupport =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _ffmpegPath;
    private readonly string _inputUrl;
    private readonly string _protocol;
    private readonly List<string> _inputCandidates;
    private readonly Dispatcher _dispatcher;
    private readonly Action _blackFrameDetected;
    private readonly Action _firstFramePresented;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly int _stride;
    private readonly int _frameBytes;
    private readonly int _analysisSampleStrideX;
    private readonly int _analysisSampleStrideY;
    private readonly int _analysisSampleColumns;
    private readonly int _analysisSampleRows;
    private readonly string _requestedHardwareDecodeMode;
    private readonly bool _observeHardwareBackend;
    private readonly object _sync = new();
    private readonly object _processLifecycleSync = new();
    private readonly object _audioParserSync = new();
    private readonly object _bitmapFrameSync = new();
    private readonly Queue<AudioLevelSnapshot> _audioTimeline = new();
    private readonly double[] _previousSampleLuma;
    private readonly double[] _currentSampleLuma;
    private byte[] _bitmapWriteFrameBuffer;
    private byte[] _bitmapPendingFrameBuffer;
    private long _bitmapWriteFrameReadTicks;
    private long _bitmapPendingFrameReadTicks;
    private double _bitmapWriteFramePtsSeconds = double.NaN;
    private double _bitmapPendingFramePtsSeconds = double.NaN;
    private Process? _process;
    private FfmpegOutputPipeContext? _outputPipes;
    private CancellationTokenSource? _cts;
    private VideoLumaSnapshot? _snapshot;
    private AudioLevelSnapshot? _audioSnapshot;
    private AudioLevelSnapshot? _presentedAudioSnapshot;
    private long _framesRead;
    private long _bytesRead;
    private int _stableFrames;
    private bool _hasPreviousFrame;
    private bool _blackFrameAlertLatched;
    private string _state = "stopped";
    private string _lastError = "";
    private int _candidateIndex;
    private bool _bitmapWriteScheduled;
    private bool _bitmapPendingFrameReady;
    private long _bitmapFramesQueued;
    private long _bitmapFramesDropped;
    private long _bitmapFramesCoalesced;
    private long _bitmapFramesPresented;
    private long _lastFrameReadTicks;
    private long _lastFramePresentedTicks;
    private long _lastPresentedFrameReadTicks;
    private long _readFpsWindowStartTimestamp;
    private long _readFpsWindowStartFrames;
    private double _observedReadFps;
    private long _presentedFpsWindowStartTimestamp;
    private long _presentedFpsWindowStartFrames;
    private double _observedPresentedFps;
    private bool _disposed;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private double _lastParsedRmsDb = SilenceFloorDb;
    private double _lastParsedPeakDb = SilenceFloorDb;
    private double _activeAudioPtsSeconds = double.NaN;
    private double _lastAudioTimelinePtsSeconds = double.NaN;
    private double _lastPresentedVideoPtsSeconds = double.NaN;
    private int _parsedAudioChannelMask;
    private int _parsedAudioChannelRmsMask;
    private int _parsedAudioChannelPeakMask;
    private string _decodeStatusText;
    private string _detectedHardwareBackend = "";
    private bool _decodeBackendConfirmed;
    private int _audioMetadataGeneration;
    private int _startScheduled;
    private readonly double[] _lastParsedChannelRmsDb = new double[MaxAudioStatsChannels];
    private readonly double[] _lastParsedChannelPeakDb = new double[MaxAudioStatsChannels];

    public FfmpegPlaybackSession(
        string ffmpegPath,
        string inputUrl,
        string protocol,
        Dispatcher dispatcher,
        double darkPixelThreshold,
        double stabilityDifferenceThreshold,
        double blackLumaThreshold,
        double blackPixelRatioThreshold,
        Action blackFrameDetected,
        Action? firstFramePresented = null)
        : this(
            ffmpegPath,
            inputUrl,
            protocol,
            dispatcher,
            darkPixelThreshold,
            stabilityDifferenceThreshold,
            blackLumaThreshold,
            blackPixelRatioThreshold,
            blackFrameDetected,
            firstFramePresented,
            Width,
            Height,
            observeHardwareBackend: false)
    {
    }

    public FfmpegPlaybackSession(
        string ffmpegPath,
        string inputUrl,
        string protocol,
        Dispatcher dispatcher,
        double darkPixelThreshold,
        double stabilityDifferenceThreshold,
        double blackLumaThreshold,
        double blackPixelRatioThreshold,
        Action blackFrameDetected,
        Action? firstFramePresented,
        int outputWidth,
        int outputHeight,
        bool observeHardwareBackend)
    {
        _ffmpegPath = ffmpegPath;
        _inputUrl = inputUrl;
        _protocol = protocol;
        _inputCandidates = BuildInputCandidates(inputUrl, protocol);
        _dispatcher = dispatcher;
        _blackFrameDetected = blackFrameDetected;
        _firstFramePresented = firstFramePresented ?? (() => { });
        _outputWidth = NormalizeOutputDimension(outputWidth, Width);
        _outputHeight = NormalizeOutputDimension(outputHeight, Height);
        _stride = _outputWidth * BytesPerPixel;
        _frameBytes = _stride * _outputHeight;
        _analysisSampleStrideX = Math.Max(1, (int)Math.Ceiling(_outputWidth / (double)TargetAnalysisSampleColumns));
        _analysisSampleStrideY = Math.Max(1, (int)Math.Ceiling(_outputHeight / (double)TargetAnalysisSampleRows));
        _analysisSampleColumns = (_outputWidth + _analysisSampleStrideX - 1) / _analysisSampleStrideX;
        _analysisSampleRows = (_outputHeight + _analysisSampleStrideY - 1) / _analysisSampleStrideY;
        _previousSampleLuma = new double[_analysisSampleColumns * _analysisSampleRows];
        _currentSampleLuma = new double[_analysisSampleColumns * _analysisSampleRows];
        _bitmapWriteFrameBuffer = new byte[_frameBytes];
        _bitmapPendingFrameBuffer = new byte[_frameBytes];
        _requestedHardwareDecodeMode = MainWindow.FfmpegHardwareDecodeMode();
        _observeHardwareBackend = observeHardwareBackend;
        _decodeStatusText = InitialDecodeStatusText(_requestedHardwareDecodeMode);
        DarkPixelThreshold = Math.Clamp(darkPixelThreshold, 0.0, 1.0);
        StabilityDifferenceThreshold = Math.Clamp(stabilityDifferenceThreshold, 0.0, 1.0);
        BlackLumaThreshold = Math.Clamp(blackLumaThreshold, 0.0, 1.0);
        BlackPixelRatioThreshold = Math.Clamp(blackPixelRatioThreshold, 0.0, 1.0);
        Bitmap = new WriteableBitmap(_outputWidth, _outputHeight, 96, 96, PixelFormats.Bgr24, null);
    }

    public WriteableBitmap Bitmap { get; }

    public int OutputWidth => _outputWidth;

    public int OutputHeight => _outputHeight;

    public string OutputDescription => $"WPF {_outputWidth}x{_outputHeight}";

    public string DecodeStatusText
    {
        get
        {
            lock (_sync)
            {
                return _decodeStatusText;
            }
        }
    }

    public bool DecodeBackendConfirmed
    {
        get
        {
            lock (_sync)
            {
                return _decodeBackendConfirmed;
            }
        }
    }

    private static int NormalizeOutputDimension(int value, int fallback)
    {
        var normalized = Math.Clamp(value > 0 ? value : fallback, 64, 3840);
        return normalized % 2 == 0 ? normalized : normalized - 1;
    }

    private static string BuildVideoFilter(int outputWidth, int outputHeight)
    {
        var width = NormalizeOutputDimension(outputWidth, Width);
        var height = NormalizeOutputDimension(outputHeight, Height);
        return string.Join(",",
            $"fps={TargetFps.ToString("0.###", CultureInfo.InvariantCulture)}",
            $"scale=w={width}:h={height}:force_original_aspect_ratio=decrease:flags=bicubic",
            $"pad=width={width}:height={height}:x=(ow-iw)/2:y=(oh-ih)/2:color=black",
            "format=bgr24");
    }

    private static string BuildFallbackVideoFilter()
    {
        return $"fps={TargetFps.ToString("0.###", CultureInfo.InvariantCulture)},scale={Width}:{Height}:flags=fast_bilinear,format=bgr24";
    }

    private static string InitialDecodeStatusText(string requestedMode)
    {
        if (string.Equals(requestedMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        return string.Equals(requestedMode, "auto", StringComparison.OrdinalIgnoreCase)
            ? "AUTO · DETECTING"
            : $"{requestedMode.ToUpperInvariant()} · REQUESTED";
    }

    private static string? ConfirmedHardwareBackendFromLogLine(string line)
    {
        var pixelFormatMatch = HardwarePixelFormatRegex.Match(line);
        return pixelFormatMatch.Success
            ? NormalizeHardwareBackendName(pixelFormatMatch.Groups[1].Value)
            : null;
    }

    private static string NormalizeHardwareBackendName(string backend)
    {
        return backend.ToLowerInvariant() switch
        {
            "d3d11" or "d3d11va" => "D3D11VA",
            "dxva2" or "dxva2_vld" => "DXVA2",
            "videotoolbox" or "videotoolbox_vld" => "VIDEOTOOLBOX",
            _ => backend.ToUpperInvariant()
        };
    }

    private bool TryObserveHardwareBackendLogLine(string line)
    {
        if (!_observeHardwareBackend)
        {
            return false;
        }

        var backend = ConfirmedHardwareBackendFromLogLine(line);
        if (string.IsNullOrWhiteSpace(backend))
        {
            return false;
        }

        lock (_sync)
        {
            _detectedHardwareBackend = backend;
            if (Interlocked.Read(ref _bitmapFramesPresented) > 0)
            {
                _decodeBackendConfirmed = true;
                _decodeStatusText = $"GPU · {backend}";
            }
        }

        return true;
    }

    private void FinalizeDecodeBackendObservation()
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_detectedHardwareBackend))
            {
                _decodeBackendConfirmed = true;
                _decodeStatusText = $"GPU · {_detectedHardwareBackend}";
                return;
            }

            _decodeBackendConfirmed = false;
            _decodeStatusText = string.Equals(_requestedHardwareDecodeMode, "auto", StringComparison.OrdinalIgnoreCase)
                ? "AUTO · UNKNOWN"
                : InitialDecodeStatusText(_requestedHardwareDecodeMode);
        }
    }

    public bool HasFrame => _framesRead > 0;

    public bool HasPresentedFrame => Interlocked.Read(ref _bitmapFramesPresented) > 0;

    public bool IsProcessRunning
    {
        get
        {
            lock (_processLifecycleSync)
            {
                if (_disposed || _process is null)
                {
                    return false;
                }

                try
                {
                    return !_process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }

    public long BitmapFramesQueued => Interlocked.Read(ref _bitmapFramesQueued);

    public long BitmapFramesDropped => Interlocked.Read(ref _bitmapFramesDropped);

    public long BitmapFramesCoalesced => Interlocked.Read(ref _bitmapFramesCoalesced);

    public long BitmapFramesPresented => Interlocked.Read(ref _bitmapFramesPresented);

    public DateTimeOffset LastFrameReadAt => UtcTicksToDateTimeOffset(Interlocked.Read(ref _lastFrameReadTicks));

    public DateTimeOffset LastFramePresentedAt => UtcTicksToDateTimeOffset(Interlocked.Read(ref _lastFramePresentedTicks));

    public double LastPresentedVideoPtsSeconds
    {
        get
        {
            lock (_sync)
            {
                return _lastPresentedVideoPtsSeconds;
            }
        }
    }

    public double LastPresentedAudioPtsSeconds
    {
        get
        {
            lock (_sync)
            {
                return _presentedAudioSnapshot?.MediaTimeSeconds ?? double.NaN;
            }
        }
    }

    public double LastAudioVideoSyncOffsetMs
    {
        get
        {
            lock (_sync)
            {
                var audioPts = _presentedAudioSnapshot?.MediaTimeSeconds ?? double.NaN;
                return double.IsFinite(_lastPresentedVideoPtsSeconds) && double.IsFinite(audioPts)
                    ? (_lastPresentedVideoPtsSeconds - audioPts) * 1000
                    : double.NaN;
            }
        }
    }

    public double LastRenderDelayMs
    {
        get
        {
            var readTicks = Interlocked.Read(ref _lastPresentedFrameReadTicks);
            var presentedTicks = Interlocked.Read(ref _lastFramePresentedTicks);
            return readTicks > 0 && presentedTicks >= readTicks
                ? TimeSpan.FromTicks(presentedTicks - readTicks).TotalMilliseconds
                : -1;
        }
    }

    public string Kind => "ffmpeg-playback";

    public double ObservedReadFps
    {
        get
        {
            lock (_sync)
            {
                return _observedReadFps;
            }
        }
    }

    public double ObservedPresentedFps
    {
        get
        {
            lock (_sync)
            {
                return _observedPresentedFps;
            }
        }
    }

    public string State => _state;

    public string LastError => _lastError;

    public double DarkPixelThreshold { get; set; }

    public double StabilityDifferenceThreshold { get; set; }

    public double BlackLumaThreshold { get; set; }

    public double BlackPixelRatioThreshold { get; set; }

    public void Start()
    {
        CancellationToken token;
        lock (_processLifecycleSync)
        {
            if (_disposed || _startScheduled != 0)
            {
                return;
            }

            _startScheduled = 1;
            _cts = new CancellationTokenSource();
            _state = "opening";
            token = _cts.Token;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var process = StartProcess(_inputCandidates[_candidateIndex], token, out var outputPipes);
                var terminateStartup = false;
                lock (_processLifecycleSync)
                {
                    if (_disposed || token.IsCancellationRequested)
                    {
                        terminateStartup = true;
                    }
                    else
                    {
                        _process = process;
                        _outputPipes = outputPipes;
                        var audioMetadataGeneration = Interlocked.Increment(ref _audioMetadataGeneration);
                        StartProcessReaders(process, outputPipes, token, audioMetadataGeneration);
                        _ = System.Threading.Tasks.Task.Run(() => ReadFramesAsync(process, outputPipes, token));
                    }
                }

                if (terminateStartup)
                {
                    TerminateProcessAndPipes(process, outputPipes);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                _state = "stopped";
            }
            catch (Exception ex)
            {
                _lastError = MainWindow.MaskStreamUrl(ex.Message);
                _state = "error";
            }
        });
    }

    private static void TerminateProcessAndPipes(Process process, FfmpegOutputPipeContext outputPipes)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort termination for shutdown and failed startup.
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // The process may already have been disposed by a concurrent shutdown.
            }

            outputPipes.Dispose();
        }
    }

    private Process StartProcess(string inputUrl, CancellationToken token, out FfmpegOutputPipeContext outputPipes)
    {
        var videoPtsPipeEnabled = SupportsVideoPtsStats(_ffmpegPath);
        outputPipes = new FfmpegOutputPipeContext(
            token,
            videoPtsEnabled: true,
            videoPtsPipeEnabled: videoPtsPipeEnabled);
        var ffmpegInputUrl = FfmpegInputUrl(inputUrl);
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-y");
        AddCommonFfmpegInputArguments(startInfo.ArgumentList, _protocol, ffmpegInputUrl);
        if (_observeHardwareBackend || !videoPtsPipeEnabled)
        {
            var logLevelIndex = startInfo.ArgumentList.IndexOf("-loglevel");
            if (logLevelIndex >= 0 && logLevelIndex + 1 < startInfo.ArgumentList.Count)
            {
                startInfo.ArgumentList[logLevelIndex + 1] = _observeHardwareBackend ? "verbose" : "info";
            }
        }
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(ffmpegInputUrl);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-vf");
        var videoFilter = _observeHardwareBackend
            ? BuildVideoFilter(_outputWidth, _outputHeight)
            : BuildFallbackVideoFilter();
        startInfo.ArgumentList.Add(videoPtsPipeEnabled ? videoFilter : $"{videoFilter},showinfo");
        if (videoPtsPipeEnabled)
        {
            startInfo.ArgumentList.Add("-fps_mode:v:0");
            startInfo.ArgumentList.Add("passthrough");
            startInfo.ArgumentList.Add("-stats_enc_pre:v:0");
            startInfo.ArgumentList.Add(outputPipes.VideoPtsPath);
            startInfo.ArgumentList.Add("-stats_enc_pre_fmt:v:0");
            startInfo.ArgumentList.Add("{n} {t}");
        }
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add(outputPipes.VideoPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0?");
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add(BuildAudioStatsFilter());
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");

        try
        {
            token.ThrowIfCancellationRequested();
            return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg playback.");
        }
        catch
        {
            outputPipes.Dispose();
            throw;
        }
    }

    private static string BuildAudioStatsFilter()
    {
        return "astats=metadata=1:reset=1:measure_perchannel=RMS_level+Peak_level:measure_overall=RMS_level+Peak_level," +
            "ametadata=print:file='pipe\\:1':direct=1";
    }

    private static bool SupportsVideoPtsStats(string ffmpegPath)
    {
        return VideoPtsStatsSupport.GetOrAdd(
            ffmpegPath,
            path => new Lazy<bool>(
                () => ProbeVideoPtsStatsSupport(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal static void WarmUpCapabilities(string ffmpegPath)
    {
        _ = SupportsVideoPtsStats(ffmpegPath);
    }

    private static bool ProbeVideoPtsStatsSupport(string ffmpegPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-h");
            process.StartInfo.ArgumentList.Add("full");
            if (!process.Start())
            {
                return false;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return false;
            }

            var helpText = standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
            return helpText.Contains("-stats_enc_pre", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void StartProcessReaders(
        Process process,
        FfmpegOutputPipeContext outputPipes,
        CancellationToken token,
        int audioMetadataGeneration)
    {
        _ = System.Threading.Tasks.Task.Run(() => DrainErrorsAsync(process, outputPipes, token));
        _ = System.Threading.Tasks.Task.Run(() => DrainAudioMetadataAsync(process, token, audioMetadataGeneration));
        if (outputPipes.VideoPtsPipeEnabled)
        {
            _ = System.Threading.Tasks.Task.Run(() => ReadVideoPtsAsync(process, outputPipes, token));
        }
    }

    private static string FfmpegInputUrl(string inputUrl)
    {
        return Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : inputUrl;
    }

    private static List<string> BuildInputCandidates(string inputUrl, string protocol)
    {
        if (!inputUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ||
            TryGetInputQueryParameter(inputUrl, "mode", out _))
        {
            return [inputUrl];
        }

        var explicitMode = protocol.ToLowerInvariant() switch
        {
            "srt-listener" => "listener",
            "srt-caller" => "caller",
            "srt-rendezvous" => "rendezvous",
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(explicitMode))
        {
            return [inputUrl];
        }

        var separator = inputUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return [$"{inputUrl}{separator}mode={explicitMode}"];
    }

    internal static string EffectiveSrtMode(string inputUrl, string protocol)
    {
        if (TryGetInputQueryParameter(inputUrl, "mode", out var queryMode))
        {
            return queryMode.Trim().ToLowerInvariant();
        }

        return (protocol ?? "").Trim().ToLowerInvariant() switch
        {
            "srt-listener" => "listener",
            "srt-caller" => "caller",
            "srt-rendezvous" => "rendezvous",
            "srt" => "caller",
            _ when inputUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) => "caller",
            _ => ""
        };
    }

    private static bool TryGetInputQueryParameter(string inputUrl, string parameterName, out string value)
    {
        value = "";
        var queryStart = inputUrl.IndexOf('?');
        if (queryStart < 0 || queryStart + 1 >= inputUrl.Length)
        {
            return false;
        }

        var fragmentStart = inputUrl.IndexOf('#', queryStart + 1);
        var query = fragmentStart >= 0
            ? inputUrl[(queryStart + 1)..fragmentStart]
            : inputUrl[(queryStart + 1)..];
        foreach (var item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            var encodedName = separator >= 0 ? item[..separator] : item;
            if (!string.Equals(Uri.UnescapeDataString(encodedName), parameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = separator >= 0 && separator + 1 < item.Length
                ? Uri.UnescapeDataString(item[(separator + 1)..])
                : "";
            return true;
        }

        return false;
    }

    private static DateTimeOffset UtcTicksToDateTimeOffset(long ticks)
    {
        return ticks > 0
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : DateTimeOffset.MinValue;
    }

    public PlaybackSnapshot CurrentPlaybackSnapshot()
    {
        lock (_sync)
        {
            var state = _state;
            var observedFps = _observedPresentedFps > 0
                ? _observedPresentedFps
                : _observedReadFps;
            var framesPresented = Interlocked.Read(ref _bitmapFramesPresented);
            if (string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase) && framesPresented == 0)
            {
                state = "rendering";
            }

            return new PlaybackSnapshot(
                state,
                string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase)
                    ? (float)(observedFps > 0 ? observedFps : TargetFps)
                    : 0,
                _framesRead > 0 ? (long)(_framesRead * 1000 / TargetFps) : 0,
                -1,
                true,
                -1,
                -1,
                _bytesRead,
                _bytesRead,
                (int)Math.Min(int.MaxValue, _framesRead),
                (int)Math.Min(int.MaxValue, framesPresented),
                0,
                0,
                0,
                0,
                0,
                DateTimeOffset.UtcNow);
        }
    }

    public bool TryGetSnapshot(out VideoLumaSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_snapshot is null)
            {
                snapshot = new VideoLumaSnapshot(0, 0, 1, 0, DateTimeOffset.MinValue);
                return false;
            }

            snapshot = _snapshot;
            return true;
        }
    }

    public bool TryGetAudioSnapshot(out AudioLevelSnapshot snapshot)
    {
        lock (_sync)
        {
            SelectPresentedAudioSnapshotLocked();
            if (_presentedAudioSnapshot is null)
            {
                snapshot = new AudioLevelSnapshot(SilenceFloorDb, SilenceFloorDb, 0, DateTimeOffset.MinValue, []);
                return false;
            }

            snapshot = _presentedAudioSnapshot;
            return true;
        }
    }

    private void SelectPresentedAudioSnapshotLocked()
    {
        if (!double.IsFinite(_lastPresentedVideoPtsSeconds))
        {
            return;
        }

        var latestAllowedPts = _lastPresentedVideoPtsSeconds + AudioSyncLeadToleranceSeconds;
        while (_audioTimeline.Count > 0 &&
               _audioTimeline.Peek().MediaTimeSeconds <= latestAllowedPts)
        {
            _presentedAudioSnapshot = _audioTimeline.Dequeue();
        }

        if (_presentedAudioSnapshot is not null &&
            double.IsFinite(_presentedAudioSnapshot.MediaTimeSeconds) &&
            _lastPresentedVideoPtsSeconds - _presentedAudioSnapshot.MediaTimeSeconds > AudioSyncMaxAgeSeconds)
        {
            _presentedAudioSnapshot = null;
        }
    }

    public bool ShouldTreatMissingAudioAsSilence(AlertProfileConfig profile)
    {
        lock (_sync)
        {
            return false;
        }
    }

    private static void AddCommonFfmpegInputArguments(Collection<string> arguments, string protocol, string inputUrl)
    {
        arguments.Add("-hide_banner");
        arguments.Add("-nostats");
        arguments.Add("-loglevel");
        arguments.Add("error");
        MainWindow.AddFfmpegHardwareDecodeArguments(arguments);

        arguments.Add("-fflags");
        arguments.Add("nobuffer");
        arguments.Add("-probesize");
        arguments.Add("32768");
        arguments.Add("-analyzeduration");
        arguments.Add("0");
        arguments.Add("-flags");
        arguments.Add("low_delay");
        arguments.Add("-rw_timeout");
        arguments.Add("3000000");
        arguments.Add("-threads");
        arguments.Add("1");
        arguments.Add("-filter_threads");
        arguments.Add("1");
        if (string.Equals(protocol, "rtsp", StringComparison.OrdinalIgnoreCase) ||
            inputUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-rtsp_transport");
            arguments.Add("tcp");
        }
    }

    private async System.Threading.Tasks.Task DrainErrorsAsync(
        Process process,
        FfmpegOutputPipeContext outputPipes,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (!outputPipes.VideoPtsPipeEnabled &&
                        TryParseShowInfoVideoPtsLine(line, out var ordinal, out var ptsSeconds))
                    {
                        StoreVideoPts(outputPipes, ordinal, ptsSeconds);
                        continue;
                    }

                    if (!TryObserveHardwareBackendLogLine(line))
                    {
                        if (!_observeHardwareBackend && !IsBenignFfmpegLogLine(line))
                        {
                            _lastError = MainWindow.MaskStreamUrl(line);
                        }
                        else if (_observeHardwareBackend && IsFfmpegFailureLogLine(line))
                        {
                            _lastError = MainWindow.MaskStreamUrl(line);
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private async System.Threading.Tasks.Task DrainAudioMetadataAsync(
        Process process,
        CancellationToken token,
        int audioMetadataGeneration)
    {
        try
        {
            while (!token.IsCancellationRequested &&
                   audioMetadataGeneration == Volatile.Read(ref _audioMetadataGeneration))
            {
                var line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null)
                {
                    return;
                }

                if (audioMetadataGeneration != Volatile.Read(ref _audioMetadataGeneration))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    TryParseAudioStatsLineForGeneration(line, audioMetadataGeneration);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Audio metadata is best effort; playback remains authoritative.
        }
    }

    private async System.Threading.Tasks.Task ReadVideoPtsAsync(
        Process process,
        FfmpegOutputPipeContext outputPipes,
        CancellationToken token)
    {
        try
        {
            if (!await WaitForPipeConnectionAsync(outputPipes.VideoPtsConnected, process, token))
            {
                return;
            }

            using var reader = new StreamReader(
                outputPipes.VideoPtsStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    return;
                }

                if (!TryParseVideoPtsLine(line, out var ordinal, out var ptsSeconds))
                {
                    continue;
                }

                StoreVideoPts(outputPipes, ordinal, ptsSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Missing PTS falls back to frame-arrival synchronization.
        }
    }

    private static bool TryParseVideoPtsLine(string line, out long ordinal, out double ptsSeconds)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length >= 2 &&
            long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) &&
            double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ptsSeconds) &&
            double.IsFinite(ptsSeconds))
        {
            return true;
        }

        ordinal = -1;
        ptsSeconds = double.NaN;
        return false;
    }

    private static bool TryParseShowInfoVideoPtsLine(string line, out long ordinal, out double ptsSeconds)
    {
        var match = ShowInfoVideoPtsRegex.Match(line);
        if (match.Success &&
            long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) &&
            double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ptsSeconds) &&
            double.IsFinite(ptsSeconds))
        {
            return true;
        }

        ordinal = -1;
        ptsSeconds = double.NaN;
        return false;
    }

    private static async System.Threading.Tasks.Task<bool> WaitForPipeConnectionAsync(
        System.Threading.Tasks.Task connectionTask,
        Process process,
        CancellationToken token)
    {
        if (connectionTask.IsCompletedSuccessfully)
        {
            return true;
        }

        var processExit = process.WaitForExitAsync(token);
        var completed = await System.Threading.Tasks.Task.WhenAny(connectionTask, processExit);
        if (completed == connectionTask || connectionTask.IsCompletedSuccessfully)
        {
            await connectionTask;
            return true;
        }

        return false;
    }

    private bool TryParseAudioStatsLine(string line)
    {
        lock (_audioParserSync)
        {
            return TryParseAudioStatsLineCore(line);
        }
    }

    private bool TryParseAudioStatsLineForGeneration(string line, int audioMetadataGeneration)
    {
        lock (_audioParserSync)
        {
            return audioMetadataGeneration == Volatile.Read(ref _audioMetadataGeneration) &&
                TryParseAudioStatsLineCore(line);
        }
    }

    private bool TryParseAudioStatsLineCore(string line)
    {
        var frameMetadata = AudioMetadataFrameRegex.Match(line);
        if (frameMetadata.Success)
        {
            _activeAudioPtsSeconds = double.TryParse(
                frameMetadata.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var ptsSeconds)
                ? ptsSeconds
                : double.NaN;
            _parsedAudioChannelMask = 0;
            _parsedAudioChannelRmsMask = 0;
            _parsedAudioChannelPeakMask = 0;
            Array.Fill(_lastParsedChannelRmsDb, SilenceFloorDb);
            Array.Fill(_lastParsedChannelPeakDb, SilenceFloorDb);
            return true;
        }

        var channelRmsMetadata = ChannelRmsMetadataRegex.Match(line);
        if (channelRmsMetadata.Success)
        {
            var channel = Math.Clamp(int.Parse(channelRmsMetadata.Groups[1].Value, CultureInfo.InvariantCulture), 1, MaxAudioStatsChannels);
            var channelBit = RememberParsedAudioChannel(channel);
            _parsedAudioChannelRmsMask |= channelBit;
            _lastParsedChannelRmsDb[channel - 1] = ParseFfmpegDb(channelRmsMetadata.Groups[2].Value);
            return true;
        }

        var channelPeakMetadata = ChannelPeakMetadataRegex.Match(line);
        if (channelPeakMetadata.Success)
        {
            var channel = Math.Clamp(int.Parse(channelPeakMetadata.Groups[1].Value, CultureInfo.InvariantCulture), 1, MaxAudioStatsChannels);
            var channelBit = RememberParsedAudioChannel(channel);
            _parsedAudioChannelPeakMask |= channelBit;
            _lastParsedChannelPeakDb[channel - 1] = ParseFfmpegDb(channelPeakMetadata.Groups[2].Value);
            return true;
        }

        var rmsMetadata = OverallRmsMetadataRegex.Match(line);
        if (rmsMetadata.Success)
        {
            _lastParsedRmsDb = ParseFfmpegDb(rmsMetadata.Groups[1].Value);
            UpdateAudioSnapshot(_activeAudioPtsSeconds);
            return true;
        }

        var peakMetadata = OverallPeakMetadataRegex.Match(line);
        if (peakMetadata.Success)
        {
            _lastParsedPeakDb = ParseFfmpegDb(peakMetadata.Groups[1].Value);
            return true;
        }

        var rms = FallbackRmsLogRegex.Match(line);
        if (rms.Success)
        {
            _lastParsedRmsDb = ParseFfmpegDb(rms.Groups[1].Value);
            UpdateAudioSnapshot(double.NaN);
            return true;
        }

        var peak = FallbackPeakLogRegex.Match(line);
        if (peak.Success)
        {
            _lastParsedPeakDb = ParseFfmpegDb(peak.Groups[1].Value);
            UpdateAudioSnapshot(double.NaN);
            return true;
        }

        return false;
    }

    private int RememberParsedAudioChannel(int channel)
    {
        var channelBit = 1 << (channel - 1);
        _parsedAudioChannelMask |= channelBit;
        return channelBit;
    }

    private static bool IsBenignFfmpegLogLine(string line)
    {
        return line.StartsWith("Input #", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Output #", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Stream mapping:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Press [q]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Duration:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Stream #", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Metadata:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("lavfi.astats.", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("encoder", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("frame=", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("size=", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("bitrate=", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("showinfo", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[Parsed_astats_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFfmpegFailureLogLine(string line)
    {
        if (line.Contains("0 decode errors", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error resilience", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static double ParseFfmpegDb(string value)
    {
        if (value.Contains("inf", StringComparison.OrdinalIgnoreCase))
        {
            return SilenceFloorDb;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, SilenceFloorDb, 12)
            : SilenceFloorDb;
    }

    private void UpdateAudioSnapshot(double mediaTimeSeconds)
    {
        lock (_sync)
        {
            var channelMask = _parsedAudioChannelMask;
            var channelRmsMask = _parsedAudioChannelRmsMask;
            var channelPeakMask = _parsedAudioChannelPeakMask;
            var channelCount = 0;
            for (var channel = 1; channel <= MaxAudioStatsChannels; channel++)
            {
                if ((channelMask & (1 << (channel - 1))) != 0)
                {
                    channelCount++;
                }
            }

            var channels = channelCount > 0 ? new AudioChannelLevel[channelCount] : Array.Empty<AudioChannelLevel>();
            var clippingSamples = 0;
            var channelIndex = 0;
            for (var channel = 1; channel <= MaxAudioStatsChannels; channel++)
            {
                if ((channelMask & (1 << (channel - 1))) == 0)
                {
                    continue;
                }

                var channelBit = 1 << (channel - 1);
                var channelIndex0 = channel - 1;
                var levelDb = (channelRmsMask & channelBit) != 0 ? _lastParsedChannelRmsDb[channelIndex0] : _lastParsedRmsDb;
                var peakDb = (channelPeakMask & channelBit) != 0 ? _lastParsedChannelPeakDb[channelIndex0] : _lastParsedPeakDb;
                var channelClippingSamples = peakDb >= -1 ? 1 : 0;
                channels[channelIndex++] = new AudioChannelLevel(channel, levelDb, peakDb, channelClippingSamples);
                clippingSamples += channelClippingSamples;
            }

            if (channelCount == 0 && _lastParsedPeakDb >= -1)
            {
                clippingSamples = 1;
            }

            var snapshot = new AudioLevelSnapshot(
                _lastParsedRmsDb,
                _lastParsedPeakDb,
                clippingSamples,
                DateTimeOffset.UtcNow,
                channels,
                mediaTimeSeconds);
            _audioSnapshot = snapshot;
            if (double.IsFinite(mediaTimeSeconds))
            {
                if (double.IsFinite(_lastAudioTimelinePtsSeconds) &&
                    mediaTimeSeconds + 0.5 < _lastAudioTimelinePtsSeconds)
                {
                    _audioTimeline.Clear();
                    _presentedAudioSnapshot = null;
                }

                _audioTimeline.Enqueue(snapshot);
                _lastAudioTimelinePtsSeconds = mediaTimeSeconds;
                while (_audioTimeline.Count > MaxAudioTimelineSnapshots)
                {
                    _audioTimeline.Dequeue();
                }
            }
        }
    }

    private async System.Threading.Tasks.Task ReadFramesAsync(
        Process process,
        FfmpegOutputPipeContext outputPipes,
        CancellationToken token)
    {
        var frame = new byte[_frameBytes];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var framesAtCandidateStart = Interlocked.Read(ref _framesRead);
                if (!await WaitForPipeConnectionAsync(outputPipes.VideoConnected, process, token))
                {
                    if (TryStartNextCandidate(ref process, ref outputPipes, token))
                    {
                        continue;
                    }

                    _state = "ended";
                    return;
                }

                long frameOrdinal = 0;
                var retryCandidate = false;
                while (!token.IsCancellationRequested)
                {
                    var offset = 0;
                    while (offset < _frameBytes)
                    {
                        var read = await outputPipes.VideoStream.ReadAsync(
                            frame.AsMemory(offset, _frameBytes - offset),
                            token);
                        if (read <= 0)
                        {
                            retryCandidate = Interlocked.Read(ref _framesRead) == framesAtCandidateStart;
                            break;
                        }

                        offset += read;
                    }

                    if (offset < _frameBytes)
                    {
                        break;
                    }

                    var videoPtsSeconds = await WaitForVideoPtsAsync(outputPipes, frameOrdinal++, token);
                    var frameReadTicks = DateTime.UtcNow.Ticks;
                    Interlocked.Exchange(ref _lastFrameReadTicks, frameReadTicks);
                    UpdateSnapshot(frame);
                    _state = "playing";
                    QueueTimestampedBitmapFrame(frame, frameReadTicks, videoPtsSeconds);
                }

                if (retryCandidate && TryStartNextCandidate(ref process, ref outputPipes, token))
                {
                    continue;
                }

                _state = process.HasExited ? "ended" : "waiting";
                return;
            }
        }
        catch (OperationCanceledException)
        {
            _state = "stopped";
        }
        catch (Exception ex)
        {
            _lastError = MainWindow.MaskStreamUrl(ex.Message);
            _state = "error";
        }
    }

    private static async System.Threading.Tasks.Task<double> WaitForVideoPtsAsync(
        FfmpegOutputPipeContext outputPipes,
        long ordinal,
        CancellationToken token)
    {
        Interlocked.Exchange(ref outputPipes.LastRequestedVideoPtsOrdinal, ordinal);
        if (outputPipes.VideoPtsByOrdinal.TryRemove(ordinal, out var availablePtsSeconds))
        {
            _ = outputPipes.VideoPtsAvailable.Wait(0);
            return availablePtsSeconds;
        }

        if (Volatile.Read(ref outputPipes.VideoPtsWaitDisabled) != 0)
        {
            return TryEstimateVideoPts(outputPipes, ordinal, out var estimatedPtsSeconds)
                ? estimatedPtsSeconds
                : double.NaN;
        }

        while (outputPipes.VideoPtsAvailable.Wait(0))
        {
            // Coalesce producer notifications that pre-date this ordinal wait.
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            if (outputPipes.VideoPtsByOrdinal.TryRemove(ordinal, out var ptsSeconds))
            {
                _ = outputPipes.VideoPtsAvailable.Wait(0);
                return ptsSeconds;
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var remaining = VideoPtsWaitTimeout - elapsed;
            if (remaining <= TimeSpan.Zero ||
                !await outputPipes.VideoPtsAvailable.WaitAsync(remaining, token).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref outputPipes.VideoPtsWaitDisabled, 1);
                return TryEstimateVideoPts(outputPipes, ordinal, out var estimatedPtsSeconds)
                    ? estimatedPtsSeconds
                    : double.NaN;
            }
        }

        return double.NaN;
    }

    private static void StoreVideoPts(FfmpegOutputPipeContext outputPipes, long ordinal, double ptsSeconds)
    {
        lock (outputPipes.VideoPtsStateSync)
        {
            if (ordinal >= outputPipes.LastObservedVideoPtsOrdinal)
            {
                outputPipes.LastObservedVideoPtsOrdinal = ordinal;
                outputPipes.LastObservedVideoPtsSeconds = ptsSeconds;
            }
        }

        var lastRequestedOrdinal = Volatile.Read(ref outputPipes.LastRequestedVideoPtsOrdinal);
        if (ordinal < lastRequestedOrdinal)
        {
            return;
        }

        outputPipes.VideoPtsByOrdinal[ordinal] = ptsSeconds;
        if (outputPipes.VideoPtsByOrdinal.Count > MaxVideoPtsBufferedOrdinals + 1)
        {
            var minimumUsefulOrdinal = Math.Max(lastRequestedOrdinal, ordinal - MaxVideoPtsBufferedOrdinals);
            foreach (var bufferedOrdinal in outputPipes.VideoPtsByOrdinal.Keys)
            {
                if (bufferedOrdinal < minimumUsefulOrdinal)
                {
                    outputPipes.VideoPtsByOrdinal.TryRemove(bufferedOrdinal, out _);
                }
            }
        }

        if (outputPipes.VideoPtsAvailable.CurrentCount == 0)
        {
            try
            {
                outputPipes.VideoPtsAvailable.Release();
            }
            catch (SemaphoreFullException)
            {
                // A single coalesced wake-up is enough for the sole video reader.
            }
        }
    }

    private static bool TryEstimateVideoPts(
        FfmpegOutputPipeContext outputPipes,
        long ordinal,
        out double ptsSeconds)
    {
        lock (outputPipes.VideoPtsStateSync)
        {
            var ordinalDelta = ordinal - outputPipes.LastObservedVideoPtsOrdinal;
            if (outputPipes.LastObservedVideoPtsOrdinal >= 0 &&
                double.IsFinite(outputPipes.LastObservedVideoPtsSeconds) &&
                ordinalDelta >= 0 &&
                ordinalDelta <= MaxVideoPtsExtrapolationFrames)
            {
                ptsSeconds = outputPipes.LastObservedVideoPtsSeconds + ordinalDelta / TargetFps;
                return true;
            }
        }

        ptsSeconds = double.NaN;
        return false;
    }

    private void QueueBitmapFrame(byte[] frame, long frameReadTicks)
    {
        QueueTimestampedBitmapFrame(frame, frameReadTicks, double.NaN);
    }

    private void QueueTimestampedBitmapFrame(byte[] frame, long frameReadTicks, double videoPtsSeconds)
    {
        var shouldSchedule = false;
        lock (_bitmapFrameSync)
        {
            if (!_bitmapWriteScheduled)
            {
                Buffer.BlockCopy(frame, 0, _bitmapWriteFrameBuffer, 0, _frameBytes);
                _bitmapWriteFrameReadTicks = frameReadTicks;
                _bitmapWriteFramePtsSeconds = videoPtsSeconds;
                _bitmapWriteScheduled = true;
                shouldSchedule = true;
                Interlocked.Increment(ref _bitmapFramesQueued);
            }
            else
            {
                if (_bitmapPendingFrameReady)
                {
                    Interlocked.Increment(ref _bitmapFramesDropped);
                }

                Buffer.BlockCopy(frame, 0, _bitmapPendingFrameBuffer, 0, _frameBytes);
                _bitmapPendingFrameReadTicks = frameReadTicks;
                _bitmapPendingFramePtsSeconds = videoPtsSeconds;
                _bitmapPendingFrameReady = true;
                Interlocked.Increment(ref _bitmapFramesCoalesced);
            }
        }

        if (shouldSchedule)
        {
            DispatchBitmapFrame();
        }
    }

    private void DispatchBitmapFrame()
    {
        _ = _dispatcher.BeginInvoke(() =>
        {
            try
            {
                Bitmap.WritePixels(new Int32Rect(0, 0, _outputWidth, _outputHeight), _bitmapWriteFrameBuffer, _stride, 0);
                var presentedTicks = DateTime.UtcNow.Ticks;
                var presentedFrameReadTicks = _bitmapWriteFrameReadTicks;
                Interlocked.Exchange(ref _lastFramePresentedTicks, presentedTicks);
                Interlocked.Exchange(ref _lastPresentedFrameReadTicks, presentedFrameReadTicks);
                var presentedFrames = Interlocked.Increment(ref _bitmapFramesPresented);
                lock (_sync)
                {
                    if (double.IsFinite(_bitmapWriteFramePtsSeconds) &&
                        double.IsFinite(_lastPresentedVideoPtsSeconds) &&
                        _bitmapWriteFramePtsSeconds + 0.5 < _lastPresentedVideoPtsSeconds)
                    {
                        _audioTimeline.Clear();
                        _presentedAudioSnapshot = null;
                        _lastAudioTimelinePtsSeconds = double.NaN;
                    }

                    _lastPresentedVideoPtsSeconds = _bitmapWriteFramePtsSeconds;
                    if (double.IsFinite(_lastPresentedVideoPtsSeconds))
                    {
                        SelectPresentedAudioSnapshotLocked();
                    }
                    else
                    {
                        _presentedAudioSnapshot = null;
                    }

                    UpdateObservedFrameRateLocked(
                        ref _presentedFpsWindowStartTimestamp,
                        ref _presentedFpsWindowStartFrames,
                        ref _observedPresentedFps,
                        presentedFrames);
                }

                if (presentedFrames == 1)
                {
                    FinalizeDecodeBackendObservation();
                    try
                    {
                        _firstFramePresented();
                    }
                    catch (Exception ex)
                    {
                        _lastError = MainWindow.MaskStreamUrl(ex.Message);
                    }
                }
            }
            finally
            {
                var shouldScheduleNext = false;
                lock (_bitmapFrameSync)
                {
                    if (_bitmapPendingFrameReady)
                    {
                        (_bitmapWriteFrameBuffer, _bitmapPendingFrameBuffer) = (_bitmapPendingFrameBuffer, _bitmapWriteFrameBuffer);
                        (_bitmapWriteFrameReadTicks, _bitmapPendingFrameReadTicks) = (_bitmapPendingFrameReadTicks, _bitmapWriteFrameReadTicks);
                        (_bitmapWriteFramePtsSeconds, _bitmapPendingFramePtsSeconds) = (_bitmapPendingFramePtsSeconds, _bitmapWriteFramePtsSeconds);
                        _bitmapPendingFrameReady = false;
                        shouldScheduleNext = true;
                        Interlocked.Increment(ref _bitmapFramesQueued);
                    }
                    else
                    {
                        _bitmapWriteScheduled = false;
                    }
                }

                if (shouldScheduleNext)
                {
                    DispatchBitmapFrame();
                }
            }
        }, DispatcherPriority.Render);
    }

    private bool TryStartNextCandidate(
        ref Process process,
        ref FfmpegOutputPipeContext outputPipes,
        CancellationToken token)
    {
        if (_candidateIndex + 1 >= _inputCandidates.Count || token.IsCancellationRequested)
        {
            return false;
        }

        TerminateProcessAndPipes(process, outputPipes);

        _candidateIndex++;
        var nextInput = _inputCandidates[_candidateIndex];
        _lastError = $"Retrying ffmpeg input as {MainWindow.MaskStreamUrl(nextInput)}";
        _state = "opening";
        var audioMetadataGeneration = Interlocked.Increment(ref _audioMetadataGeneration);
        ResetAudioVideoSyncState();
        var nextProcess = StartProcess(nextInput, token, out var nextOutputPipes);
        var terminateStartup = false;
        lock (_processLifecycleSync)
        {
            if (_disposed || token.IsCancellationRequested)
            {
                terminateStartup = true;
            }
            else
            {
                process = nextProcess;
                outputPipes = nextOutputPipes;
                _process = nextProcess;
                _outputPipes = nextOutputPipes;
                StartProcessReaders(nextProcess, nextOutputPipes, token, audioMetadataGeneration);
            }
        }

        if (terminateStartup)
        {
            TerminateProcessAndPipes(nextProcess, nextOutputPipes);
            return false;
        }

        return true;
    }

    private void ResetAudioVideoSyncState()
    {
        lock (_audioParserSync)
        {
            lock (_sync)
            {
                _audioSnapshot = null;
                _presentedAudioSnapshot = null;
                _audioTimeline.Clear();
                _lastAudioTimelinePtsSeconds = double.NaN;
                _lastPresentedVideoPtsSeconds = double.NaN;
            }

            _activeAudioPtsSeconds = double.NaN;
            _parsedAudioChannelMask = 0;
            _parsedAudioChannelRmsMask = 0;
            _parsedAudioChannelPeakMask = 0;
            _lastParsedRmsDb = SilenceFloorDb;
            _lastParsedPeakDb = SilenceFloorDb;
            Array.Fill(_lastParsedChannelRmsDb, SilenceFloorDb);
            Array.Fill(_lastParsedChannelPeakDb, SilenceFloorDb);
        }
    }

    private void UpdateSnapshot(byte[] frame)
    {
        var sampleIndex = 0;
        for (var y = 0; y < _outputHeight; y += _analysisSampleStrideY)
        {
            var rowStart = y * _stride;
            for (var x = 0; x < _outputWidth; x += _analysisSampleStrideX)
            {
                var index = rowStart + x * BytesPerPixel;
                var blue = frame[index];
                var green = frame[index + 1];
                var red = frame[index + 2];
                var luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                _currentSampleLuma[sampleIndex++] = luma;
            }
        }

        var bounds = FindActiveAnalysisBounds(
            _currentSampleLuma,
            _analysisSampleColumns,
            _analysisSampleRows,
            Math.Max(4, DarkPixelThreshold * 255));
        double total = 0;
        double diffTotal = 0;
        var darkPixels = 0;
        var sampleCount = 0;
        for (var row = bounds.Top; row < bounds.BottomExclusive; row++)
        {
            for (var column = bounds.Left; column < bounds.RightExclusive; column++)
            {
                var index = row * _analysisSampleColumns + column;
                var luma = _currentSampleLuma[index];
                total += luma;
                sampleCount++;
                if (luma / 255 <= DarkPixelThreshold)
                {
                    darkPixels++;
                }

                if (_hasPreviousFrame)
                {
                    diffTotal += Math.Abs(luma - _previousSampleLuma[index]);
                }
            }
        }

        sampleCount = Math.Max(1, sampleCount);
        var difference = _hasPreviousFrame ? diffTotal / sampleCount / 255 : 1;
        _stableFrames = difference <= StabilityDifferenceThreshold ? _stableFrames + 1 : 0;
        _hasPreviousFrame = true;
        Array.Copy(_currentSampleLuma, _previousSampleLuma, _currentSampleLuma.Length);
        VideoLumaSnapshot snapshot;
        lock (_sync)
        {
            _framesRead++;
            _bytesRead += frame.Length;
            UpdateObservedFrameRateLocked(
                ref _readFpsWindowStartTimestamp,
                ref _readFpsWindowStartFrames,
                ref _observedReadFps,
                _framesRead);
            snapshot = new VideoLumaSnapshot(
                total / sampleCount / 255,
                darkPixels / (double)sampleCount,
                difference,
                _stableFrames,
                DateTimeOffset.UtcNow);
            _snapshot = snapshot;
        }

        var blackFrameDetected = snapshot.Luma <= BlackLumaThreshold &&
            snapshot.DarkRatio >= BlackPixelRatioThreshold;
        if (!blackFrameDetected)
        {
            _blackFrameAlertLatched = false;
        }
        else if (!_blackFrameAlertLatched)
        {
            _blackFrameAlertLatched = true;
            _blackFrameDetected();
        }
    }

    private static AnalysisSampleBounds FindActiveAnalysisBounds(
        IReadOnlyList<double> samples,
        int columns,
        int rows,
        double darkLumaThreshold)
    {
        if (columns <= 0 || rows <= 0 || samples.Count < columns * rows)
        {
            return new AnalysisSampleBounds(0, 0, Math.Max(1, columns), Math.Max(1, rows));
        }

        var left = 0;
        var right = columns;
        var top = 0;
        var bottom = rows;
        while (left < right && IsDarkAnalysisColumn(samples, columns, rows, left, darkLumaThreshold))
        {
            left++;
        }
        while (right > left && IsDarkAnalysisColumn(samples, columns, rows, right - 1, darkLumaThreshold))
        {
            right--;
        }
        while (top < bottom && IsDarkAnalysisRow(samples, columns, top, darkLumaThreshold))
        {
            top++;
        }
        while (bottom > top && IsDarkAnalysisRow(samples, columns, bottom - 1, darkLumaThreshold))
        {
            bottom--;
        }

        var activeWidth = right - left;
        var activeHeight = bottom - top;
        if (activeWidth < Math.Max(2, columns / 4) || activeHeight < Math.Max(2, rows / 4))
        {
            return new AnalysisSampleBounds(0, 0, columns, rows);
        }

        return new AnalysisSampleBounds(left, top, right, bottom);
    }

    private static bool IsDarkAnalysisColumn(
        IReadOnlyList<double> samples,
        int columns,
        int rows,
        int column,
        double threshold)
    {
        var dark = 0;
        for (var row = 0; row < rows; row++)
        {
            if (samples[row * columns + column] <= threshold)
            {
                dark++;
            }
        }

        return dark >= Math.Ceiling(rows * 0.98);
    }

    private static bool IsDarkAnalysisRow(
        IReadOnlyList<double> samples,
        int columns,
        int row,
        double threshold)
    {
        var dark = 0;
        var rowStart = row * columns;
        for (var column = 0; column < columns; column++)
        {
            if (samples[rowStart + column] <= threshold)
            {
                dark++;
            }
        }

        return dark >= Math.Ceiling(columns * 0.98);
    }

    private readonly record struct AnalysisSampleBounds(
        int Left,
        int Top,
        int RightExclusive,
        int BottomExclusive);

    private static void UpdateObservedFrameRateLocked(
        ref long windowStartTimestamp,
        ref long windowStartFrames,
        ref double observedFps,
        long currentFrames)
    {
        var now = Stopwatch.GetTimestamp();
        if (windowStartTimestamp <= 0 || currentFrames <= windowStartFrames)
        {
            windowStartTimestamp = now;
            windowStartFrames = currentFrames;
            return;
        }

        var elapsedSeconds = (now - windowStartTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds < FpsObservationWindowSeconds)
        {
            return;
        }

        observedFps = (currentFrames - windowStartFrames) / elapsedSeconds;
        windowStartTimestamp = now;
        windowStartFrames = currentFrames;
    }

    private sealed class FfmpegOutputPipeContext : IDisposable
    {
        private int _disposed;

        public FfmpegOutputPipeContext(
            CancellationToken token,
            bool videoPtsEnabled = true,
            bool videoPtsPipeEnabled = true)
        {
            var suffix = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
            var videoName = $"ipmv-video-{suffix}";
            var videoPtsName = $"ipmv-vpts-{suffix}";
            VideoPath = $@"\\.\pipe\{videoName}";
            VideoPtsPath = $@"\\.\pipe\{videoPtsName}";
            VideoStream = new NamedPipeServerStream(
                videoName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            VideoPtsStream = new NamedPipeServerStream(
                videoPtsName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            VideoConnected = VideoStream.WaitForConnectionAsync(token);
            VideoPtsConnected = VideoPtsStream.WaitForConnectionAsync(token);
            VideoPtsEnabled = videoPtsEnabled;
            VideoPtsPipeEnabled = videoPtsPipeEnabled;
            VideoPtsWaitDisabled = videoPtsEnabled ? 0 : 1;
        }

        public string VideoPath { get; }

        public string VideoPtsPath { get; }

        public bool VideoPtsEnabled { get; }

        public bool VideoPtsPipeEnabled { get; }

        public NamedPipeServerStream VideoStream { get; }

        public NamedPipeServerStream VideoPtsStream { get; }

        public System.Threading.Tasks.Task VideoConnected { get; }

        public System.Threading.Tasks.Task VideoPtsConnected { get; }

        public ConcurrentDictionary<long, double> VideoPtsByOrdinal { get; } = new();

        public SemaphoreSlim VideoPtsAvailable { get; } = new(0, 1);

        public object VideoPtsStateSync { get; } = new();

        public long LastRequestedVideoPtsOrdinal = -1;

        public long LastObservedVideoPtsOrdinal = -1;

        public double LastObservedVideoPtsSeconds = double.NaN;

        public int VideoPtsWaitDisabled;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                VideoStream.Dispose();
            }
            catch
            {
                // Best effort teardown for a terminating FFmpeg process.
            }

            try
            {
                VideoPtsStream.Dispose();
            }
            catch
            {
                // Best effort teardown for a terminating FFmpeg process.
            }

        }
    }

    public void Dispose()
    {
        Process? process;
        FfmpegOutputPipeContext? outputPipes;
        CancellationTokenSource? cts;
        lock (_processLifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _audioMetadataGeneration);
            cts = _cts;
            _cts = null;
            process = _process;
            _process = null;
            outputPipes = _outputPipes;
            _outputPipes = null;
            cts?.Cancel();
        }

        if (process is not null && outputPipes is not null)
        {
            TerminateProcessAndPipes(process, outputPipes);
        }
        else
        {
            try
            {
                process?.Dispose();
            }
            catch
            {
                // Best effort shutdown.
            }

            outputPipes?.Dispose();
        }

        cts?.Dispose();
        _state = "stopped";
    }
}

internal sealed class FfmpegVideoAnalysisMonitor : IVideoAnalysisMonitor, IImmediateBlackFrameMonitor
{
    private const int AnalysisFps = 10;
    private const int Width = 160;
    private const int Height = 90;
    private const int BytesPerPixel = 3;
    private const int FrameBytes = Width * Height * BytesPerPixel;
    private static readonly string AnalysisVideoFilter = $"fps={AnalysisFps},scale={Width}:{Height}:flags=fast_bilinear,format=rgb24";

    private readonly string _ffmpegPath;
    private readonly string _inputUrl;
    private readonly string _protocol;
    private readonly object _sync = new();
    private readonly object _callbackSync = new();
    private readonly List<Action> _blackFrameDetectedCallbacks = [];
    private readonly byte[] _previousBuffer = new byte[FrameBytes];
    private Process? _process;
    private CancellationTokenSource? _cts;
    private VideoLumaSnapshot? _snapshot;
    private int _stableFrames;
    private bool _hasPreviousFrame;
    private bool _blackFrameAlertLatched;
    private string _state = "stopped";
    private string _lastError = "";
    private bool _disposed;

    public FfmpegVideoAnalysisMonitor(
        string ffmpegPath,
        string inputUrl,
        string protocol,
        double darkPixelThreshold,
        double stabilityDifferenceThreshold,
        double blackLumaThreshold = 0.04,
        double blackPixelRatioThreshold = 0.85,
        Action? blackFrameDetected = null)
    {
        _ffmpegPath = ffmpegPath;
        _inputUrl = inputUrl;
        _protocol = protocol;
        AddBlackFrameDetectedCallback(blackFrameDetected);
        DarkPixelThreshold = Math.Clamp(darkPixelThreshold, 0.0, 1.0);
        StabilityDifferenceThreshold = Math.Clamp(stabilityDifferenceThreshold, 0.0, 1.0);
        BlackLumaThreshold = Math.Clamp(blackLumaThreshold, 0.0, 1.0);
        BlackPixelRatioThreshold = Math.Clamp(blackPixelRatioThreshold, 0.0, 1.0);
    }

    public string Kind => "ffmpeg";

    public string State => _state;

    public string LastError => _lastError;

    public double DarkPixelThreshold { get; set; }

    public double StabilityDifferenceThreshold { get; set; }

    public double BlackLumaThreshold { get; set; }

    public double BlackPixelRatioThreshold { get; set; }

    public void AddBlackFrameDetectedCallback(Action? callback)
    {
        if (callback is null)
        {
            return;
        }

        lock (_callbackSync)
        {
            _blackFrameDetectedCallbacks.Add(callback);
        }
    }

    public void Start()
    {
        if (_process is not null)
        {
            return;
        }

        var inputUrl = FfmpegInputUrl(_inputUrl);
        _cts = new CancellationTokenSource();
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        AddLowCostAnalysisInputArguments(startInfo.ArgumentList);
        if (string.Equals(_protocol, "rtsp", StringComparison.OrdinalIgnoreCase) ||
            inputUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-rtsp_transport");
            startInfo.ArgumentList.Add("tcp");
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputUrl);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add(AnalysisVideoFilter);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _state = "opening";
        _ = System.Threading.Tasks.Task.Run(() => DrainErrorsAsync(_process, _cts.Token));
        _ = System.Threading.Tasks.Task.Run(() => ReadFramesAsync(_process, _cts.Token));
    }

    private static string FfmpegInputUrl(string inputUrl)
    {
        return Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : inputUrl;
    }

    private static void AddLowCostAnalysisInputArguments(Collection<string> arguments)
    {
        AddBestEffortHardwareDecodeArguments(arguments);
        arguments.Add("-fflags");
        arguments.Add("nobuffer");
        arguments.Add("-flags");
        arguments.Add("low_delay");
        arguments.Add("-probesize");
        arguments.Add("32768");
        arguments.Add("-analyzeduration");
        arguments.Add("0");
        arguments.Add("-rw_timeout");
        arguments.Add("3000000");
        arguments.Add("-threads");
        arguments.Add("1");
        arguments.Add("-filter_threads");
        arguments.Add("1");
    }

    private static void AddBestEffortHardwareDecodeArguments(Collection<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        MainWindow.AddFfmpegHardwareDecodeArguments(arguments);
    }

    public bool TryGetSnapshot(out VideoLumaSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_snapshot is null)
            {
                snapshot = new VideoLumaSnapshot(0, 0, 1, 0, DateTimeOffset.MinValue);
                return false;
            }

            snapshot = _snapshot;
            return true;
        }
    }

    private async System.Threading.Tasks.Task DrainErrorsAsync(Process process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _lastError = MainWindow.MaskStreamUrl(line);
                }
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private async System.Threading.Tasks.Task ReadFramesAsync(Process process, CancellationToken token)
    {
        var frame = new byte[FrameBytes];
        try
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                var offset = 0;
                while (offset < FrameBytes)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(frame.AsMemory(offset, FrameBytes - offset), token);
                    if (read <= 0)
                    {
                        _state = process.HasExited ? "ended" : "waiting";
                        return;
                    }

                    offset += read;
                }

                UpdateSnapshot(frame);
                _state = "playing";
            }
        }
        catch (OperationCanceledException)
        {
            _state = "stopped";
        }
        catch
        {
            _state = "error";
        }
    }

    private void UpdateSnapshot(byte[] frame)
    {
        double total = 0;
        double diffTotal = 0;
        var darkPixels = 0;
        var pixelCount = Width * Height;
        for (var index = 0; index < frame.Length; index += 3)
        {
            var red = frame[index];
            var green = frame[index + 1];
            var blue = frame[index + 2];
            var luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
            total += luma;
            if (luma / 255 <= DarkPixelThreshold)
            {
                darkPixels++;
            }

            if (_hasPreviousFrame)
            {
                var previousRed = _previousBuffer[index];
                var previousGreen = _previousBuffer[index + 1];
                var previousBlue = _previousBuffer[index + 2];
                var previousLuma = 0.2126 * previousRed + 0.7152 * previousGreen + 0.0722 * previousBlue;
                diffTotal += Math.Abs(luma - previousLuma);
            }
        }

        var difference = _hasPreviousFrame ? diffTotal / pixelCount / 255 : 1;
        _stableFrames = difference <= StabilityDifferenceThreshold ? _stableFrames + 1 : 0;
        Buffer.BlockCopy(frame, 0, _previousBuffer, 0, frame.Length);
        _hasPreviousFrame = true;
        VideoLumaSnapshot snapshot;
        lock (_sync)
        {
            snapshot = new VideoLumaSnapshot(
                total / pixelCount / 255,
                darkPixels / (double)pixelCount,
                difference,
                _stableFrames,
                DateTimeOffset.UtcNow);
            _snapshot = snapshot;
        }

        var blackFrameDetected = snapshot.Luma <= BlackLumaThreshold &&
            snapshot.DarkRatio >= BlackPixelRatioThreshold;
        if (!blackFrameDetected)
        {
            _blackFrameAlertLatched = false;
        }
        else if (!_blackFrameAlertLatched)
        {
            _blackFrameAlertLatched = true;
            NotifyBlackFrameDetected();
        }
    }

    private void NotifyBlackFrameDetected()
    {
        Action[] callbacks;
        lock (_callbackSync)
        {
            callbacks = _blackFrameDetectedCallbacks.ToArray();
        }

        foreach (var callback in callbacks)
        {
            try
            {
                callback();
            }
            catch
            {
                // Analysis callbacks must not stop the FFmpeg reader.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts?.Cancel();
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort shutdown.
        }

        _process?.Dispose();
        _cts?.Dispose();
        _state = "stopped";
    }
}

internal sealed class VideoLumaMonitor : IVideoAnalysisMonitor
{
    private const uint Width = 160;
    private const uint Height = 90;
    private const uint BytesPerPixel = 4;
    private const uint Pitch = Width * BytesPerPixel;

    private readonly LibVLC _libVlc;
    private readonly string _inputUrl;
    private readonly VlcMediaPlayer _player;
    private readonly byte[] _buffer = new byte[Pitch * Height];
    private readonly byte[] _previousBuffer = new byte[Pitch * Height];
    private readonly GCHandle _bufferHandle;
    private readonly object _sync = new();
    private readonly VlcMediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoDisplayCb _displayCallback;
    private VideoLumaSnapshot? _snapshot;
    private int _stableFrames;
    private bool _hasPreviousFrame;
    private bool _disposed;

    public VideoLumaMonitor(LibVLC libVlc, string inputUrl, double darkPixelThreshold, double stabilityDifferenceThreshold)
    {
        _libVlc = libVlc;
        _inputUrl = inputUrl;
        DarkPixelThreshold = Math.Clamp(darkPixelThreshold, 0.0, 1.0);
        StabilityDifferenceThreshold = Math.Clamp(stabilityDifferenceThreshold, 0.0, 1.0);
        _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        _player = new VlcMediaPlayer(libVlc);
        _lockCallback = OnVideoLock;
        _unlockCallback = (_, _, _) => { };
        _displayCallback = (_, _) => UpdateLuma();
        _player.SetVideoFormat("RV32", Width, Height, Pitch);
        _player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
    }

    public string Kind => "vlc";

    public string LastError => "";

    public double DarkPixelThreshold { get; set; }

    public double StabilityDifferenceThreshold { get; set; }

    public string State => _player.State.ToString().ToLowerInvariant();

    public void Start()
    {
        using var media = new Media(_libVlc, _inputUrl, FromType.FromLocation);
        MainWindow.AddLowLatencyVlcMediaOptions(media, disableAudio: true);
        if (!_player.Play(media))
        {
            throw new InvalidOperationException("LibVLC failed to start video analysis monitor.");
        }
    }

    public bool TryGetSnapshot(out VideoLumaSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_snapshot is null)
            {
                snapshot = new VideoLumaSnapshot(0, 0, 1, 0, DateTimeOffset.MinValue);
                return false;
            }

            snapshot = _snapshot;
            return true;
        }
    }

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _bufferHandle.AddrOfPinnedObject());
        return IntPtr.Zero;
    }

    private void UpdateLuma()
    {
        try
        {
            double total = 0;
            double diffTotal = 0;
            var darkPixels = 0;
            var pixelCount = (int)(Width * Height);
            for (var index = 0; index < _buffer.Length; index += 4)
            {
                var blue = _buffer[index];
                var green = _buffer[index + 1];
                var red = _buffer[index + 2];
                var luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                total += luma;
                if (luma / 255 <= DarkPixelThreshold)
                {
                    darkPixels++;
                }

                if (_hasPreviousFrame)
                {
                    var previousBlue = _previousBuffer[index];
                    var previousGreen = _previousBuffer[index + 1];
                    var previousRed = _previousBuffer[index + 2];
                    var previousLuma = 0.2126 * previousRed + 0.7152 * previousGreen + 0.0722 * previousBlue;
                    diffTotal += Math.Abs(luma - previousLuma);
                }
            }

            var difference = _hasPreviousFrame ? diffTotal / pixelCount / 255 : 1;
            _stableFrames = difference <= StabilityDifferenceThreshold ? _stableFrames + 1 : 0;
            Buffer.BlockCopy(_buffer, 0, _previousBuffer, 0, _buffer.Length);
            _hasPreviousFrame = true;
            lock (_sync)
            {
                _snapshot = new VideoLumaSnapshot(
                    total / pixelCount / 255,
                    darkPixels / (double)pixelCount,
                    difference,
                    _stableFrames,
                    DateTimeOffset.UtcNow);
            }
        }
        catch
        {
            // Video callbacks run on a LibVLC thread; keep monitoring failures isolated.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _player.Stop();
        }
        catch
        {
            // Best effort shutdown.
        }

        _player.Dispose();
        if (_bufferHandle.IsAllocated)
        {
            _bufferHandle.Free();
        }
    }
}

internal sealed class AudioLevelMonitor : IDisposable
{
    private const uint SampleRate = 48000;
    private const int DefaultChannelCount = 2;
    private const int MaxChannelCount = 16;
    private const double SilenceFloorDb = -120;
    private const double DefaultClippingThresholdDb = -1;
    private const long AudioAnalysisMinIntervalMs = 50;
    private const string SampleFormat = "S16N";
    private static readonly double ClippingAmplitude = short.MaxValue * Math.Pow(10, DefaultClippingThresholdDb / 20.0);

    private readonly object _sync = new();
    private readonly VlcMediaPlayer.LibVLCAudioPlayCb _playCallback;
    private readonly VlcMediaPlayer.LibVLCAudioPauseCb _pauseCallback;
    private readonly VlcMediaPlayer.LibVLCAudioResumeCb _resumeCallback;
    private readonly VlcMediaPlayer.LibVLCAudioFlushCb _flushCallback;
    private readonly VlcMediaPlayer.LibVLCAudioDrainCb _drainCallback;
    private VlcMediaPlayer? _player;
    private AudioLevelSnapshot? _snapshot;
    private long _lastAudioAnalysisAtMs;
    private int _channelCount = DefaultChannelCount;
    private bool _disposed;

    public AudioLevelMonitor()
    {
        _playCallback = OnAudioPlay;
        _pauseCallback = (_, _) => { };
        _resumeCallback = (_, _) => { };
        _flushCallback = (_, _) => { };
        _drainCallback = _ => { };
    }

    public string State => _player?.State.ToString().ToLowerInvariant() ?? "detached";

    public void Attach(VlcMediaPlayer player)
    {
        _player = player;
        _channelCount = DefaultChannelCount;
        Interlocked.Exchange(ref _lastAudioAnalysisAtMs, 0);
        player.SetAudioFormat(SampleFormat, SampleRate, DefaultChannelCount);
        player.SetAudioCallbacks(_playCallback, _pauseCallback, _resumeCallback, _flushCallback, _drainCallback);
    }

    public bool TryGetSnapshot(out AudioLevelSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_snapshot is null)
            {
                snapshot = new AudioLevelSnapshot(SilenceFloorDb, SilenceFloorDb, 0, DateTimeOffset.MinValue, []);
                return false;
            }

            snapshot = _snapshot;
            return true;
        }
    }

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        try
        {
            var channelCount = Math.Clamp(_channelCount, 1, MaxChannelCount);
            var sampleCount = checked((int)(count * (uint)channelCount));
            if (sampleCount <= 0)
            {
                return;
            }

            if (!TryReserveAudioAnalysis())
            {
                return;
            }

            var buffer = ArrayPool<short>.Shared.Rent(sampleCount);
            try
            {
                Marshal.Copy(samples, buffer, 0, sampleCount);

                Span<double> perChannelSumSquares = stackalloc double[MaxChannelCount];
                Span<int> perChannelMaxAbs = stackalloc int[MaxChannelCount];
                Span<int> perChannelClippingSamples = stackalloc int[MaxChannelCount];
                double sumSquares = 0;
                var maxAbs = 0;
                var clippingSamples = 0;
                for (var index = 0; index < sampleCount; index++)
                {
                    var sample = buffer[index];
                    var channelIndex = index % channelCount;
                    var abs = Math.Abs((int)sample);
                    maxAbs = Math.Max(maxAbs, abs);
                    perChannelMaxAbs[channelIndex] = Math.Max(perChannelMaxAbs[channelIndex], abs);
                    if (abs >= ClippingAmplitude)
                    {
                        clippingSamples++;
                        perChannelClippingSamples[channelIndex]++;
                    }

                    var normalized = sample / 32768.0;
                    sumSquares += normalized * normalized;
                    perChannelSumSquares[channelIndex] += normalized * normalized;
                }

                var rms = Math.Sqrt(sumSquares / sampleCount);
                var levelDb = rms > 0 ? 20 * Math.Log10(rms) : SilenceFloorDb;
                var peakDb = maxAbs > 0 ? 20 * Math.Log10(Math.Min(maxAbs, 32767) / 32767.0) : SilenceFloorDb;
                var frames = Math.Max(1, sampleCount / channelCount);
                var channels = new AudioChannelLevel[channelCount];
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var channelRms = Math.Sqrt(perChannelSumSquares[channel] / frames);
                    var channelLevelDb = channelRms > 0 ? 20 * Math.Log10(channelRms) : SilenceFloorDb;
                    var channelPeakDb = perChannelMaxAbs[channel] > 0
                        ? 20 * Math.Log10(Math.Min(perChannelMaxAbs[channel], 32767) / 32767.0)
                        : SilenceFloorDb;
                    channels[channel] = new AudioChannelLevel(channel + 1, channelLevelDb, channelPeakDb, perChannelClippingSamples[channel]);
                }

                lock (_sync)
                {
                    _snapshot = new AudioLevelSnapshot(levelDb, peakDb, clippingSamples, DateTimeOffset.UtcNow, channels);
                }
            }
            finally
            {
                ArrayPool<short>.Shared.Return(buffer);
            }
        }
        catch
        {
            // Audio callbacks run on a LibVLC thread; never let analysis errors escape into playback.
        }
    }

    private bool TryReserveAudioAnalysis()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastAudioAnalysisAtMs);
        if (last != 0 &&
            now - last < AudioAnalysisMinIntervalMs)
        {
            return false;
        }

        Interlocked.Exchange(ref _lastAudioAnalysisAtMs, now);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player = null;
    }
}

internal sealed record ServerClientRegistration(
    string ClientId,
    string Name,
    string Version,
    string Mode,
    Dictionary<string, bool> Capabilities);

internal sealed record ServerClientHeartbeat(
    string ClientId,
    string ActiveLayoutId,
    string Status,
    int ActiveAlerts,
    DateTimeOffset Timestamp);

internal sealed record ServerAlert(
    string Id,
    string StreamId,
    string Type,
    string Severity,
    string Status,
    string Message,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SnoozedUntil,
    string? SourceClientId);

internal sealed record ServerClientStatus(
    string ClientId,
    string? Name,
    string? Version,
    string? Mode,
    Dictionary<string, bool>? Capabilities,
    string Status,
    string? ActiveLayoutId,
    int ActiveAlerts,
    DateTimeOffset? LastHeartbeatAt,
    long HeartbeatAgeSec,
    bool Online,
    bool Registered);
