using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using IPMultiViewer.Core.Audio;

namespace IPMultiViewer.Desktop;

public partial class MainWindow
{
    private const double NextTileFrameThickness = 1.5;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private MeterDisplayMode _nextMeterMode = MeterDisplayMode.Vu;
    private readonly Dictionary<string, Window> _nextAlertOverlayWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _nextAlertOverlayTargets = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private Border BuildNextTile(StreamConfig? stream, int index)
    {
        List<AlertRecord> alerts = stream is null ? [] : ActiveAlertsFor(stream.Id);
        var alert = alerts.Count > 0 ? alerts[0] : null;
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
            : NextStatusTextFor(stream, alert, isLoaded, snapshot, videoFreshness);

        var root = new Grid
        {
            Background = BrushFromHex("#071019"),
            ClipToBounds = true
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

        var videoFrame = new Grid
        {
            Background = BrushFromHex("#071019"),
            ClipToBounds = true
        };
        videoFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NextTileFrameThickness) });
        videoFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        videoFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NextTileFrameThickness) });
        videoFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(NextTileFrameThickness) });
        videoFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        videoFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(NextTileFrameThickness) });
        Grid.SetRow(videoFrame, 0);
        root.Children.Add(videoFrame);

        var leftFrame = new Border
        {
            Background = BrushFromHex(statusColor),
            IsHitTestVisible = false
        };
        Grid.SetColumn(leftFrame, 0);
        Grid.SetRowSpan(leftFrame, 3);
        videoFrame.Children.Add(leftFrame);

        var statusStrip = new Border
        {
            Background = BrushFromHex(statusColor),
            IsHitTestVisible = false
        };
        Grid.SetColumn(statusStrip, 1);
        Grid.SetRow(statusStrip, 0);
        Panel.SetZIndex(statusStrip, 20);
        videoFrame.Children.Add(statusStrip);

        var rightFrame = new Border
        {
            Background = BrushFromHex(statusColor),
            IsHitTestVisible = false
        };
        Grid.SetColumn(rightFrame, 2);
        Grid.SetRowSpan(rightFrame, 3);
        videoFrame.Children.Add(rightFrame);

        var bottomFrame = new Border
        {
            Background = BrushFromHex(statusColor),
            IsHitTestVisible = false
        };
        Grid.SetColumn(bottomFrame, 1);
        Grid.SetRow(bottomFrame, 2);
        videoFrame.Children.Add(bottomFrame);

        var surfaceHost = new Grid
        {
            Background = Brushes.Black,
            ClipToBounds = true
        };
        Grid.SetColumn(surfaceHost, 1);
        Grid.SetRow(surfaceHost, 1);
        videoFrame.Children.Add(surfaceHost);
        SetPlaybackSurface(surfaceHost, stream);

        var audioMeterHost = new Grid
        {
            IsHitTestVisible = false,
            Margin = new Thickness(8, 7, 8, 7),
            Visibility = Visibility.Collapsed
        };
        audioMeterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        audioMeterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        audioMeterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(audioMeterHost, 1);
        Grid.SetRow(audioMeterHost, 1);
        Panel.SetZIndex(audioMeterHost, 8);
        videoFrame.Children.Add(audioMeterHost);

        var alertStack = new StackPanel
        {
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        BindingOperations.SetBinding(
            alertStack,
            FrameworkElement.WidthProperty,
            new Binding(nameof(ActualWidth)) { Source = videoFrame });
        BindingOperations.SetBinding(
            alertStack,
            FrameworkElement.MaxHeightProperty,
            new Binding(nameof(ActualHeight)) { Source = videoFrame });
        Window? alertOverlayWindow = null;
        if (stream is null || ShouldUseFfmpegPlayback(stream))
        {
            Grid.SetColumn(alertStack, 0);
            Grid.SetColumnSpan(alertStack, 3);
            Grid.SetRow(alertStack, 0);
            Grid.SetRowSpan(alertStack, 3);
            Panel.SetZIndex(alertStack, 30);
            videoFrame.Children.Add(alertStack);
        }
        else
        {
            alertOverlayWindow = new Window
            {
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Content = alertStack,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.Height,
                SnapsToDevicePixels = true,
                Title = "IP MultiViewer Alert Overlay",
                Topmost = false,
                UseLayoutRounding = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None
            };
        }

        videoFrame.Loaded += (_, _) => RefreshNextAlertOverlayPositions();
        videoFrame.SizeChanged += (_, _) =>
        {
            RefreshNextAlertOverlayPositions();
            if (stream is not null)
            {
                RefreshNextAudioMeterReserve(stream.Id);
            }
        };

        var bottomBar = new Grid
        {
            Background = BrushFromHex("#0A141C")
        };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(bottomBar, 1);
        root.Children.Add(bottomBar);

        var tileNumber = new TextBlock
        {
            Text = (index + 1).ToString(),
            Foreground = BrushFromHex("#F3F6F8"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        bottomBar.Children.Add(tileNumber);

        var streamName = new TextBlock
        {
            Text = stream?.Name ?? "Unassigned",
            Foreground = BrushFromHex("#E8EDF0"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(streamName, 1);
        bottomBar.Children.Add(streamName);

        var statusDot = new Border
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = BrushFromHex(statusColor),
            CornerRadius = new CornerRadius(5),
            IsHitTestVisible = false
        };
        var statusTextBlock = new TextBlock
        {
            Text = statusText,
            Foreground = BrushFromHex(statusColor),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var statusPanel = new StackPanel
        {
            Margin = new Thickness(8, 0, 12, 0),
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusPanel.Children.Add(statusDot);
        statusPanel.Children.Add(statusTextBlock);
        Grid.SetColumn(statusPanel, 2);
        bottomBar.Children.Add(statusPanel);

        var assignButton = new Button
        {
            Content = stream is null ? "Assign" : "Change",
            Tag = index,
            Width = 68,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (TryFindResource("TileActionButton") is Style tileActionStyle)
        {
            assignButton.Style = tileActionStyle;
        }

        assignButton.Click += TileAssignButton_Click;
        Grid.SetColumn(assignButton, 3);
        bottomBar.Children.Add(assignButton);

        var border = new Border
        {
            Background = BrushFromHex("#071019"),
            BorderBrush = BrushFromHex("#2B3B46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(3),
            ClipToBounds = true,
            Child = root
        };

        if (stream is not null)
        {
            _tileIndexStreamIds[index] = stream.Id;
            _streamTileIndexes[stream.Id] = index;
            _tileStatusFrames[stream.Id] = [statusStrip, leftFrame, rightFrame, bottomFrame, statusDot];
            _tileBorders[stream.Id] = border;
            _tileNameTextBlocks[stream.Id] = streamName;
            _tileStatusTextBlocks[stream.Id] = statusTextBlock;
            _tileAlertStacks[stream.Id] = alertStack;
            if (alertOverlayWindow is not null)
            {
                _nextAlertOverlayWindows[stream.Id] = alertOverlayWindow;
                _nextAlertOverlayTargets[stream.Id] = videoFrame;
            }
            _tileSurfaceHosts[stream.Id] = surfaceHost;
            _tileAudioMeterHosts[stream.Id] = audioMeterHost;

            EnsurePlaybackSurfaceChildren(surfaceHost, stream);
            RenderAlertOverlay(stream.Id, force: true);
            RenderAudioMeter(stream.Id, force: true);
            ApplyNextMeterModeToStream(stream.Id);
        }

        return border;
    }

    private string NextStatusTextFor(
        StreamConfig? stream,
        AlertRecord? alert,
        bool isLoaded,
        PlaybackSnapshot? snapshot,
        PlaybackVideoFreshness videoFreshness)
    {
        if (stream is not null &&
            stream.Enabled &&
            alert is null &&
            isLoaded &&
            snapshot is not null &&
            string.Equals(snapshot.State, "playing", StringComparison.OrdinalIgnoreCase) &&
            HasPresentedPlaybackVideo(stream.Id, snapshot) &&
            IsPlaybackVideoFreshForStatus(videoFreshness))
        {
            return "PLAYING";
        }

        return StatusTextFor(stream, alert, isLoaded, snapshot, videoFreshness);
    }

    private void NextMeterVuButton_Click(object sender, RoutedEventArgs e)
    {
        SetNextMeterMode(MeterDisplayMode.Vu);
    }

    private void NextMeterPeakButton_Click(object sender, RoutedEventArgs e)
    {
        SetNextMeterMode(MeterDisplayMode.Peak);
    }

    private void ConfigureNextUiFromConfig()
    {
        _nextMeterMode = string.Equals(_config.AudioMeterMode, "peak", StringComparison.OrdinalIgnoreCase)
            ? MeterDisplayMode.Peak
            : MeterDisplayMode.Vu;
        _config.AudioMeterMode = MeterModeConfigValue(_nextMeterMode);
    }

    private void SetNextMeterMode(MeterDisplayMode mode)
    {
        var changed = _nextMeterMode != mode;
        _nextMeterMode = mode;
        _config.AudioMeterMode = MeterModeConfigValue(mode);
        ApplyNextMeterToggleChrome();

        foreach (var streamId in _tileAudioMeterHosts.Keys.ToArray())
        {
            InvalidateNextAudioMeterView(streamId);
            UpdateAudioMeterTargets(streamId, force: true);
        }

        if (changed && !_isLoading)
        {
            LocalConfigStore.QueueSave(_config);
            UpdateFooter($"Meter mode changed to {NextMeterModeLabel(mode)}.");
        }
    }

    private void ApplyNextMeterModeToAll()
    {
        ApplyNextMeterToggleChrome();
        foreach (var streamId in _tileAudioMeterViews.Keys)
        {
            ApplyNextMeterModeToStream(streamId);
        }
    }

    private void InvalidateNextAudioMeterView(string streamId)
    {
        if (_tileAudioMeterHosts.TryGetValue(streamId, out var host))
        {
            host.Children.Clear();
            host.Visibility = Visibility.Collapsed;
        }

        _tileAudioMeterViews.Remove(streamId);
        _tileAudioMeterViewLookup.Remove(streamId);
        _lastAudioMeterTargetRefreshAt.Remove(streamId);
        ClearNextAudioMeterReserve(streamId);
    }

    private void ApplyNextMeterModeToStream(string streamId)
    {
        if (!_tileAudioMeterViews.TryGetValue(streamId, out var views))
        {
            return;
        }

        var peakVisibility = _nextMeterMode == MeterDisplayMode.Peak
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var view in views)
        {
            view.Fill.Opacity = 1;

            if (view.PeakMarker.Visibility != peakVisibility)
            {
                view.PeakMarker.Visibility = peakVisibility;
            }
        }
    }

    private void ApplyNextMeterToggleChrome()
    {
        if (NextMeterVuButton is null || NextMeterPeakButton is null)
        {
            return;
        }

        var selectedBackground = BrushFromHex("#079CE4");
        var selectedBorder = BrushFromHex("#14B4FF");
        var idleBackground = BrushFromHex("#081018");
        var idleBorder = BrushFromHex("#40515D");
        var selectedForeground = Brushes.White;
        var idleForeground = BrushFromHex("#D0D6DB");

        var peakSelected = _nextMeterMode == MeterDisplayMode.Peak;
        NextMeterVuButton.Background = peakSelected ? idleBackground : selectedBackground;
        NextMeterVuButton.BorderBrush = peakSelected ? idleBorder : selectedBorder;
        NextMeterVuButton.Foreground = peakSelected ? idleForeground : selectedForeground;

        NextMeterPeakButton.Background = peakSelected ? selectedBackground : idleBackground;
        NextMeterPeakButton.BorderBrush = peakSelected ? selectedBorder : idleBorder;
        NextMeterPeakButton.Foreground = peakSelected ? selectedForeground : idleForeground;
    }

    private void UpdateNextAudioMeterReserve(string streamId, AudioChannelLevel[] channels)
    {
        if (!_tileSurfaceHosts.TryGetValue(streamId, out var surfaceHost))
        {
            return;
        }

        var (left, right) = SplitAudioChannelsForSides(channels);
        var current = surfaceHost.Margin;
        var availableWidth = surfaceHost.ActualWidth + current.Left + current.Right;
        var availableHeight = surfaceHost.ActualHeight + current.Top + current.Bottom;
        var leftReserve = NextMeterPanelReserve(left.Length, availableWidth, availableHeight);
        var rightReserve = NextMeterPanelReserve(right.Length, availableWidth, availableHeight);
        if (_tileAudioMeterHosts.TryGetValue(streamId, out var meterHost))
        {
            foreach (var panel in meterHost.Children.OfType<Viewbox>())
            {
                panel.MaxWidth = Grid.GetColumn(panel) == 0 ? leftReserve : rightReserve;
            }
        }

        surfaceHost.Margin = new Thickness(
            leftReserve,
            current.Top,
            rightReserve,
            current.Bottom);
    }

    private void RefreshNextAudioMeterReserve(string streamId)
    {
        if (!_tileAudioMeterViews.TryGetValue(streamId, out var views) || views.Length == 0)
        {
            ClearNextAudioMeterReserve(streamId);
            return;
        }

        var channels = views
            .Select(view => new AudioChannelLevel(view.Channel, view.LastLevelDb, view.LastPeakDb, 0))
            .ToArray();
        UpdateNextAudioMeterReserve(streamId, channels);
    }

    private void ClearNextAudioMeterReserve(string streamId)
    {
        if (_tileSurfaceHosts.TryGetValue(streamId, out var surfaceHost))
        {
            var current = surfaceHost.Margin;
            surfaceHost.Margin = new Thickness(0, current.Top, 0, current.Bottom);
        }
    }

    private void UpdateNextAlertReserve(string streamId, int alertCount)
    {
        if (!_tileSurfaceHosts.TryGetValue(streamId, out var surfaceHost))
        {
            return;
        }

        var current = surfaceHost.Margin;
        if (current.Top != 0)
        {
            surfaceHost.Margin = new Thickness(current.Left, 0, current.Right, current.Bottom);
        }

        if (!_nextAlertOverlayWindows.TryGetValue(streamId, out var overlayWindow))
        {
            return;
        }

        if (alertCount > 0 && IsVisible && IsNextMainWindowForeground() && WindowState != WindowState.Minimized)
        {
            ShowNextAlertOverlayWindow(streamId, overlayWindow);
        }
        else
        {
            HideNextAlertOverlayWindow(overlayWindow);
        }
    }

    private void RemoveNextAlertOverlayWindow(string streamId)
    {
        _nextAlertOverlayTargets.Remove(streamId);
        if (_nextAlertOverlayWindows.Remove(streamId, out var overlayWindow))
        {
            overlayWindow.Content = null;
            overlayWindow.Close();
        }
    }

    private void CloseNextAlertOverlayWindows()
    {
        foreach (var overlayWindow in _nextAlertOverlayWindows.Values)
        {
            overlayWindow.Content = null;
            overlayWindow.Close();
        }

        _nextAlertOverlayWindows.Clear();
        _nextAlertOverlayTargets.Clear();
    }

    private void RefreshNextAlertOverlayPositions()
    {
        if (!IsVisible || !IsNextMainWindowForeground() || WindowState == WindowState.Minimized)
        {
            HideNextAlertOverlayWindows();
            return;
        }

        foreach (var (streamId, overlayWindow) in _nextAlertOverlayWindows)
        {
            if (!_tileAlertStacks.TryGetValue(streamId, out var stack) || stack.Children.Count == 0)
            {
                HideNextAlertOverlayWindow(overlayWindow);
                continue;
            }

            ShowNextAlertOverlayWindow(streamId, overlayWindow);
        }
    }

    private void ShowNextAlertOverlayWindow(string streamId, Window overlayWindow)
    {
        if (!IsNextMainWindowForeground() ||
            !_nextAlertOverlayTargets.TryGetValue(streamId, out var target) ||
            !target.IsLoaded ||
            target.ActualWidth <= 0 ||
            target.ActualHeight <= 0)
        {
            HideNextAlertOverlayWindow(overlayWindow);
            return;
        }

        if (overlayWindow.Owner is null)
        {
            overlayWindow.Owner = this;
        }

        var screenOrigin = target.PointToScreen(new Point(0, 0));
        var source = PresentationSource.FromVisual(target);
        var logicalOrigin = source?.CompositionTarget is { } compositionTarget
            ? compositionTarget.TransformFromDevice.Transform(screenOrigin)
            : screenOrigin;
        overlayWindow.Left = logicalOrigin.X;
        overlayWindow.Top = logicalOrigin.Y;
        overlayWindow.Width = Math.Max(1, target.ActualWidth);
        overlayWindow.MaxHeight = Math.Max(1, target.ActualHeight);
        if (!overlayWindow.IsVisible)
        {
            overlayWindow.Show();
        }

        overlayWindow.UpdateLayout();
        var targetBottomRight = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
        var overlaySource = PresentationSource.FromVisual(overlayWindow);
        var overlayScaleY = overlaySource?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        var overlayHandle = new WindowInteropHelper(overlayWindow).Handle;
        var physicalWidth = Math.Max(1, (int)Math.Round(targetBottomRight.X - screenOrigin.X));
        var physicalHeight = Math.Max(1, (int)Math.Ceiling(overlayWindow.ActualHeight * overlayScaleY));
        if (overlayHandle != IntPtr.Zero)
        {
            _ = SetWindowPos(
                overlayHandle,
                IntPtr.Zero,
                (int)Math.Round(screenOrigin.X),
                (int)Math.Round(screenOrigin.Y),
                physicalWidth,
                physicalHeight,
                SwpNoZOrder | SwpNoActivate);
        }

    }

    private static void HideNextAlertOverlayWindow(Window overlayWindow)
    {
        if (overlayWindow.IsVisible)
        {
            overlayWindow.Hide();
        }
    }

    private bool IsNextMainWindowForeground()
    {
        if (!IsActive)
        {
            return false;
        }

        var mainWindowHandle = new WindowInteropHelper(this).Handle;
        return mainWindowHandle != IntPtr.Zero && GetForegroundWindow() == mainWindowHandle;
    }

    private void HideNextAlertOverlayWindows()
    {
        foreach (var overlayWindow in _nextAlertOverlayWindows.Values)
        {
            HideNextAlertOverlayWindow(overlayWindow);
        }
    }

    private static double NextMeterPanelReserve(int channelCount, double availableWidth, double availableHeight)
    {
        if (channelCount <= 0)
        {
            return 0;
        }

        var naturalWidth = Math.Min(192, 48 + channelCount * 18);
        var heightScale = availableHeight > 0
            ? Math.Min(1, Math.Max(0.45, (availableHeight - 14) / 181d))
            : 1;
        var widthCap = availableWidth > 0
            ? Math.Max(56, availableWidth * 0.32)
            : 192;
        return Math.Ceiling(Math.Min(widthCap, naturalWidth * heightScale));
    }

    private static bool IsNextMeterMajorTick(double displayValue, MeterDisplayMode mode)
    {
        if (mode == MeterDisplayMode.Peak)
        {
            return displayValue is -60 or -48 or -36 or -24 or -18 or -12 or -6 or -3 or 0;
        }

        return displayValue is -60 or -40 or -20 or -10 or -5 or -3 or 0 or 3;
    }

    private static string FormatNextMeterTick(double displayValue)
    {
        var prefix = displayValue > 0 ? "+" : string.Empty;
        return $"{prefix}{displayValue.ToString("0", CultureInfo.InvariantCulture)}";
    }

    private static string MeterModeConfigValue(MeterDisplayMode mode) =>
        mode == MeterDisplayMode.Peak ? "peak" : "vu";

    private static string NextMeterModeLabel(MeterDisplayMode mode) =>
        mode == MeterDisplayMode.Peak ? "PEAK" : "VU";

    private static (int Width, int Height) NextFfmpegRenderSizeFor(string? layoutKind)
    {
        return layoutKind?.ToLowerInvariant() switch
        {
            "4x4" => (480, 270),
            "3x3" => (640, 360),
            _ => (960, 540)
        };
    }

    private static (int Width, int Height) NextFfmpegRenderSizeForViewport(
        string? layoutKind,
        double viewportPixelWidth,
        double viewportPixelHeight)
    {
        var baseline = NextFfmpegRenderSizeFor(layoutKind);
        var highDpiCeiling = layoutKind?.ToLowerInvariant() switch
        {
            "4x4" => (Width: 640, Height: 360),
            "3x3" => (Width: 960, Height: 540),
            _ => (Width: 1280, Height: 720)
        };
        var requiredWidth = Math.Max(viewportPixelWidth, viewportPixelHeight * 16d / 9d);
        return requiredWidth > baseline.Width * 1.2
            ? highDpiCeiling
            : baseline;
    }

    private (int Width, int Height) CurrentNextFfmpegRenderSizeFor(string? streamId = null)
    {
        var layoutKind = ActiveLayout()?.Kind;
        Grid? surface = null;
        if (!string.IsNullOrWhiteSpace(streamId))
        {
            _tileSurfaceHosts.TryGetValue(streamId, out surface);
        }
        surface ??= _tileSurfaceHosts.Values.FirstOrDefault();
        if (surface is null || surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
        {
            return NextFfmpegRenderSizeFor(layoutKind);
        }

        var dpi = VisualTreeHelper.GetDpi(surface);
        return NextFfmpegRenderSizeForViewport(
            layoutKind,
            surface.ActualWidth * dpi.DpiScaleX,
            surface.ActualHeight * dpi.DpiScaleY);
    }

    private void UpdateNextRenderBackendStatus()
    {
        if (RenderBackendText is null)
        {
            return;
        }

        var entries = new List<(string StreamId, string Decode, string Output, bool Confirmed)>();
        foreach (var (streamId, session) in _ffmpegPlaybackSessions.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add((streamId, session.DecodeStatusText, session.OutputDescription, session.DecodeBackendConfirmed));
        }

        foreach (var streamId in _players.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add((
                streamId,
                GpuRenderingEnabled() ? "DXVA2 · REQUESTED" : "LIBVLC · UNKNOWN",
                "LIBVLC HWND",
                false));
        }

        if (entries.Count == 0)
        {
            SetTextIfChanged(RenderBackendText, "IDLE / —");
            RenderBackendText.Foreground = BrushFromHex("#7D8A94");
            RenderBackendText.ToolTip = "Start playback to inspect the decode backend and output path.";
            return;
        }

        var decodeStates = entries.Select(entry => entry.Decode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var outputStates = entries.Select(entry => entry.Output).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var decode = decodeStates.Count == 1 ? decodeStates[0] : "MIXED";
        var output = outputStates.Count == 1 ? outputStates[0] : "MIXED";
        SetTextIfChanged(RenderBackendText, $"{decode} / {output}");

        var allConfirmed = entries.All(entry => entry.Confirmed);
        var detecting = entries.Any(entry => entry.Decode.Contains("DETECTING", StringComparison.OrdinalIgnoreCase));
        RenderBackendText.Foreground = BrushFromHex(allConfirmed
            ? "#2FD47E"
            : detecting ? "#14B4FF" : "#F5B942");
        var nextTarget = CurrentNextFfmpegRenderSizeFor();
        RenderBackendText.ToolTip = string.Join(
            Environment.NewLine,
            entries.Select(entry => $"{entry.StreamId}: decode {entry.Decode}; output {entry.Output}")) +
            Environment.NewLine +
            $"Current viewport target on the next receiver start: {nextTarget.Width}x{nextTarget.Height}." +
            Environment.NewLine +
            "GPU is marked active only after FFmpeg runtime evidence and a presented frame. REQUESTED or UNKNOWN is not confirmation." +
            Environment.NewLine +
            "For FFmpeg, GPU applies to decode only; scaling, BGR conversion, analysis, and WPF bitmap output still use CPU/memory transfer.";
    }

    private void MainWindow_NextLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNextMeterModeToAll();
        UpdateNextMaximizeGlyph();
        UpdateNextRenderBackendStatus();
        RefreshNextAlertOverlayPositions();
    }

    private void MainWindow_NextActivated(object? sender, EventArgs e)
    {
        RefreshNextAlertOverlayPositions();
    }

    private void MainWindow_NextDeactivated(object? sender, EventArgs e)
    {
        HideNextAlertOverlayWindows();
    }

    private void MainWindow_NextLocationChanged(object? sender, EventArgs e)
    {
        RefreshNextAlertOverlayPositions();
    }

    private void MainWindow_NextSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshNextAlertOverlayPositions();
    }

    private void WindowTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleNextWindowState();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void NextMinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void NextMaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleNextWindowState();
    }

    private void NextCloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_NextStateChanged(object? sender, EventArgs e)
    {
        UpdateNextMaximizeGlyph();
        RefreshNextAlertOverlayPositions();
    }

    private void ToggleNextWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateNextMaximizeGlyph()
    {
        if (NextMaximizeGlyph is null)
        {
            return;
        }

        NextMaximizeGlyph.Text = WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
    }
}
