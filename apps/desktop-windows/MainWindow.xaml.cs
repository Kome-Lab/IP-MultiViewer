using System;
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
    private static readonly HashSet<string> ActiveAlertStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "open",
        "acknowledged",
        "snoozed"
    };

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
    private readonly Dictionary<string, List<Border>> _tileStatusFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _tileBorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _tileStatusTextBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StackPanel> _tileAlertStacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _tileSurfaceHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoView> _tileVideoViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _tileFfmpegImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _tileSurfacePlaceholders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tileRenderedAlertKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new();
    private bool _isLoading;
    private bool _blinkPhase;
    private LibVLC? _libVlc;
    private readonly DispatcherTimer _playbackTimer;
    private SettingsWindow _settingsWindow = null!;
    private AlertHistoryWindow? _alertHistoryWindow;
    private DateTimeOffset _lastServerSyncAt = DateTimeOffset.MinValue;
    private bool _serverSyncInFlight;
    private bool _playbackStartInFlight;
    private const double TileFrameThickness = 3;
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
    private TextBox BlackThresholdText => _settingsWindow.BlackThresholdText;
    private TextBox BlackPixelThresholdText => _settingsWindow.BlackPixelThresholdText;
    private TextBox BlackPixelRatioText => _settingsWindow.BlackPixelRatioText;
    private TextBox MinBlackDurationText => _settingsWindow.MinBlackDurationText;
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
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _playbackTimer.Tick += (_, _) => RefreshPlaybackMetadata();
        InitializeComponent();
        _settingsWindow = new SettingsWindow(this);
        LoadConfig();
        _playbackTimer.Start();
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
        ModeText.Text = string.IsNullOrWhiteSpace(_config.ServerUrl)
            ? "Standalone local mode"
            : $"Server sync: {_config.ServerUrl}";
        ServerSyncStatusText.Text = _config.ServerSyncEnabled ? "server sync: pending" : "server sync: disabled";
        ServerClientSummaryText.Text = _config.ServerSyncEnabled ? "server clients: pending" : "server clients: unavailable";
        ConfigPathText.Text = LocalConfigStore.DefaultConfigPath;
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
        _libVlc = new LibVLC("--no-video-title-show", "--network-caching=300");
        return _libVlc;
    }

    private void RenderTiles()
    {
        TileGrid.Children.Clear();
        _tileStatusFrames.Clear();
        _tileBorders.Clear();
        _tileStatusTextBlocks.Clear();
        _tileAlertStacks.Clear();
        _tileSurfaceHosts.Clear();
        _tileVideoViews.Clear();
        _tileFfmpegImages.Clear();
        _tileSurfacePlaceholders.Clear();
        _tileRenderedAlertKeys.Clear();

        var layout = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
            ?? _config.Layouts.FirstOrDefault();
        var size = GridSizeFor(layout?.Kind);
        TileGrid.Rows = size;
        TileGrid.Columns = size;

        for (var index = 0; index < size * size; index++)
        {
            var tile = layout?.Tiles.FirstOrDefault(tile => tile.Index == index);
            var stream = _config.Streams.FirstOrDefault(stream => stream.Id == tile?.StreamId);
            TileGrid.Children.Add(CreateTile(stream, index));
        }
    }

    private Border CreateTile(StreamConfig? stream, int index)
    {
        var alerts = stream is null ? new List<AlertRecord>() : ActiveAlertsFor(stream.Id).ToList();
        var alert = alerts.FirstOrDefault();
        var pendingIssue = stream is null ? null : PendingIssueFor(stream.Id);
        PlaybackSnapshot? snapshot = null;
        var isLoaded = stream is not null && IsPlaybackLoaded(stream.Id);
        if (stream is not null)
        {
            _playbackSnapshots.TryGetValue(stream.Id, out snapshot);
        }

        var statusColor = alert is null && pendingIssue is not null
            ? StatusColorForPendingIssue(pendingIssue)
            : StatusColorFor(stream, alert, isLoaded, snapshot);
        var statusText = alert is null && pendingIssue is not null
            ? $"pending / {pendingIssue}"
            : StatusTextFor(stream, alert, isLoaded, snapshot);

        var root = new Grid
        {
            Background = BrushFromHex("#05080d"),
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

        Grid.SetColumn(alertStack, 1);
        Grid.SetRow(alertStack, 0);
        Panel.SetZIndex(alertStack, 10);
        videoFrame.Children.Add(alertStack);

        var bottomBar = new Grid
        {
            Background = BrushFromHex("#99050a12"),
            Height = 34
        };
        Grid.SetRow(bottomBar, 1);
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var streamName = new TextBlock
        {
            Text = stream?.Name ?? $"Tile {index + 1}: Unassigned",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
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
            Margin = new Thickness(8, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(statusTextBlock, 1);
        bottomBar.Children.Add(statusTextBlock);
        var assignButton = new Button
        {
            Content = stream is null ? "Assign" : "Change",
            Tag = index,
            Height = 24,
            MinWidth = 62,
            Margin = new Thickness(0, 4, 8, 4),
            Opacity = 0.88
        };
        assignButton.Click += TileAssignButton_Click;
        Grid.SetColumn(assignButton, 2);
        bottomBar.Children.Add(assignButton);
        root.Children.Add(bottomBar);

        var border = new Border
        {
            Background = BrushFromHex("#05080d"),
            BorderBrush = BrushFromHex("#1f2937"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
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
            EnsurePlaybackSurfaceChildren(surfaceHost, stream);
            RenderAlertOverlay(stream.Id, force: true);
        }

        return border;
    }

    private void RenderAlerts()
    {
        var activeCount = _config.AlertHistory.Count(alert => ActiveAlertStatuses.Contains(alert.Status));
        AlertSummaryText.Text = $"{activeCount} active alerts / {_config.Streams.Count} streams";
        _alertHistoryWindow?.RefreshAlerts();
        UpdatePlaybackSummary();
        UpdateAlertActionButtons();
    }

    private void UpdateAllTileChrome()
    {
        foreach (var stream in ActiveLayoutStreams())
        {
            UpdateTileChrome(stream);
        }
    }

    private void UpdateTileChrome(StreamConfig stream)
    {
        var alert = ActiveAlertFor(stream.Id);
        var pendingIssue = alert is null ? PendingIssueFor(stream.Id) : null;
        _playbackSnapshots.TryGetValue(stream.Id, out var snapshot);
        var isLoaded = IsPlaybackLoaded(stream.Id);
        var statusColor = pendingIssue is not null
            ? StatusColorForPendingIssue(pendingIssue)
            : StatusColorFor(stream, alert, isLoaded, snapshot);

        if (_tileStatusFrames.TryGetValue(stream.Id, out var frames))
        {
            var shouldBlink = alert is not null || pendingIssue is not null;
            foreach (var frame in frames)
            {
                ApplyFadeBlink(frame, shouldBlink, statusColor);
            }
        }

        if (_tileBorders.TryGetValue(stream.Id, out var tileBorder))
        {
            tileBorder.BorderBrush = BrushFromHex("#1f2937");
            tileBorder.BorderThickness = new Thickness(1);
            tileBorder.BeginAnimation(OpacityProperty, null);
            tileBorder.Opacity = 1.0;
        }

        if (_tileStatusTextBlocks.TryGetValue(stream.Id, out var statusText))
        {
            statusText.Text = pendingIssue is not null ? $"pending / {pendingIssue}" : StatusTextFor(stream, alert, isLoaded, snapshot);
            statusText.Foreground = BrushFromHex(statusColor);
        }

        RenderAlertOverlay(stream.Id);
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

    private void RenderAlertOverlay(string streamId, bool force = false)
    {
        if (!_tileAlertStacks.TryGetValue(streamId, out var stack))
        {
            return;
        }

        var activeAlerts = ActiveAlertsFor(streamId).Take(5).ToList();
        var renderKey = string.Join("|", activeAlerts.Select(alert =>
            $"{alert.Id}:{alert.Type}:{alert.Severity}:{alert.Status}:{alert.Message}"));
        if (!force &&
            _tileRenderedAlertKeys.TryGetValue(streamId, out var previousKey) &&
            string.Equals(previousKey, renderKey, StringComparison.Ordinal))
        {
            return;
        }

        _tileRenderedAlertKeys[streamId] = renderKey;
        stack.Children.Clear();
        foreach (var alert in activeAlerts)
        {
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
        ModeText.Text = _config.ServerSyncEnabled ? $"Server sync: {_config.ServerUrl}" : "Standalone local mode";
        ServerSyncStatusText.Text = _config.ServerSyncEnabled ? "server sync: enabled" : "server sync: disabled";
        ServerClientSummaryText.Text = _config.ServerSyncEnabled ? "server clients: pending" : "server clients: unavailable";
        _ = SyncWithServerAsync(force: true);
    }

    private async void StartPlaybackButton_Click(object sender, RoutedEventArgs e)
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
        UpdateFooter($"Starting playback for {streams.Count} layout streams...");

        try
        {
            foreach (var stream in streams)
            {
                if (!CanPlayStream(stream))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await StartStreamPlaybackAsync(stream);
                    started++;
                    UpdateFooter($"Starting playback: {started} started, {skipped} skipped, {failed} failed...");
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
            var summary = $"Playback started: {started}, skipped: {skipped}, failed: {failed}.";
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
        if (_isLoading || MainLayoutCombo.SelectedItem is not LayoutConfig layout)
        {
            return;
        }

        _config.ActiveLayoutId = layout.Id;
        LayoutCombo.SelectedItem = layout;
        LocalConfigStore.Save(_config);
        RenderTiles();
        UpdatePlaybackSummary();
        UpdateFooter($"Layout changed to {layout.Name}.");
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
        if (_isLoading || LayoutCombo.SelectedItem is not LayoutConfig layout)
        {
            return;
        }

        _config.ActiveLayoutId = layout.Id;
        MainLayoutCombo.SelectedItem = layout;
        LocalConfigStore.Save(_config);
        RenderTiles();
        UpdatePlaybackSummary();
        UpdateFooter($"Layout changed to {layout.Name}.");
    }

    private void UpdateFooter(string message)
    {
        FooterText.Text = $"{message} Logs: {LocalConfigStore.LogDirectory}";
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
            string.Equals(snapshot!.State, "playing", StringComparison.OrdinalIgnoreCase);
        var showFfmpegVideo = hasFfmpegSession &&
            snapshotAvailable &&
            string.Equals(snapshot!.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            ffmpegSession!.HasFrame;
        if (_tileVideoViews.TryGetValue(stream.Id, out var videoView))
        {
            videoView.MediaPlayer = hasPlayer ? player : null;
            videoView.Visibility = showVlcVideo ? Visibility.Visible : Visibility.Hidden;
        }

        if (_tileFfmpegImages.TryGetValue(stream.Id, out var image))
        {
            image.Source = hasFfmpegSession ? ffmpegSession!.Bitmap : null;
            image.Visibility = showFfmpegVideo ? Visibility.Visible : Visibility.Hidden;
        }

        if (_tileSurfacePlaceholders.TryGetValue(stream.Id, out var placeholder))
        {
            var loaded = hasPlayer || hasFfmpegSession;
            placeholder.Text = loaded ? "Playback starting" : "Playback stopped";
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

    internal void ApplyAlertProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (AlertProfileCombo.SelectedItem is not AlertProfileConfig profile)
        {
            UpdateFooter("Select an alert profile first.");
            return;
        }

        profile.BlackThreshold = ReadProfileDouble(BlackThresholdText, profile.BlackThreshold, 0, 1);
        profile.BlackPixelThreshold = ReadProfileDouble(BlackPixelThresholdText, profile.BlackPixelThreshold, 0, 1);
        profile.BlackPixelRatioThreshold = ReadProfileDouble(BlackPixelRatioText, profile.BlackPixelRatioThreshold, 0, 1);
        profile.MinBlackDurationSec = ReadProfileDouble(MinBlackDurationText, profile.MinBlackDurationSec, 0.1, 3600);
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
        PopulateAlertProfileEditor(profile);
        UpdateFooter($"Updated alert profile {profile.Name}.");
    }

    private void PopulateAlertProfileEditor(AlertProfileConfig? profile)
    {
        profile ??= _config.AlertProfiles.FirstOrDefault();
        var enabled = profile is not null;
        foreach (var control in new TextBox[]
        {
            BlackThresholdText,
            BlackPixelThresholdText,
            BlackPixelRatioText,
            MinBlackDurationText,
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
            return;
        }

        BlackThresholdText.Text = FormatProfileNumber(profile.BlackThreshold);
        BlackPixelThresholdText.Text = FormatProfileNumber(profile.BlackPixelThreshold);
        BlackPixelRatioText.Text = FormatProfileNumber(profile.BlackPixelRatioThreshold);
        MinBlackDurationText.Text = FormatProfileNumber(profile.MinBlackDurationSec);
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

    private IEnumerable<StreamConfig> ActiveLayoutStreams()
    {
        var layout = _config.Layouts.FirstOrDefault(layout => layout.Id == _config.ActiveLayoutId)
            ?? _config.Layouts.FirstOrDefault();
        if (layout is null)
        {
            return [];
        }

        return layout.Tiles
            .OrderBy(tile => tile.Index)
            .Select(tile => _config.Streams.FirstOrDefault(stream => stream.Id == tile.StreamId))
            .Where(stream => stream is not null)
            .Cast<StreamConfig>()
            .DistinctBy(stream => stream.Id);
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
        media.AddOption(":network-caching=300");
        media.AddOption(":live-caching=300");
        media.AddOption(":rtsp-tcp");
        media.AddOption(":ipv4-timeout=3000000");
        media.AddOption(":sout-mux-caching=300");
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

        return string.Equals(stream.InputProtocol, "srt", StringComparison.OrdinalIgnoreCase) ||
            stream.InputUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
    }

    private void StartFfmpegStreamPlayback(StreamConfig stream)
    {
        StopStreamPlayback(stream.Id);
        ClearPlaybackAnalysis(stream.Id);
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg was not found.");
        var profile = AlertProfileFor(stream);
        var session = new FfmpegPlaybackSession(
            ffmpeg,
            stream.InputUrl,
            stream.InputProtocol,
            Dispatcher,
            profile.BlackPixelThreshold,
            profile.FreezeDifferenceThreshold);

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
        foreach (var streamId in _players.Keys.Concat(_ffmpegPlaybackSessions.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            StopStreamPlayback(streamId);
        }
    }

    private void StartAudioMonitor(StreamConfig stream)
    {
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
        if (_videoMonitors.ContainsKey(stream.Id) || !CanPlayStream(stream))
        {
            return;
        }

        try
        {
            var profile = AlertProfileFor(stream);
            var ffmpeg = FindFfmpeg();
            IVideoAnalysisMonitor monitor = !string.IsNullOrWhiteSpace(ffmpeg)
                ? new FfmpegVideoAnalysisMonitor(ffmpeg, stream.InputUrl, stream.InputProtocol, profile.BlackPixelThreshold, profile.FreezeDifferenceThreshold)
                : new VideoLumaMonitor(EnsureLibVlc(), stream.InputUrl, profile.BlackPixelThreshold, profile.FreezeDifferenceThreshold);
            monitor.Start();
            _videoMonitors[stream.Id] = monitor;
            AppendDesktopLog($"Video analysis monitor started for {stream.Id}: {monitor.Kind}");
        }
        catch (Exception ex)
        {
            AppendDesktopLog($"Video analysis monitor failed for {stream.Id}: {ex.Message}");
            StopVideoMonitor(stream.Id);
        }
    }

    private void QueueVideoMonitorStart(StreamConfig stream)
    {
        if (_videoMonitors.ContainsKey(stream.Id) || !CanPlayStream(stream))
        {
            return;
        }

        var profile = AlertProfileFor(stream);
        var ffmpeg = FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            _ = Dispatcher.BeginInvoke(() => StartVideoMonitor(stream), DispatcherPriority.ContextIdle);
            return;
        }

        var streamId = stream.Id;
        var inputUrl = stream.InputUrl;
        var inputProtocol = stream.InputProtocol;
        var blackPixelThreshold = profile.BlackPixelThreshold;
        var freezeDifferenceThreshold = profile.FreezeDifferenceThreshold;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            IVideoAnalysisMonitor? monitor = null;
            Exception? error = null;
            try
            {
                monitor = new FfmpegVideoAnalysisMonitor(ffmpeg, inputUrl, inputProtocol, blackPixelThreshold, freezeDifferenceThreshold);
                monitor.Start();
            }
            catch (Exception ex)
            {
                error = ex;
                monitor?.Dispose();
                monitor = null;
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_players.ContainsKey(streamId))
                {
                    monitor?.Dispose();
                    return;
                }

                if (monitor is not null)
                {
                    _videoMonitors[streamId] = monitor;
                    AppendDesktopLog($"Video analysis monitor started for {streamId}: {monitor.Kind}");
                    return;
                }

                AppendDesktopLog($"Video analysis monitor failed for {streamId}: {error?.Message}");
            }, DispatcherPriority.Background);
        });
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

    private void UpdatePlaybackSummary()
    {
        var active = _players.Values.Count(player => IsActivePlaybackState(player.State)) +
            _ffmpegPlaybackSessions.Values.Count(session => string.Equals(session.State, "playing", StringComparison.OrdinalIgnoreCase));
        var errors = _playbackSnapshots.Values.Count(snapshot => string.Equals(snapshot.State, "error", StringComparison.OrdinalIgnoreCase));
        PlaybackSummaryText.Text = $"{active} active / {_players.Count + _ffmpegPlaybackSessions.Count} loaded / {ActiveLayoutStreams().Count()} layout streams / {errors} errors";
    }

    private void UpdateAlertActionButtons()
    {
        _alertHistoryWindow?.RefreshAlerts();
    }

    private IEnumerable<AlertRecord> ActiveAlertsFor(string streamId)
    {
        return _config.AlertHistory
            .Where(alert => string.Equals(alert.StreamId, streamId, StringComparison.OrdinalIgnoreCase))
            .Where(alert => ActiveAlertStatuses.Contains(alert.Status))
            .OrderByDescending(alert => SeverityRank(alert.Severity))
            .ThenByDescending(alert => alert.StartedAt);
    }

    private AlertRecord? ActiveAlertFor(string streamId)
    {
        return ActiveAlertsFor(streamId).FirstOrDefault();
    }

    private string? PendingIssueFor(string streamId)
    {
        var activeTypes = ActiveAlertsFor(streamId)
            .Select(alert => alert.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var type in new[]
        {
            "video.no_signal",
            "video.black",
            "video.freeze",
            "video.low_fps",
            "audio.channel_missing",
            "audio.silence",
            "audio.clipping"
        })
        {
            if (activeTypes.Contains(type))
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

    private static string StatusColorFor(StreamConfig? stream, AlertRecord? alert, bool isLoaded, PlaybackSnapshot? snapshot)
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
                "opening" or "buffering" or "starting" => "#f59e0b",
                "stopped" or "ended" or "error" => "#ef4444",
                _ => "#6b7280"
            };
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

    private static string StatusTextFor(StreamConfig? stream, AlertRecord? alert, bool isLoaded, PlaybackSnapshot? snapshot)
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

        return string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase)
            ? "OK"
            : $"monitoring / {snapshot.State}";
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

            return $"playback: {snapshot.State} / fps: {fps} / time: {time}/{length} / audio: {audio} / tracks: {audioTracks} / level: {audioLevel} / luma: {luma}";
        }

        return IsPlaybackLoaded(stream.Id) ? "playback: starting" : "playback: stopped";
    }

    private void RefreshPlaybackMetadata()
    {
        _blinkPhase = !_blinkPhase;
        foreach (var (streamId, player) in _players.ToList())
        {
            try
            {
                var snapshot = BuildSnapshot(player, null);
                _playbackSnapshots[streamId] = snapshot;
                TrackPlaybackProgress(streamId, snapshot);
                if (_config.Streams.FirstOrDefault(stream => stream.Id == streamId) is { } stream)
                {
                    RefreshPlaybackSurface(stream);
                }
            }
            catch
            {
                _playbackSnapshots[streamId] = EmptyPlaybackSnapshot("error");
            }
        }

        foreach (var (streamId, session) in _ffmpegPlaybackSessions.ToList())
        {
            try
            {
                var snapshot = session.CurrentPlaybackSnapshot();
                _playbackSnapshots[streamId] = snapshot;
                TrackPlaybackProgress(streamId, snapshot);
                if (_config.Streams.FirstOrDefault(stream => stream.Id == streamId) is { } stream)
                {
                    RefreshPlaybackSurface(stream);
                }
            }
            catch
            {
                _playbackSnapshots[streamId] = EmptyPlaybackSnapshot("error");
            }
        }

        var alertStateChanged = EvaluatePlaybackAlerts();
        alertStateChanged |= RefreshSnoozedAlerts();

        UpdatePlaybackSummary();
        UpdateAllTileChrome();
        if (alertStateChanged)
        {
            LocalConfigStore.Save(_config);
            RenderAlerts();
            UpdateAllTileChrome();
        }

        _ = SyncWithServerAsync(force: false);
    }

    private bool EvaluatePlaybackAlerts()
    {
        var changed = false;
        var activeLayoutStreams = ActiveLayoutStreams().ToList();
        foreach (var stream in activeLayoutStreams.Where(stream => stream.Enabled && stream.AnalysisEnabled && IsPlaybackLoaded(stream.Id)))
        {
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
                if (IssueDurationSec(stream.Id, "video.black") >= profile.MinBlackDurationSec)
                {
                    changed |= RaiseAlert(stream, "video.black", "error", AlertMessageBlack);
                }
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

        foreach (var stream in activeLayoutStreams.Where(stream => !IsPlaybackLoaded(stream.Id)))
        {
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
        PlayAlertSound(stream, severity);
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
                PlayAlertSound(stream, alert.Severity);
            }

            changed = true;
        }

        return changed;
    }

    private void PlayAlertSound(StreamConfig stream, string severity)
    {
        if (!_config.AlertSoundEnabled || stream.AlertMuted)
        {
            return;
        }

        try
        {
            switch (severity.ToLowerInvariant())
            {
                case "critical":
                case "error":
                    SystemSounds.Hand.Play();
                    break;
                case "warning":
                    SystemSounds.Exclamation.Play();
                    break;
                default:
                    SystemSounds.Asterisk.Play();
                    break;
            }
        }
        catch
        {
            // Alert sounds are best-effort and must not interrupt monitoring.
        }
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
            ServerSyncStatusText.Text = $"server sync: ok / config: {streams.Count} streams, {layouts.Count} layouts, {alertProfiles.Count} profiles / alerts: {serverAlerts.Count} pulled, {uploaded} uploaded / {DateTimeOffset.Now:HH:mm:ss}";
            ServerClientSummaryText.Text = ServerClientSummary(clients);
        }
        catch (Exception ex)
        {
            ServerSyncStatusText.Text = $"server sync: failed / {ex.Message}";
            ServerClientSummaryText.Text = "server clients: unavailable";
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

        RefreshConfigBindings(selectedStreamId);
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
            MinBlackDurationSec = profile.MinBlackDurationSec > 0 ? profile.MinBlackDurationSec : defaults.MinBlackDurationSec,
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

        return new PlaybackSnapshot(
            overrideState ?? player.State.ToString().ToLowerInvariant(),
            player.Fps,
            player.Time,
            player.Length,
            player.Mute,
            player.AudioTrackCount,
            player.AudioTrack,
            stats?.ReadBytes ?? 0,
            stats?.DemuxReadBytes ?? 0,
            stats?.DecodedVideo ?? 0,
            stats?.DisplayedPictures ?? 0,
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

    private static long PlaybackProgressMarker(PlaybackSnapshot snapshot)
    {
        return new[]
        {
            Math.Max(0, snapshot.TimeMs),
            Math.Max(0, snapshot.ReadBytes),
            Math.Max(0, snapshot.DemuxReadBytes),
            Math.Max(0, snapshot.DecodedVideo),
            Math.Max(0, snapshot.DisplayedPictures),
            Math.Max(0, snapshot.DecodedAudio),
            Math.Max(0, snapshot.PlayedAudioBuffers)
        }.Max();
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
            "starting" or "opening" or "buffering" or "playing" => true,
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
        _playbackTimer.Stop();
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
        return (Brush)new BrushConverter().ConvertFromString(hex)!;
    }

    private static Color ColorFromHex(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex)!;
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
    DateTimeOffset LastSamplesAt);

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

internal sealed class FfmpegPlaybackSession : IVideoAnalysisMonitor
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int BytesPerPixel = 3;
    private const int Stride = Width * BytesPerPixel;
    private const int FrameBytes = Stride * Height;
    private const double TargetFps = 30;
    private const double SilenceFloorDb = -120;

    private readonly string _ffmpegPath;
    private readonly string _inputUrl;
    private readonly string _protocol;
    private readonly List<string> _inputCandidates;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly byte[] _previousBuffer = new byte[FrameBytes];
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
    private bool _disposed;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private double _lastParsedRmsDb = SilenceFloorDb;
    private double _lastParsedPeakDb = SilenceFloorDb;

    public FfmpegPlaybackSession(string ffmpegPath, string inputUrl, string protocol, Dispatcher dispatcher, double darkPixelThreshold, double stabilityDifferenceThreshold)
    {
        _ffmpegPath = ffmpegPath;
        _inputUrl = inputUrl;
        _protocol = protocol;
        _inputCandidates = BuildInputCandidates(inputUrl, protocol);
        _dispatcher = dispatcher;
        DarkPixelThreshold = Math.Clamp(darkPixelThreshold, 0.0, 1.0);
        StabilityDifferenceThreshold = Math.Clamp(stabilityDifferenceThreshold, 0.0, 1.0);
        Bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgr24, null);
    }

    public WriteableBitmap Bitmap { get; }

    public bool HasFrame => _framesRead > 0;

    public string Kind => "ffmpeg-playback";

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
        var candidates = new List<string> { inputUrl };
        if ((string.Equals(protocol, "srt", StringComparison.OrdinalIgnoreCase) ||
                inputUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase)) &&
            !inputUrl.Contains("mode=", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(inputUrl.Contains('?', StringComparison.Ordinal) ? $"{inputUrl}&mode=listener" : $"{inputUrl}?mode=listener");
            candidates.Add(inputUrl.Contains('?', StringComparison.Ordinal) ? $"{inputUrl}&mode=caller" : $"{inputUrl}?mode=caller");
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public PlaybackSnapshot CurrentPlaybackSnapshot()
    {
        lock (_sync)
        {
            var state = _state;
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
                (int)Math.Min(int.MaxValue, _framesRead),
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
                snapshot = new AudioLevelSnapshot(SilenceFloorDb, SilenceFloorDb, 0, DateTimeOffset.MinValue);
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
        var rmsMetadata = Regex.Match(line, @"lavfi\.astats\.Overall\.RMS_level=(-?inf|[-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (rmsMetadata.Success)
        {
            _lastParsedRmsDb = ParseFfmpegDb(rmsMetadata.Groups[1].Value);
            UpdateAudioSnapshot();
            return true;
        }

        var peakMetadata = Regex.Match(line, @"lavfi\.astats\.Overall\.Peak_level=(-?inf|[-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
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

        var rms = Regex.Match(line, @"RMS level dB:\s*(-?inf|[-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (rms.Success)
        {
            _lastParsedRmsDb = ParseFfmpegDb(rms.Groups[1].Value);
            UpdateAudioSnapshot();
            return true;
        }

        var peak = Regex.Match(line, @"Peak level dB:\s*(-?inf|[-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (peak.Success)
        {
            _lastParsedPeakDb = ParseFfmpegDb(peak.Groups[1].Value);
            UpdateAudioSnapshot();
            return true;
        }

        return false;
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
            var clippingSamples = _lastParsedPeakDb >= -1 ? 1 : 0;
            _audioSnapshot = new AudioLevelSnapshot(_lastParsedRmsDb, _lastParsedPeakDb, clippingSamples, DateTimeOffset.UtcNow);
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

                var frameCopy = new byte[FrameBytes];
                Buffer.BlockCopy(frame, 0, frameCopy, 0, FrameBytes);
                UpdateSnapshot(frameCopy);
                _ = _dispatcher.BeginInvoke(() =>
                {
                    Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), frameCopy, Stride, 0);
                }, DispatcherPriority.Render);
                _state = "playing";
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
        var pixelCount = Width * Height;
        for (var index = 0; index < frame.Length; index += 3)
        {
            var blue = frame[index];
            var green = frame[index + 1];
            var red = frame[index + 2];
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
        Buffer.BlockCopy(frame, 0, _previousBuffer, 0, frame.Length);
        _hasPreviousFrame = true;
        lock (_sync)
        {
            _framesRead++;
            _bytesRead += frame.Length;
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
        media.AddOption(":no-audio");
        media.AddOption(":rtsp-tcp");
        media.AddOption(":network-caching=300");
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
    private const uint ChannelCount = 2;
    private const double SilenceFloorDb = -120;
    private const double DefaultClippingThresholdDb = -1;

    private readonly object _sync = new();
    private readonly VlcMediaPlayer.LibVLCAudioPlayCb _playCallback;
    private readonly VlcMediaPlayer.LibVLCAudioPauseCb _pauseCallback;
    private readonly VlcMediaPlayer.LibVLCAudioResumeCb _resumeCallback;
    private readonly VlcMediaPlayer.LibVLCAudioFlushCb _flushCallback;
    private readonly VlcMediaPlayer.LibVLCAudioDrainCb _drainCallback;
    private VlcMediaPlayer? _player;
    private AudioLevelSnapshot? _snapshot;
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
        player.SetAudioFormat("S16N", SampleRate, ChannelCount);
        player.SetAudioCallbacks(_playCallback, _pauseCallback, _resumeCallback, _flushCallback, _drainCallback);
    }

    public bool TryGetSnapshot(out AudioLevelSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_snapshot is null)
            {
                snapshot = new AudioLevelSnapshot(SilenceFloorDb, SilenceFloorDb, 0, DateTimeOffset.MinValue);
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
            var sampleCount = checked((int)(count * ChannelCount));
            if (sampleCount <= 0)
            {
                return;
            }

            var buffer = new short[sampleCount];
            Marshal.Copy(samples, buffer, 0, sampleCount);
            var clippingAmplitude = short.MaxValue * Math.Pow(10, DefaultClippingThresholdDb / 20.0);

            double sumSquares = 0;
            var maxAbs = 0;
            var clippingSamples = 0;
            foreach (var sample in buffer)
            {
                var abs = Math.Abs((int)sample);
                maxAbs = Math.Max(maxAbs, abs);
                if (abs >= clippingAmplitude)
                {
                    clippingSamples++;
                }

                var normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }

            var rms = Math.Sqrt(sumSquares / sampleCount);
            var levelDb = rms > 0 ? 20 * Math.Log10(rms) : SilenceFloorDb;
            var peakDb = maxAbs > 0 ? 20 * Math.Log10(Math.Min(maxAbs, 32767) / 32767.0) : SilenceFloorDb;
            lock (_sync)
            {
                _snapshot = new AudioLevelSnapshot(levelDb, peakDb, clippingSamples, DateTimeOffset.UtcNow);
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
