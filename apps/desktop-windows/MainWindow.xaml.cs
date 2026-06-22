using System;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Microsoft.Win32;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace IPMultiViewer.Desktop;

public partial class MainWindow : Window
{
    private readonly record struct PlaybackVideoFreshness(double AgeMs, double RenderDelayMs);

    private static readonly object ColorResourceSync = new();
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

    internal static bool IsActiveAlertStatus(string status)
    {
        return ActiveAlertStatuses.Contains(status);
    }

    private LocalConfig _config = new();
    private readonly Dictionary<string, VlcMediaPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FfmpegPlaybackSession> _ffmpegPlaybackSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioLevelMonitor> _audioMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IVideoAnalysisMonitor> _videoMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaybackSnapshot> _playbackSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _issueSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastPlaybackTimeMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastPlaybackProgressMarker = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastPlaybackProgressAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastVideoAnalysisLogAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _playbackRestartAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastDisplayedPictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastVideoPresentedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Border>> _tileStatusFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _tileBorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _tileStatusTextBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StackPanel> _tileAlertStacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _tileSurfaceHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileSurfaceRenderKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoView> _tileVideoViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _tileFfmpegImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _tileSurfacePlaceholders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _tileAudioMeterHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioMeterView[]> _tileAudioMeterViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, AudioMeterView>> _tileAudioMeterViewLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastAudioMeterTargetRefreshAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileChromeRenderKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileStatusTextRenderKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileRenderedAlertKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _alertSoundWaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _immediateVideoAlertEvaluationSync = new();
    private readonly HashSet<string> _pendingImmediateVideoAlertEvaluations = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _serverSyncInFlight;
    private bool _playbackStartInFlight;
    private bool _layoutSelectionInProgress;
    private const double TileFrameThickness = 3;
    private const int LivePlaybackCacheMs = 100;
    private static readonly TimeSpan PlaybackVideoFreshnessWarning = TimeSpan.FromSeconds(1);
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
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += (_, _) => RefreshPlaybackMetadata();
        _audioMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _audioMeterTimer.Tick += (_, _) => RefreshAudioMeterTargets();
        InitializeComponent();
        _settingsWindow = new SettingsWindow(this);
        LoadConfig();
        CompositionTarget.Rendering += AudioMeterCompositionTarget_Rendering;
        _playbackTimer.Start();
        _audioMeterTimer.Start();
        QueueInitialPlaybackStart();
    }

    private void LoadConfig()
    {
        _isLoading = true;
        _config = LocalConfigStore.LoadOrCreate();
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

        Core.Initialize();
        _libVlc = new LibVLC(LowLatencyVlcInstanceOptions());
        return _libVlc;
    }

    internal static string[] LowLatencyVlcInstanceOptions()
    {
        return
        [
            "--no-video-title-show",
            $"--network-caching={LivePlaybackCacheMs}",
            $"--live-caching={LivePlaybackCacheMs}",
            $"--file-caching={LivePlaybackCacheMs}",
            "--drop-late-frames",
            "--skip-frames",
            "--clock-jitter=0",
            "--clock-synchro=0"
        ];
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
        DetachRenderedPlaybackSurfaces();
        TileGrid.Children.Clear();
        _tileStatusFrames.Clear();
        _tileBorders.Clear();
        _tileStatusTextBlocks.Clear();
        _tileAlertStacks.Clear();
        _tileSurfaceHosts.Clear();
        _tileSurfaceRenderKeys.Clear();
        _tileVideoViews.Clear();
        _tileFfmpegImages.Clear();
        _tileSurfacePlaceholders.Clear();
        _tileAudioMeterHosts.Clear();
        _tileAudioMeterViews.Clear();
        _tileAudioMeterViewLookup.Clear();
        _lastAudioMeterTargetRefreshAt.Clear();
        _tileChromeRenderKeys.Clear();
        _tileStatusTextRenderKeys.Clear();
        _tileRenderedAlertKeys.Clear();

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
        foreach (var view in _tileVideoViews.Values)
        {
            view.MediaPlayer = null;
        }

        foreach (var image in _tileFfmpegImages.Values)
        {
            image.Source = null;
        }

        foreach (var host in _tileSurfaceHosts.Values)
        {
            host.Children.Clear();
        }
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
        List<AlertRecord> alerts = stream is null ? [] : ActiveAlertsFor(stream.Id);
        var alert = alerts.FirstOrDefault();
        var pendingIssue = stream is null ? null : PendingIssueFor(stream.Id);
        PlaybackSnapshot? snapshot = null;
        var isLoaded = stream is not null && IsPlaybackLoaded(stream.Id);
        if (stream is not null)
        {
            _playbackSnapshots.TryGetValue(stream.Id, out snapshot);
        }

        var videoFreshness = stream is null ? default : PlaybackVideoFreshnessFor(stream.Id);
        var statusColor = alert is null && pendingIssue is not null
            ? StatusColorForPendingIssue(pendingIssue)
            : StatusColorFor(stream, alert, isLoaded, snapshot, videoFreshness);
        var statusText = alert is null && pendingIssue is not null
            ? $"pending / {pendingIssue}"
            : StatusTextFor(stream, alert, isLoaded, snapshot, videoFreshness);

        var root = new Grid
        {
            Background = BrushFromHex("#070b12"),
            ClipToBounds = true
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var statusStrip = new Border
        {
            Height = TileFrameThickness,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = BrushFromHex(statusColor),
            IsHitTestVisible = false
        };

        var alertStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0),
            Background = Brushes.Transparent
        };

        var videoFrame = new Grid { Background = BrushFromHex(statusColor) };
        videoFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileFrameThickness) });
        videoFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        videoFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileFrameThickness) });
        videoFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        videoFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileFrameThickness) });
        Grid.SetRow(videoFrame, 0);
        root.Children.Add(videoFrame);

        var leftFrame = new Border { Background = BrushFromHex(statusColor), IsHitTestVisible = false };
        Grid.SetColumn(leftFrame, 0);
        Grid.SetRowSpan(leftFrame, 2);
        videoFrame.Children.Add(leftFrame);

        Grid.SetColumn(statusStrip, 1);
        Grid.SetRow(statusStrip, 0);
        Panel.SetZIndex(statusStrip, 20);
        videoFrame.Children.Add(statusStrip);

        var rightFrame = new Border { Background = BrushFromHex(statusColor), IsHitTestVisible = false };
        Grid.SetColumn(rightFrame, 2);
        Grid.SetRowSpan(rightFrame, 2);
        videoFrame.Children.Add(rightFrame);

        var bottomFrame = new Border { Background = BrushFromHex(statusColor), IsHitTestVisible = false };
        Grid.SetColumn(bottomFrame, 1);
        Grid.SetRow(bottomFrame, 1);
        videoFrame.Children.Add(bottomFrame);

        var surfaceHost = new Grid { Background = Brushes.Black };
        Grid.SetColumn(surfaceHost, 1);
        Grid.SetRow(surfaceHost, 0);
        videoFrame.Children.Add(surfaceHost);
        SetPlaybackSurface(surfaceHost, stream);

        var audioMeterHost = new Grid
        {
            IsHitTestVisible = false,
            Margin = new Thickness(8, 8, 8, 8),
            Visibility = Visibility.Collapsed
        };
        audioMeterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        audioMeterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        audioMeterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(audioMeterHost, 1);
        Grid.SetRow(audioMeterHost, 0);
        Panel.SetZIndex(audioMeterHost, 8);
        videoFrame.Children.Add(audioMeterHost);

        Grid.SetColumn(alertStack, 1);
        Grid.SetRow(alertStack, 0);
        Panel.SetZIndex(alertStack, 10);
        videoFrame.Children.Add(alertStack);

        var bottomBar = new Grid
        {
            Background = BrushFromHex("#d80d111a"),
            Height = 44
        };
        Grid.SetRow(bottomBar, 1);
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var streamName = new TextBlock
        {
            Text = stream?.Name ?? $"Tile {index + 1}: Unassigned",
            Foreground = Brushes.White,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        bottomBar.Children.Add(streamName);
        var statusTextBlock = new TextBlock
        {
            Text = statusText,
            Foreground = BrushFromHex(statusColor),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(statusTextBlock, 1);
        bottomBar.Children.Add(statusTextBlock);
        var assignButton = new Button
        {
            Content = stream is null ? "Assign" : "Change",
            Tag = index,
            Height = 26,
            MinWidth = 66,
            Margin = new Thickness(0, 5, 8, 5),
            Opacity = 0.92
        };
        assignButton.Click += TileAssignButton_Click;
        Grid.SetColumn(assignButton, 2);
        bottomBar.Children.Add(assignButton);
        root.Children.Add(bottomBar);

        var border = new Border
        {
            Background = BrushFromHex("#070b12"),
            BorderBrush = BrushFromHex("#243044"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3),
            Child = root
        };

        if (stream is not null)
        {
            _tileStatusFrames[stream.Id] = [statusStrip, leftFrame, rightFrame, bottomFrame];
            _tileBorders[stream.Id] = border;
            _tileStatusTextBlocks[stream.Id] = statusTextBlock;
            _tileAlertStacks[stream.Id] = alertStack;
            _tileSurfaceHosts[stream.Id] = surfaceHost;
            _tileAudioMeterHosts[stream.Id] = audioMeterHost;
            EnsurePlaybackSurfaceChildren(surfaceHost, stream);
            RenderAlertOverlay(stream.Id, force: true);
            RenderAudioMeter(stream.Id, force: true);
        }

        return border;
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
        _alertHistoryWindow?.RefreshAlerts();
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
        var statusTextValue = pendingIssue is not null ? $"pending / {pendingIssue}" : StatusTextFor(stream, alert, isLoaded, snapshot, videoFreshness);
        var alertOverlayKey = AlertOverlayRenderKey(activeAlerts, limit: 5);
        UpdateTileStatusText(stream.Id, statusTextValue, statusColor);

        var shouldBlink = alert is not null || pendingIssue is not null;
        var renderKey = $"{statusColor}|{(shouldBlink ? "blink" : "solid")}|{alertOverlayKey}";
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
                ApplyFadeBlink(frame, shouldBlink, statusColor);
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

    private static void ApplyFadeBlink(Border frame, bool enabled, string statusColor)
    {
        var tag = enabled ? $"blink-color:{statusColor}" : $"solid:{statusColor}";
        if (Equals(frame.Tag, tag))
        {
            return;
        }

        frame.Tag = tag;
        frame.BeginAnimation(OpacityProperty, null);
        frame.Opacity = 1.0;
        var baseColor = ColorFromHex(statusColor);
        var brush = new SolidColorBrush(baseColor);
        frame.Background = brush;
        if (!enabled)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            return;
        }

        var dimColor = Color.Multiply(baseColor, 0.28f);
        dimColor.A = 255;
        var animation = new ColorAnimation
        {
            From = baseColor,
            To = dimColor,
            Duration = TimeSpan.FromMilliseconds(430),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
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

            var acknowledgeButton = OverlayActionButton("A");
            acknowledgeButton.ToolTip = "Acknowledge";
            acknowledgeButton.Click += (_, _) => SetAlertStatus(alert, "acknowledged");
            Grid.SetColumn(acknowledgeButton, 1);
            row.Children.Add(acknowledgeButton);

            var snoozeButton = OverlayActionButton("S");
            snoozeButton.ToolTip = "Snooze";
            snoozeButton.Click += (_, _) => SetAlertStatus(alert, "snoozed");
            Grid.SetColumn(snoozeButton, 2);
            row.Children.Add(snoozeButton);

            var resolveButton = OverlayActionButton("R");
            resolveButton.ToolTip = "Resolve";
            resolveButton.Click += (_, _) => SetAlertStatus(alert, "resolved");
            Grid.SetColumn(resolveButton, 3);
            row.Children.Add(resolveButton);

            var ignoreButton = OverlayActionButton("I");
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
        if (_tileAudioMeterViews.Count == 0)
        {
            return;
        }

        foreach (var (streamId, views) in _tileAudioMeterViews)
        {
            if (!_tileAudioMeterHosts.TryGetValue(streamId, out var host) ||
                host.Visibility != Visibility.Visible)
            {
                continue;
            }

            AdvanceAudioMeterViews(views, immediate: false);
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
        }

        foreach (var channel in channels)
        {
            if (existingViewLookup is null ||
                !existingViewLookup.TryGetValue(channel.Channel, out var view))
            {
                continue;
            }

            view.TargetLevelRatio = AudioMeterRatio(channel.LevelDb);
            view.TargetPeakRatio = AudioMeterRatio(channel.PeakDb);
            view.Clipping = channel.ClippingSamples > 0 || channel.PeakDb >= -1;
            view.LastLevelDb = channel.LevelDb;
            view.LastPeakDb = channel.PeakDb;
            if (force || now - view.LastTooltipAt >= TimeSpan.FromMilliseconds(250))
            {
                view.Track.ToolTip = $"CH{channel.Channel}: {channel.LevelDb:0.0} dBFS / peak {channel.PeakDb:0.0} dBFS";
                view.LastTooltipAt = now;
            }
        }

        host.Visibility = Visibility.Visible;
        AdvanceAudioMeterViews(streamId, force);
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
        var now = DateTimeOffset.UtcNow;
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

            view.DisplayedLevelRatio = immediate
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

    private const double AudioMeterHeight = 120;
    private const double AudioMeterBarWidth = 10;
    private const double AudioMeterTrackWidth = 14;
    private const double AudioMeterScaleWidth = 32;
    private const int AudioMeterMaxChannels = 16;
    private static readonly TimeSpan AudioMeterTargetRefreshInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan AudioMeterHiddenProbeInterval = TimeSpan.FromMilliseconds(250);
    private const double AudioMeterNominalFrameMs = 33;
    private const double AudioMeterMaxAnimationDeltaMs = 100;
    private const double AudioMeterLevelBlendPerFrame = 0.38;
    private const double AudioMeterPeakDecayPerFrame = 0.93;
    private static readonly Brush AudioMeterActiveBrush = CreateAudioMeterActiveBrush();

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

    private static FrameworkElement CreateAudioMeterSidePanel(AudioChannelLevel[] channels, HorizontalAlignment alignment, ICollection<AudioMeterView> views)
    {
        var meterStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignment,
            MaxWidth = 116
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

        return new Border
        {
            Background = BrushFromHex("#d9070b12"),
            BorderBrush = BrushFromHex("#66475566"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 6, 5, 5),
            Child = content
        };
    }

    private static FrameworkElement CreateAudioMeter(int channel, ICollection<AudioMeterView> views)
    {
        var trackCanvas = new Canvas
        {
            Width = AudioMeterTrackWidth,
            Height = AudioMeterHeight,
            ClipToBounds = true,
            Background = BrushFromHex("#080d16")
        };
        AddAudioMeterGrid(trackCanvas);
        var fillClip = new RectangleGeometry(new Rect(0, AudioMeterHeight - 1, AudioMeterBarWidth, 1));
        var meterFill = new Border
        {
            Width = AudioMeterBarWidth,
            Height = AudioMeterHeight,
            Background = AudioMeterActiveBrush,
            Clip = fillClip,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        Canvas.SetLeft(meterFill, 2);
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
        Canvas.SetLeft(peakMarker, 2);
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
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 3)
        };
        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = 22,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(label);
        root.Children.Add(meterTrack);
        views.Add(new AudioMeterView(channel, meterTrack, meterFill, fillClip, peakMarker, peakOffset));
        return root;
    }

    private static double AudioMeterRatio(double levelDb)
    {
        if (double.IsNaN(levelDb) || double.IsInfinity(levelDb))
        {
            return 0;
        }

        return Math.Clamp((levelDb + 60) / 60, 0.02, 1);
    }

    private static Brush CreateAudioMeterActiveBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#ef4444"), 0.00));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#ef4444"), 0.10));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#facc15"), 0.11));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#facc15"), 0.30));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#22c55e"), 0.31));
        brush.GradientStops.Add(new GradientStop(ColorFromHex("#22c55e"), 1.00));
        brush.Freeze();
        return brush;
    }

    private static void AddAudioMeterGrid(Canvas canvas)
    {
        for (var db = -60; db <= 0; db += 3)
        {
            var major = db % 6 == 0;
            var y = AudioMeterYForDb(db);
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

    private static FrameworkElement CreateAudioMeterScale(bool labelsOnRight)
    {
        var canvas = new Canvas
        {
            Width = AudioMeterScaleWidth,
            Height = AudioMeterHeight + 14,
            Margin = labelsOnRight ? new Thickness(3, 15, 0, 0) : new Thickness(0, 15, 3, 0)
        };
        for (var db = 0; db >= -60; db -= 6)
        {
            var y = AudioMeterYForDb(db);
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
                Text = db == 0 ? "0.0" : db.ToString(CultureInfo.InvariantCulture),
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

        return canvas;
    }

    private static double AudioMeterYForDb(double db)
    {
        var ratio = Math.Clamp((db + 60) / 60, 0, 1);
        return AudioMeterHeight - AudioMeterHeight * ratio;
    }

    private static Button OverlayActionButton(string text)
    {
        return new Button
        {
            Content = text,
            Width = 22,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 10,
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
        LocalConfigStore.Save(_config);
        UpdateFooter($"Saved {LocalConfigStore.DefaultConfigPath}");
    }

    internal void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
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
            EnsureStandardLayouts();
            LocalConfigStore.Save(_config);
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
        LocalConfigStore.Save(_config);
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
        LocalConfigStore.Save(_config);
        SetTextIfChanged(ModeText, _config.ServerSyncEnabled ? $"Server sync: {_config.ServerUrl}" : "Standalone local mode");
        SetTextIfChanged(ServerSyncStatusText, _config.ServerSyncEnabled ? "server sync: enabled" : "server sync: disabled");
        SetTextIfChanged(ServerClientSummaryText, _config.ServerSyncEnabled ? "server clients: pending" : "server clients: unavailable");
        _ = SyncWithServerAsync(force: true);
    }

    private async void StartPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        await StartPlaybackForActiveLayoutStreamsAsync("Starting playback", restartLoaded: true);
    }

    private void QueueInitialPlaybackStart()
    {
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            await StartPlaybackForActiveLayoutStreamsAsync("Auto-starting receivers", restartLoaded: false);
        }), DispatcherPriority.ContextIdle);
    }

    private async System.Threading.Tasks.Task StartPlaybackForActiveLayoutStreamsAsync(string actionLabel, bool restartLoaded)
    {
        if (_playbackStartInFlight)
        {
            return;
        }

        var streams = ActiveLayoutStreams().Where(stream => stream.Enabled).ToList();
        var started = 0;
        var skipped = 0;
        var failed = 0;
        string? lastError = null;

        _playbackStartInFlight = true;
        StartPlaybackButton.IsEnabled = false;
        StopPlaybackButton.IsEnabled = false;
        _playbackTimer.Stop();
        UpdateFooter($"{actionLabel} for {streams.Count} layout streams...");

        try
        {
            foreach (var stream in streams)
            {
                if (!CanPlayStream(stream))
                {
                    skipped++;
                    continue;
                }

                if (!restartLoaded && IsPlaybackLoaded(stream.Id))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await StartStreamPlaybackAsync(stream);
                    started++;
                    UpdateFooter($"{actionLabel}: {started} started, {skipped} skipped, {failed} failed...");
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    failed++;
                    AppendDesktopLog($"Start playback failed for {stream.Id}: {ex}");
                }
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
            _playbackTimer.Start();
        }
    }

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackStartInFlight)
        {
            UpdateFooter("Playback start is still in progress. Wait for it to finish before stopping.");
            return;
        }

        var stopped = _players.Count;
        stopped += _ffmpegPlaybackSessions.Count;
        StopAllPlayback();
        RefreshPlaybackSurfaces();
        UpdateAllTileChrome();
        UpdatePlaybackSummary();
        UpdateFooter($"Playback stopped: {stopped} streams.");
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow.Owner ??= this;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenAlertHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _alertHistoryWindow ??= new AlertHistoryWindow(this) { Owner = this };
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
        if (IsPlaybackLoaded(stream.Id))
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

        LocalConfigStore.Save(_config);
        StreamsList.Items.Refresh();
        RenderAlerts();
        RenderTiles();
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
        LocalConfigStore.Save(_config);
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

        LocalConfigStore.Save(_config);
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

            LocalConfigStore.Save(_config);
            RenderTiles();
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
            var path = Path.Combine(LocalConfigStore.LogDirectory, "desktop.log");
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
            Background = Brushes.Black
        };

        SetPlaybackSurface(surface, stream);
        return surface;
    }

    private void EnsurePlaybackSurfaceChildren(Grid surface, StreamConfig stream)
    {
        if (!_tileVideoViews.TryGetValue(stream.Id, out var view))
        {
            view = new VideoView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _tileVideoViews[stream.Id] = view;
            surface.Children.Add(view);
        }
        else if (!surface.Children.Contains(view))
        {
            surface.Children.Add(view);
        }

        if (!_tileFfmpegImages.TryGetValue(stream.Id, out var image))
        {
            image = new Image
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Hidden
            };
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
        var snapshotAvailable = _playbackSnapshots.TryGetValue(stream.Id, out var snapshot);
        var showVlcVideo = hasPlayer &&
            snapshotAvailable &&
            HasPresentedVlcVideo(stream.Id, snapshot!);
        var showFfmpegVideo = hasFfmpegSession &&
            snapshotAvailable &&
            string.Equals(snapshot!.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            ffmpegSession!.HasPresentedFrame;
        var surfaceKey = string.Join("|",
            hasPlayer ? "vlc" : "no-vlc",
            hasPlayer ? player!.GetHashCode().ToString(CultureInfo.InvariantCulture) : "no-player-ref",
            hasFfmpegSession ? "ffmpeg" : "no-ffmpeg",
            hasFfmpegSession ? ffmpegSession!.GetHashCode().ToString(CultureInfo.InvariantCulture) : "no-ffmpeg-ref",
            snapshotAvailable ? snapshot!.State : "no-snapshot",
            showVlcVideo ? "has-vlc-presented-video" : "no-vlc-presented-video",
            showVlcVideo ? "show-vlc" : "hide-vlc",
            showFfmpegVideo ? "show-ffmpeg" : "hide-ffmpeg",
            hasFfmpegSession && ffmpegSession!.HasPresentedFrame ? "has-presented-frame" : "no-presented-frame");
        if (_tileSurfaceRenderKeys.TryGetValue(stream.Id, out var previousSurfaceKey) &&
            string.Equals(previousSurfaceKey, surfaceKey, StringComparison.Ordinal))
        {
            return;
        }

        _tileSurfaceRenderKeys[stream.Id] = surfaceKey;
        if (_tileVideoViews.TryGetValue(stream.Id, out var videoView))
        {
            var desiredPlayer = hasPlayer ? player : null;
            if (!ReferenceEquals(videoView.MediaPlayer, desiredPlayer))
            {
                videoView.MediaPlayer = desiredPlayer;
            }

            videoView.Visibility = showVlcVideo ? Visibility.Visible : Visibility.Hidden;
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
            SetTextIfChanged(placeholder, loaded ? "Playback starting" : "Playback stopped");
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
        LocalConfigStore.Save(_config);
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
        LocalConfigStore.Save(_config);
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
        LocalConfigStore.Save(_config);
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
        LocalConfigStore.Save(_config);
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
        LocalConfigStore.Save(_config);
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
        foreach (var tile in layout.Tiles)
        {
            if (string.IsNullOrWhiteSpace(tile.StreamId) ||
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
        return layout?.Tiles.Any(tile => string.Equals(tile.StreamId, streamId, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async System.Threading.Tasks.Task StartStreamPlaybackAsync(StreamConfig stream)
    {
        if (ShouldUseFfmpegPlayback(stream))
        {
            StartFfmpegStreamPlayback(stream);
            await Dispatcher.Yield(DispatcherPriority.Background);
            return;
        }

        var (libVlc, player, audioMonitor) = PrepareStreamPlayback(stream);
        var media = new Media(libVlc, stream.InputUrl, FromType.FromLocation);
        AddLowLatencyVlcMediaOptions(media);
        var streamId = stream.Id;
        var streamName = stream.Name;

        _ = System.Threading.Tasks.Task.Run(() => StartLibVlcPlayback(streamId, streamName, player, audioMonitor, media));

        if (stream.AnalysisEnabled)
        {
            QueueVideoMonitorStart(stream);
        }

        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private bool ShouldUseFfmpegPlayback(StreamConfig stream)
    {
        if (string.IsNullOrWhiteSpace(FindFfmpeg()))
        {
            return false;
        }

        return stream.AnalysisEnabled ||
            stream.InputProtocol.StartsWith("srt", StringComparison.OrdinalIgnoreCase) ||
            stream.InputUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
    }

    private void StartFfmpegStreamPlayback(StreamConfig stream)
    {
        _playbackRestartAfter.Remove(stream.Id);
        StopStreamPlayback(stream.Id);
        ClearPlaybackAnalysis(stream.Id);
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg was not found.");
        var profile = AlertProfileFor(stream);
        FfmpegPlaybackSession? session = null;
        session = new FfmpegPlaybackSession(
            ffmpeg,
            stream.InputUrl,
            stream.InputProtocol,
            Dispatcher,
            profile.BlackPixelThreshold,
            profile.FreezeDifferenceThreshold,
            profile.BlackThreshold,
            profile.BlackPixelRatioThreshold,
            () => QueueImmediateVideoAlertEvaluation(stream.Id),
            () =>
            {
                if (session is not null)
                {
                    OnFirstFfmpegFramePresented(stream, session);
                }
            });

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
        AppendDesktopLog($"FFmpeg playback started for {stream.Id}: {MaskStreamUrl(stream.InputUrl)}");
    }

    private void OnFirstFfmpegFramePresented(StreamConfig stream, FfmpegPlaybackSession session)
    {
        var snapshot = session.CurrentPlaybackSnapshot();
        _playbackSnapshots[stream.Id] = snapshot;
        TrackPlaybackProgress(stream.Id, snapshot);
        RefreshPlaybackSurface(stream);
        UpdateTileChrome(stream);
        UpdatePlaybackSummary();
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
        StopStreamPlayback(stream.Id);
        ClearPlaybackAnalysis(stream.Id);
        var libVlc = EnsureLibVlc();
        var player = new VlcMediaPlayer(libVlc)
        {
            Mute = _config.GlobalMute || !stream.AudioMonitorEnabled
        };
        AudioLevelMonitor? audioMonitor = null;
        if (stream.AudioMonitorEnabled)
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
        UpdateTileChrome(stream);
        return (libVlc, player, audioMonitor);
    }

    private void CleanupFailedPlaybackStart(StreamConfig stream, VlcMediaPlayer player, AudioLevelMonitor? audioMonitor)
    {
        _players.Remove(stream.Id);
        _audioMonitors.Remove(stream.Id);
        _playbackSnapshots[stream.Id] = EmptyPlaybackSnapshot("stopped");
        RefreshPlaybackSurface(stream);
        UpdateTileChrome(stream);
        player.Dispose();
        audioMonitor?.Dispose();
    }

    private void StopStreamPlayback(string streamId)
    {
        _playbackRestartAfter.Remove(streamId);
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
        _playbackRestartAfter.Clear();
        var streamIds = new HashSet<string>(_players.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var streamId in _ffmpegPlaybackSessions.Keys)
        {
            streamIds.Add(streamId);
        }

        foreach (var streamId in streamIds)
        {
            StopStreamPlayback(streamId);
        }
    }

    private void StartAudioMonitor(StreamConfig stream)
    {
        if (_ffmpegPlaybackSessions.ContainsKey(stream.Id))
        {
            return;
        }

        if (_audioMonitors.ContainsKey(stream.Id) || !CanPlayStream(stream))
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
            return;
        }

        if (_ffmpegPlaybackSessions.TryGetValue(streamId, out var session) && ReferenceEquals(session, monitor))
        {
            return;
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

    private bool QueueSharedAnalysisPlaybackRestart(StreamConfig stream)
    {
        if (!stream.AnalysisEnabled ||
            !_players.ContainsKey(stream.Id) ||
            !ShouldUseFfmpegPlayback(stream))
        {
            return false;
        }

        AppendDesktopLog($"Restarting {stream.Id} with shared FFmpeg playback/analysis to avoid a second source connection.");
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await StartStreamPlaybackAsync(stream);
            }
            catch (Exception ex)
            {
                AppendDesktopLog($"Shared analysis playback restart failed for {stream.Id}: {ex}");
                UpdateFooter($"Shared analysis restart failed for {stream.Name}: {ex.Message}");
            }
        }), DispatcherPriority.Background);
        return true;
    }

    private void ApplyMuteState()
    {
        foreach (var (streamId, player) in _players)
        {
            var stream = _config.Streams.FirstOrDefault(stream => stream.Id == streamId);
            player.Mute = _config.GlobalMute || stream?.AudioMonitorEnabled == false;
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
        SetTextIfChanged(PlaybackSummaryText, $"{active} active / {_players.Count + _ffmpegPlaybackSessions.Count} loaded / {layoutStreams.Count} layout streams / {errors} errors{videoFreshness}");
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

    private void AssignTileToStream(int tileIndex, string streamId)
    {
        AssignStreamToTile(streamId, tileIndex);
        LocalConfigStore.Save(_config);
        RenderTiles();
        RefreshPlaybackSurfaces();
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
            return "#6b7280";
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
            return "standby / playback stopped";
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

        return IsPlaybackLoaded(stream.Id) ? "playback: starting" : "playback: stopped";
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
            LocalConfigStore.Save(_config);
            RenderAlerts();
            UpdateAllTileChrome(activeLayoutStreams);
        }

        if (runSlowRefresh)
        {
            _ = SyncWithServerAsync(force: alertStateChanged);
        }
    }

    private void QueueImmediateVideoAlertEvaluation(string streamId)
    {
        lock (_immediateVideoAlertEvaluationSync)
        {
            if (!_pendingImmediateVideoAlertEvaluations.Add(streamId))
            {
                return;
            }
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
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

            LocalConfigStore.Save(_config);
            RenderAlerts();
            UpdateSingleTileChrome(activeLayoutStreams, streamId);
            _ = SyncWithServerAsync(force: true);
        }), DispatcherPriority.Normal);
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

            if (!stream.Enabled || !stream.AnalysisEnabled || !IsPlaybackLoaded(stream.Id))
            {
                continue;
            }

            var profile = AlertProfileFor(stream);
            if (!_playbackSnapshots.TryGetValue(stream.Id, out var snapshot))
            {
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
            if (analysisUnavailable && IsAnalysisEnded(stream.Id))
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
                changed |= ResolveActiveAlert(stream.Id, "video.black");
                changed |= ResolveActiveAlert(stream.Id, "video.freeze");
            }
            else
            {
                ClearIssueSince(stream.Id, "video.no_signal");
                ClearIssueSince(stream.Id, "video.opening");
                changed |= ResolveActiveAlert(stream.Id, "video.no_signal");
            }

            if (!decodeError && !noSignal && IsBlackVideo(stream.Id, profile))
            {
                MarkIssueSince(stream.Id, "video.black");
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
        UpdateContinuousAlertSound();
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
            var stream = _config.Streams.FirstOrDefault(stream => stream.Id == alert.StreamId);
            if (stream is not null)
            {
                UpdateContinuousAlertSound();
            }

            changed = true;
        }

        return changed;
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
                _config.ClientVersion,
                "desktop-windows",
                new Dictionary<string, bool>
                {
                    ["standalone_config"] = true,
                    ["playback"] = true,
                    ["local_alerts"] = true
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
                LocalConfigStore.Save(_config);
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
                    IsPlaybackLoaded(existing.Id) &&
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

            foreach (var removedID in _players.Keys.Concat(_ffmpegPlaybackSessions.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Where(streamID => merged.All(stream => !string.Equals(stream.Id, streamID, StringComparison.OrdinalIgnoreCase))).ToList())
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
        foreach (var alert in _config.AlertHistory.Where(alert =>
            string.Equals(alert.StreamId, streamId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(alert.Type, type, StringComparison.OrdinalIgnoreCase) &&
            ActiveAlertStatuses.Contains(alert.Status)))
        {
            alert.Status = "resolved";
            alert.SnoozedUntil = null;
            alert.UpdatedAt = DateTimeOffset.UtcNow;
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
        return _players.Count > 0 || _ffmpegPlaybackSessions.Count > 0;
    }

    private void SchedulePlaybackRestartIfNeeded(string streamId, PlaybackSnapshot snapshot)
    {
        if (!IsRecoverablePlaybackState(snapshot.State))
        {
            _playbackRestartAfter.Remove(streamId);
            return;
        }

        if (_playbackStartInFlight)
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
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_playbackRestartAfter.TryGetValue(streamId, out var restartAfter))
        {
            var profile = AlertProfileFor(stream);
            _playbackRestartAfter[streamId] = now.AddSeconds(Math.Max(2, profile.InputTimeoutSec));
            return;
        }

        if (now < restartAfter)
        {
            return;
        }

        _playbackRestartAfter[streamId] = now.AddSeconds(Math.Max(5, AlertProfileFor(stream).InputTimeoutSec));
        var restartReason = snapshot.State;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            await RestartSingleStreamPlaybackAsync(streamId, restartReason);
        }), DispatcherPriority.Background);
    }

    private async System.Threading.Tasks.Task RestartSingleStreamPlaybackAsync(string streamId, string reason)
    {
        if (_playbackStartInFlight)
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
            return;
        }

        try
        {
            AppendDesktopLog($"Restarting receiver for {stream.Id} after playback state '{reason}'.");
            await StartStreamPlaybackAsync(stream);
            _playbackRestartAfter.Remove(streamId);
            UpdateFooter($"Receiver restarted for {stream.Name}.");
        }
        catch (Exception ex)
        {
            _playbackRestartAfter[streamId] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, AlertProfileFor(stream).InputTimeoutSec));
            AppendDesktopLog($"Receiver restart failed for {stream.Id}: {ex}");
            UpdateFooter($"Receiver restart failed for {stream.Name}: {ex.Message}");
        }
    }

    private bool IsAnalysisEnded(string streamId)
    {
        if (!_videoMonitors.TryGetValue(streamId, out var monitor))
        {
            return false;
        }

        return monitor.State.Equals("ended", StringComparison.OrdinalIgnoreCase) ||
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
            "stopped" or "ended" => true,
            _ => false
        };
    }

    private static bool IsMonitoringPlaybackState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "starting" or "opening" or "buffering" or "rendering" or "playing" => true,
            _ => false
        };
    }

    private static bool IsRecoverablePlaybackState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "waiting" or "stopped" or "ended" or "error" => true,
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
        CompositionTarget.Rendering -= AudioMeterCompositionTarget_Rendering;
        _playbackTimer.Stop();
        _audioMeterTimer.Stop();
        StopContinuousAlertSound();
        StopAllPlayback();
        _httpClient.Dispose();
        _libVlc?.Dispose();
        base.OnClosed(e);
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
    AudioChannelLevel[] Channels);

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
    private const int Width = 960;
    private const int Height = 540;
    private const int BytesPerPixel = 3;
    private const int Stride = Width * BytesPerPixel;
    private const int FrameBytes = Stride * Height;
    private const double TargetFps = 30;
    private const double SilenceFloorDb = -120;
    private const int AnalysisSampleStrideX = 8;
    private const int AnalysisSampleStrideY = 8;
    private const int AnalysisSampleColumns = (Width + AnalysisSampleStrideX - 1) / AnalysisSampleStrideX;
    private const int AnalysisSampleRows = (Height + AnalysisSampleStrideY - 1) / AnalysisSampleStrideY;
    private const int AnalysisSampleCount = AnalysisSampleColumns * AnalysisSampleRows;
    private const int MaxAudioStatsChannels = 16;
    private static readonly RegexOptions AudioStatsRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan AudioStatsRegexTimeout = TimeSpan.FromMilliseconds(100);
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
    private static readonly Regex LegacyRmsLogRegex = new(
        @"RMS level dB:\s*(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);
    private static readonly Regex LegacyPeakLogRegex = new(
        @"Peak level dB:\s*(-?inf|[-+]?\d+(?:\.\d+)?)",
        AudioStatsRegexOptions,
        AudioStatsRegexTimeout);

    private readonly string _ffmpegPath;
    private readonly string _inputUrl;
    private readonly string _protocol;
    private readonly List<string> _inputCandidates;
    private readonly Dispatcher _dispatcher;
    private readonly Action _blackFrameDetected;
    private readonly Action _firstFramePresented;
    private readonly object _sync = new();
    private readonly object _bitmapFrameSync = new();
    private readonly double[] _previousSampleLuma = new double[AnalysisSampleCount];
    private byte[] _bitmapWriteFrameBuffer = new byte[FrameBytes];
    private byte[] _bitmapPendingFrameBuffer = new byte[FrameBytes];
    private long _bitmapWriteFrameReadTicks;
    private long _bitmapPendingFrameReadTicks;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private VideoLumaSnapshot? _snapshot;
    private AudioLevelSnapshot? _audioSnapshot;
    private long _framesRead;
    private long _bytesRead;
    private int _stableFrames;
    private bool _hasPreviousFrame;
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
    private bool _disposed;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private double _lastParsedRmsDb = SilenceFloorDb;
    private double _lastParsedPeakDb = SilenceFloorDb;
    private int _parsedAudioChannelMask;
    private int _parsedAudioChannelRmsMask;
    private int _parsedAudioChannelPeakMask;
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
    {
        _ffmpegPath = ffmpegPath;
        _inputUrl = inputUrl;
        _protocol = protocol;
        _inputCandidates = BuildInputCandidates(inputUrl, protocol);
        _dispatcher = dispatcher;
        _blackFrameDetected = blackFrameDetected;
        _firstFramePresented = firstFramePresented ?? (() => { });
        DarkPixelThreshold = Math.Clamp(darkPixelThreshold, 0.0, 1.0);
        StabilityDifferenceThreshold = Math.Clamp(stabilityDifferenceThreshold, 0.0, 1.0);
        BlackLumaThreshold = Math.Clamp(blackLumaThreshold, 0.0, 1.0);
        BlackPixelRatioThreshold = Math.Clamp(blackPixelRatioThreshold, 0.0, 1.0);
        Bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgr24, null);
    }

    public WriteableBitmap Bitmap { get; }

    public bool HasFrame => _framesRead > 0;

    public bool HasPresentedFrame => Interlocked.Read(ref _bitmapFramesPresented) > 0;

    public long BitmapFramesQueued => Interlocked.Read(ref _bitmapFramesQueued);

    public long BitmapFramesDropped => Interlocked.Read(ref _bitmapFramesDropped);

    public long BitmapFramesCoalesced => Interlocked.Read(ref _bitmapFramesCoalesced);

    public long BitmapFramesPresented => Interlocked.Read(ref _bitmapFramesPresented);

    public DateTimeOffset LastFrameReadAt => UtcTicksToDateTimeOffset(Interlocked.Read(ref _lastFrameReadTicks));

    public DateTimeOffset LastFramePresentedAt => UtcTicksToDateTimeOffset(Interlocked.Read(ref _lastFramePresentedTicks));

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

    public string State => _state;

    public string LastError => _lastError;

    public double DarkPixelThreshold { get; set; }

    public double StabilityDifferenceThreshold { get; set; }

    public double BlackLumaThreshold { get; set; }

    public double BlackPixelRatioThreshold { get; set; }

    public void Start()
    {
        if (_process is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _process = StartProcess(_inputCandidates[_candidateIndex]);
        _state = "opening";
        _ = System.Threading.Tasks.Task.Run(() => DrainErrorsAsync(_process, _cts.Token));
        _ = System.Threading.Tasks.Task.Run(() => ReadFramesAsync(_process, _cts.Token));
    }

    private Process StartProcess(string inputUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddCommonFfmpegInputArguments(startInfo.ArgumentList, _protocol, inputUrl);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputUrl);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"fps={TargetFps.ToString("0.###", CultureInfo.InvariantCulture)},scale={Width}:{Height},format=bgr24");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0?");
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add("astats=metadata=1:reset=1,ametadata=print:file='pipe\\:2'");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg playback.");
    }

    private static List<string> BuildInputCandidates(string inputUrl, string protocol)
    {
        if (!inputUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ||
            inputUrl.Contains("mode=", StringComparison.OrdinalIgnoreCase))
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
            var framesPresented = Interlocked.Read(ref _bitmapFramesPresented);
            if (string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase) && framesPresented == 0)
            {
                state = "rendering";
            }

            return new PlaybackSnapshot(
                state,
                string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase) ? (float)TargetFps : 0,
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
            if (_audioSnapshot is null)
            {
                snapshot = new AudioLevelSnapshot(SilenceFloorDb, SilenceFloorDb, 0, DateTimeOffset.MinValue, []);
                return false;
            }

            snapshot = _audioSnapshot;
            return true;
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
        if (string.Equals(protocol, "rtsp", StringComparison.OrdinalIgnoreCase) ||
            inputUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-rtsp_transport");
            arguments.Add("tcp");
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
                    if (!TryParseAudioStatsLine(line))
                    {
                        if (!IsBenignFfmpegLogLine(line))
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

    private bool TryParseAudioStatsLine(string line)
    {
        var channelRmsMetadata = ChannelRmsMetadataRegex.Match(line);
        if (channelRmsMetadata.Success)
        {
            var channel = Math.Clamp(int.Parse(channelRmsMetadata.Groups[1].Value, CultureInfo.InvariantCulture), 1, MaxAudioStatsChannels);
            var channelBit = RememberParsedAudioChannel(channel);
            _parsedAudioChannelRmsMask |= channelBit;
            _lastParsedChannelRmsDb[channel - 1] = ParseFfmpegDb(channelRmsMetadata.Groups[2].Value);
            UpdateAudioSnapshot();
            return true;
        }

        var channelPeakMetadata = ChannelPeakMetadataRegex.Match(line);
        if (channelPeakMetadata.Success)
        {
            var channel = Math.Clamp(int.Parse(channelPeakMetadata.Groups[1].Value, CultureInfo.InvariantCulture), 1, MaxAudioStatsChannels);
            var channelBit = RememberParsedAudioChannel(channel);
            _parsedAudioChannelPeakMask |= channelBit;
            _lastParsedChannelPeakDb[channel - 1] = ParseFfmpegDb(channelPeakMetadata.Groups[2].Value);
            UpdateAudioSnapshot();
            return true;
        }

        var rmsMetadata = OverallRmsMetadataRegex.Match(line);
        if (rmsMetadata.Success)
        {
            _lastParsedRmsDb = ParseFfmpegDb(rmsMetadata.Groups[1].Value);
            UpdateAudioSnapshot();
            return true;
        }

        var peakMetadata = OverallPeakMetadataRegex.Match(line);
        if (peakMetadata.Success)
        {
            _lastParsedPeakDb = ParseFfmpegDb(peakMetadata.Groups[1].Value);
            UpdateAudioSnapshot();
            return true;
        }

        if (line.StartsWith("frame:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rms = LegacyRmsLogRegex.Match(line);
        if (rms.Success)
        {
            _lastParsedRmsDb = ParseFfmpegDb(rms.Groups[1].Value);
            UpdateAudioSnapshot();
            return true;
        }

        var peak = LegacyPeakLogRegex.Match(line);
        if (peak.Success)
        {
            _lastParsedPeakDb = ParseFfmpegDb(peak.Groups[1].Value);
            UpdateAudioSnapshot();
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
            line.Contains("[Parsed_astats_", StringComparison.OrdinalIgnoreCase);
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

    private void UpdateAudioSnapshot()
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

            _audioSnapshot = new AudioLevelSnapshot(_lastParsedRmsDb, _lastParsedPeakDb, clippingSamples, DateTimeOffset.UtcNow, channels);
        }
    }

    private async System.Threading.Tasks.Task ReadFramesAsync(Process process, CancellationToken token)
    {
        var frame = new byte[FrameBytes];
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    if (_framesRead == 0 && TryStartNextCandidate(ref process, token))
                    {
                        continue;
                    }

                    _state = "ended";
                    return;
                }

                var offset = 0;
                while (offset < FrameBytes)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(frame.AsMemory(offset, FrameBytes - offset), token);
                    if (read <= 0)
                    {
                        if (_framesRead == 0 && TryStartNextCandidate(ref process, token))
                        {
                            offset = 0;
                            continue;
                        }

                        _state = process.HasExited ? "ended" : "waiting";
                        return;
                    }

                    offset += read;
                }

                var frameReadTicks = DateTime.UtcNow.Ticks;
                Interlocked.Exchange(ref _lastFrameReadTicks, frameReadTicks);
                UpdateSnapshot(frame);
                _state = "playing";
                QueueBitmapFrame(frame, frameReadTicks);
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

    private void QueueBitmapFrame(byte[] frame, long frameReadTicks)
    {
        var shouldSchedule = false;
        lock (_bitmapFrameSync)
        {
            if (!_bitmapWriteScheduled)
            {
                Buffer.BlockCopy(frame, 0, _bitmapWriteFrameBuffer, 0, FrameBytes);
                _bitmapWriteFrameReadTicks = frameReadTicks;
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

                Buffer.BlockCopy(frame, 0, _bitmapPendingFrameBuffer, 0, FrameBytes);
                _bitmapPendingFrameReadTicks = frameReadTicks;
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
                Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), _bitmapWriteFrameBuffer, Stride, 0);
                var presentedTicks = DateTime.UtcNow.Ticks;
                var presentedFrameReadTicks = _bitmapWriteFrameReadTicks;
                Interlocked.Exchange(ref _lastFramePresentedTicks, presentedTicks);
                Interlocked.Exchange(ref _lastPresentedFrameReadTicks, presentedFrameReadTicks);
                var presentedFrames = Interlocked.Increment(ref _bitmapFramesPresented);
                if (presentedFrames == 1)
                {
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

    private bool TryStartNextCandidate(ref Process process, CancellationToken token)
    {
        if (_candidateIndex + 1 >= _inputCandidates.Count || token.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            process.Dispose();
        }
        catch
        {
            // Best effort cleanup before retrying another SRT mode.
        }

        _candidateIndex++;
        var nextInput = _inputCandidates[_candidateIndex];
        _lastError = $"Retrying ffmpeg input as {MainWindow.MaskStreamUrl(nextInput)}";
        _state = "opening";
        process = StartProcess(nextInput);
        _process = process;
        var retryProcess = process;
        _ = System.Threading.Tasks.Task.Run(() => DrainErrorsAsync(retryProcess, token));
        return true;
    }

    private void UpdateSnapshot(byte[] frame)
    {
        double total = 0;
        double diffTotal = 0;
        var darkPixels = 0;
        var sampleCount = 0;
        var sampleIndex = 0;
        for (var y = 0; y < Height; y += AnalysisSampleStrideY)
        {
            var rowStart = y * Stride;
            for (var x = 0; x < Width; x += AnalysisSampleStrideX)
            {
                var index = rowStart + x * BytesPerPixel;
                var blue = frame[index];
                var green = frame[index + 1];
                var red = frame[index + 2];
                var luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                total += luma;
                sampleCount++;
                if (luma / 255 <= DarkPixelThreshold)
                {
                    darkPixels++;
                }

                if (_hasPreviousFrame)
                {
                    diffTotal += Math.Abs(luma - _previousSampleLuma[sampleIndex]);
                }

                _previousSampleLuma[sampleIndex] = luma;
                sampleIndex++;
            }
        }

        sampleCount = Math.Max(1, sampleCount);
        var difference = _hasPreviousFrame ? diffTotal / sampleCount / 255 : 1;
        _stableFrames = difference <= StabilityDifferenceThreshold ? _stableFrames + 1 : 0;
        _hasPreviousFrame = true;
        VideoLumaSnapshot snapshot;
        lock (_sync)
        {
            _framesRead++;
            _bytesRead += frame.Length;
            snapshot = new VideoLumaSnapshot(
                total / sampleCount / 255,
                darkPixels / (double)sampleCount,
                difference,
                _stableFrames,
                DateTimeOffset.UtcNow);
            _snapshot = snapshot;
        }

        if (snapshot.Luma <= BlackLumaThreshold && snapshot.DarkRatio >= BlackPixelRatioThreshold)
        {
            _blackFrameDetected();
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

internal sealed class FfmpegVideoAnalysisMonitor : IVideoAnalysisMonitor
{
    private const int Width = 160;
    private const int Height = 90;
    private const int BytesPerPixel = 3;
    private const int FrameBytes = Width * Height * BytesPerPixel;

    private readonly string _ffmpegPath;
    private readonly string _inputUrl;
    private readonly string _protocol;
    private readonly object _sync = new();
    private readonly byte[] _previousBuffer = new byte[FrameBytes];
    private Process? _process;
    private CancellationTokenSource? _cts;
    private VideoLumaSnapshot? _snapshot;
    private int _stableFrames;
    private bool _hasPreviousFrame;
    private string _state = "stopped";
    private string _lastError = "";
    private bool _disposed;

    public FfmpegVideoAnalysisMonitor(string ffmpegPath, string inputUrl, string protocol, double darkPixelThreshold, double stabilityDifferenceThreshold)
    {
        _ffmpegPath = ffmpegPath;
        _inputUrl = inputUrl;
        _protocol = protocol;
        DarkPixelThreshold = Math.Clamp(darkPixelThreshold, 0.0, 1.0);
        StabilityDifferenceThreshold = Math.Clamp(stabilityDifferenceThreshold, 0.0, 1.0);
    }

    public string Kind => "ffmpeg";

    public string State => _state;

    public string LastError => _lastError;

    public double DarkPixelThreshold { get; set; }

    public double StabilityDifferenceThreshold { get; set; }

    public void Start()
    {
        if (_process is not null)
        {
            return;
        }

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
        if (string.Equals(_protocol, "rtsp", StringComparison.OrdinalIgnoreCase) ||
            _inputUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-rtsp_transport");
            startInfo.ArgumentList.Add("tcp");
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(_inputUrl);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"fps=2,scale={Width}:{Height},format=rgb24");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _state = "opening";
        _ = System.Threading.Tasks.Task.Run(() => DrainErrorsAsync(_process, _cts.Token));
        _ = System.Threading.Tasks.Task.Run(() => ReadFramesAsync(_process, _cts.Token));
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

    private readonly object _sync = new();
    private readonly VlcMediaPlayer.LibVLCAudioSetupCb _setupCallback;
    private readonly VlcMediaPlayer.LibVLCAudioCleanupCb _cleanupCallback;
    private readonly VlcMediaPlayer.LibVLCAudioPlayCb _playCallback;
    private readonly VlcMediaPlayer.LibVLCAudioPauseCb _pauseCallback;
    private readonly VlcMediaPlayer.LibVLCAudioResumeCb _resumeCallback;
    private readonly VlcMediaPlayer.LibVLCAudioFlushCb _flushCallback;
    private readonly VlcMediaPlayer.LibVLCAudioDrainCb _drainCallback;
    private VlcMediaPlayer? _player;
    private AudioLevelSnapshot? _snapshot;
    private int _channelCount = DefaultChannelCount;
    private bool _disposed;

    public AudioLevelMonitor()
    {
        _setupCallback = OnAudioSetup;
        _cleanupCallback = _ => { };
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
        player.SetAudioFormatCallback(_setupCallback, _cleanupCallback);
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

    private int OnAudioSetup(ref IntPtr opaque, ref IntPtr format, ref uint rate, ref uint channels)
    {
        try
        {
            if (format != IntPtr.Zero)
            {
                Marshal.Copy(Encoding.ASCII.GetBytes("S16N"), 0, format, 4);
            }

            rate = rate == 0 ? SampleRate : rate;
            var negotiatedChannels = channels == 0 ? DefaultChannelCount : (int)Math.Min(channels, MaxChannelCount);
            _channelCount = Math.Clamp(negotiatedChannels, 1, MaxChannelCount);
            channels = (uint)_channelCount;
            return 0;
        }
        catch
        {
            _channelCount = DefaultChannelCount;
            channels = (uint)DefaultChannelCount;
            rate = SampleRate;
            return 0;
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

            var buffer = ArrayPool<short>.Shared.Rent(sampleCount);
            try
            {
                Marshal.Copy(samples, buffer, 0, sampleCount);
                var clippingAmplitude = short.MaxValue * Math.Pow(10, DefaultClippingThresholdDb / 20.0);

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
                    if (abs >= clippingAmplitude)
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
