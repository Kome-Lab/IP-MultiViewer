using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPMultiViewer.Core.Audio;
using IPMultiViewer.Desktop;

internal static class Program
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

    [STAThread]
    private static int Main(string[] args)
    {
        var previousDataDir = Environment.GetEnvironmentVariable("IPMV_DATA_DIR");
        var previousForceFfmpeg = Environment.GetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK");
        var previousDisableInitialPlayback = Environment.GetEnvironmentVariable("IPMV_DISABLE_INITIAL_PLAYBACK");
        var qaDataDir = Path.Combine(Path.GetTempPath(), $"ipmv-windows-qa-{Guid.NewGuid():N}");
        var outputPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(Path.GetTempPath(), "ipmv-windows-implementation.png");
        var peakOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(outputPath)}-peak{Path.GetExtension(outputPath)}");
        var alertOverlayOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(outputPath)}-alert-overlay{Path.GetExtension(outputPath)}");
        var settingsOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(outputPath)}-settings{Path.GetExtension(outputPath)}");
        var alertHistoryOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(outputPath)}-alert-history{Path.GetExtension(outputPath)}");
        var screenCaptureEnabled = string.Equals(
            Environment.GetEnvironmentVariable("IPMV_QA_SCREEN_CAPTURE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
        var senders = new List<Process>();
        Dictionary<string, object>? liveSessionBaseline = null;
        MainWindow? window = null;

        try
        {
            Environment.SetEnvironmentVariable("IPMV_DATA_DIR", qaDataDir);
            Environment.SetEnvironmentVariable("IPMV_DISABLE_INITIAL_PLAYBACK", "1");
            AssertPreviewConfigPromotion();
            var liveFixtureMayBeSkipped = string.Equals(
                Environment.GetEnvironmentVariable("IPMV_SKIP_LIVE_METER_QA"),
                "1",
                StringComparison.OrdinalIgnoreCase);
            var ffmpeg = FindFfmpeg();
            var ports = !liveFixtureMayBeSkipped && ffmpeg is not null && FfmpegSupportsProtocol(ffmpeg, "srt")
                ? FreeUdpPorts(4)
                : [];
            var liveFixtureEnabled = !liveFixtureMayBeSkipped && ffmpeg is not null && ports.Length == 4;
            var includeAlertFixture = !string.Equals(
                Environment.GetEnvironmentVariable("IPMV_QA_NO_ALERT"),
                "1",
                StringComparison.OrdinalIgnoreCase);
            if (!liveFixtureEnabled && !liveFixtureMayBeSkipped)
            {
                throw new InvalidOperationException(
                    "Windows QA requires an FFmpeg build with SRT support for the live per-channel meter gate. Set IPMV_SKIP_LIVE_METER_QA=1 only for an explicitly reduced UI-only check.");
            }

            if (liveFixtureEnabled)
            {
                Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", "1");
                WriteLiveFixtureConfig(ports, includeAlertFixture);
            }

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/IPMultiViewer;component/DirectMatrixTheme.xaml",
                    UriKind.Absolute)
            });

            window = new MainWindow
            {
                Width = 1642,
                Height = 958,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = screenCaptureEnabled ? 20 : -20000,
                Top = screenCaptureEnabled ? 20 : -20000,
                ShowActivated = true,
                ShowInTaskbar = screenCaptureEnabled
            };
            window.Show();
            window.Activate();
            Pump(window, 900);

            if (liveFixtureEnabled)
            {
                Named<Button>(window, "StartPlaybackButton")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var waitingListenerSessions = WaitForIdleListenerReceivers(
                    window,
                    expected: 4,
                    timeout: TimeSpan.FromSeconds(4));
                AssertIdleListenerReceiversRemainRegistered(
                    window,
                    waitingListenerSessions,
                    duration: TimeSpan.FromSeconds(6));
                senders.AddRange(StartLiveFixtureSenders(ffmpeg!, ports, fixtureOffset: 0));
                WaitForVisibleAudioMeters(window, 4, TimeSpan.FromSeconds(10));
                AssertFfmpegSessionsPreserved(
                    window,
                    waitingListenerSessions,
                    expectedWidth: 640,
                    expectedHeight: 360);
                AssertPresentedAudioVideoPtsAligned(window, expectedStreams: 4);
                AssertLiveChannelMetersMove(window, expectedStreams: 4, expectedChannels: 4);
                AssertVisibleMeterTracksVideoPulse(window, "qa-01");
                AssertNextMeterUsesVerticalSpace(window);
                AssertVisibleMeterPanelsFit(window);
                AssertFfmpegSessionTier(window, expectedWidth: 640, expectedHeight: 360, expectedMinimumCount: 4);
                liveSessionBaseline = FfmpegSessionSnapshot(window);
                AssertRepeatedStartPreservesFfmpegSessions(window, liveSessionBaseline, expectedWidth: 640, expectedHeight: 360);
                AssertRenderBackendIndicator(window, expectedOutput: "WPF 640x360");
                if (includeAlertFixture)
                {
                    AssertAlertOverlayDoesNotResizeSurface(window, "qa-04", alertOverlayOutputPath);
                }
            }

            AssertRequiredNamedControls(window);
            AssertDisplayedApplicationVersion(window);
            AssertAdaptiveRenderTierContract();
            AssertPaddedContentAnalysisContract();
            AssertHardwareBackendEvidenceContract();
            SwitchLayout(window, "3x3", 3);
            AssertNextTileFactoryIsActive(window);
            AssertMeterToggle(window, liveFixtureEnabled, peakOutputPath);
            AssertSixteenChannelPanelFits(window);
            Capture(window, outputPath);
            AssertLayout(window, "4x4", 4);
            AssertVisibleMeterPanelsFit(window);
            if (liveSessionBaseline is not null)
            {
                AssertFfmpegSessionsPreserved(window, liveSessionBaseline, expectedWidth: 640, expectedHeight: 360);
            }
            AssertLayout(window, "2x2", 2);
            if (liveSessionBaseline is not null)
            {
                AssertFfmpegSessionsPreserved(window, liveSessionBaseline, expectedWidth: 640, expectedHeight: 360);
            }

            CaptureAuxiliaryWindows(window, settingsOutputPath, alertHistoryOutputPath);

            window.Close();
            window = null;
            Pump(null, 100);
            Console.WriteLine($"windows_ui_qa_ok live_meter={(liveFixtureEnabled ? "enabled" : "skipped")} vu={outputPath} peak={peakOutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (window is not null)
            {
                try
                {
                    window.Close();
                    Pump(null, 100);
                }
                catch
                {
                    // Receiver cleanup must not hide the original QA failure.
                }
            }

            foreach (var sender in senders)
            {
                StopProcess(sender);
            }

            Environment.SetEnvironmentVariable("IPMV_DATA_DIR", previousDataDir);
            Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", previousForceFfmpeg);
            Environment.SetEnvironmentVariable("IPMV_DISABLE_INITIAL_PLAYBACK", previousDisableInitialPlayback);
            try
            {
                if (Directory.Exists(qaDataDir))
                {
                    Directory.Delete(qaDataDir, recursive: true);
                }
            }
            catch
            {
                // QA cleanup must not hide a more useful test failure.
            }
        }
    }

    private static void AssertRequiredNamedControls(MainWindow window)
    {
        var names = new[]
        {
            "ApplicationVersionText",
            "ModeText",
            "PlaybackSummaryText",
            "RenderBackendText",
            "MainLayoutCombo",
            "StartPlaybackButton",
            "StopPlaybackButton",
            "OpenAlertHistoryButton",
            "OpenSettingsButton",
            "NextMeterVuButton",
            "NextMeterPeakButton",
            "TileGrid",
            "FooterText"
        };

        foreach (var name in names)
        {
            if (window.FindName(name) is null)
            {
                throw new InvalidOperationException($"Required named control was not created: {name}.");
            }
        }
    }

    private static void AssertDisplayedApplicationVersion(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod(
            "CurrentApplicationVersion",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CurrentApplicationVersion was not available.");
        var softwareVersion = method.Invoke(null, null) as string
            ?? throw new InvalidOperationException("CurrentApplicationVersion did not return a string.");
        var displayedVersion = Named<TextBlock>(window, "ApplicationVersionText").Text;
        if (!string.Equals(displayedVersion, $"v{softwareVersion}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Displayed application version was '{displayedVersion}'; expected 'v{softwareVersion}'.");
        }
    }

    private static void SwitchLayout(MainWindow window, string kind, int size)
    {
        var combo = Named<ComboBox>(window, "MainLayoutCombo");
        var layout = combo.Items
            .Cast<LayoutConfig>()
            .FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Layout {kind} was not available.");
        combo.SelectedItem = layout;
        Pump(window, 500);
        AssertTileGrid(window, size);
    }

    private static void AssertLayout(MainWindow window, string kind, int size)
    {
        SwitchLayout(window, kind, size);
        window.UpdateLayout();
    }

    private static void AssertTileGrid(MainWindow window, int size)
    {
        var tileGrid = Named<UniformGrid>(window, "TileGrid");
        if (tileGrid.Rows != size ||
            tileGrid.Columns != size ||
            tileGrid.Children.Count != size * size)
        {
            throw new InvalidOperationException(
                $"Expected {size}x{size} tiles, got {tileGrid.Rows}x{tileGrid.Columns} with {tileGrid.Children.Count} children.");
        }
    }

    private static void AssertNextTileFactoryIsActive(MainWindow window)
    {
        var tileGrid = Named<UniformGrid>(window, "TileGrid");
        if (tileGrid.Children.Count == 0 ||
            tileGrid.Children[0] is not Border tile ||
            Math.Abs(tile.CornerRadius.TopLeft - 4) > 0.01)
        {
            throw new InvalidOperationException(
                "The current Direct Matrix tile factory is not active.");
        }
    }

    private static void AssertMeterToggle(MainWindow window, bool liveFixtureEnabled, string peakOutputPath)
    {
        var vu = Named<Button>(window, "NextMeterVuButton");
        var peak = Named<Button>(window, "NextMeterPeakButton");
        vu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window, 350);
        var vuSelected = ColorOf(vu.Background);
        if (vuSelected == ColorOf(peak.Background))
        {
            throw new InvalidOperationException("VU and PEAK both appeared selected before the toggle.");
        }

        if (liveFixtureEnabled)
        {
            AssertMeterTargetsMatchMode(window, MeterDisplayMode.Vu);
        }

        peak.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window, 350);
        var peakSelected = ColorOf(peak.Background);
        if (vuSelected != peakSelected)
        {
            throw new InvalidOperationException("VU and PEAK did not share the selected-state treatment.");
        }

        if (ColorOf(vu.Background) == peakSelected)
        {
            throw new InvalidOperationException("VU remained selected after switching to PEAK.");
        }

        if (!string.Equals(LocalConfigStore.LoadOrCreate().AudioMeterMode, "peak", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PEAK selection was not persisted to the application config.");
        }

        AssertPeakMarkerVisibility(window, Visibility.Visible);
        if (liveFixtureEnabled)
        {
            AssertMeterTargetsMatchMode(window, MeterDisplayMode.Peak);
        }

        Capture(window, peakOutputPath);

        vu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window, 350);
        if (!string.Equals(LocalConfigStore.LoadOrCreate().AudioMeterMode, "vu", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("VU selection was not persisted to the application config.");
        }

        AssertPeakMarkerVisibility(window, Visibility.Collapsed);
        if (!string.Equals(Path.GetFileName(LocalConfigStore.DefaultConfigPath), "local-config.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The released application must use local-config.json.");
        }
    }

    private static void AssertMeterTargetsMatchMode(MainWindow window, MeterDisplayMode mode)
    {
        var viewsByStream = (IDictionary)PrivateField(window, "_tileAudioMeterViews");
        var hostsByStream = (IDictionary)PrivateField(window, "_tileAudioMeterHosts");
        var views = viewsByStream.Keys
            .Cast<object>()
            .Where(key =>
                hostsByStream[key] is FrameworkElement host &&
                host.Visibility == Visibility.Visible)
            .SelectMany(key => ((IEnumerable)viewsByStream[key]!).Cast<object>())
            .ToArray();
        if (views.Length == 0)
        {
            throw new InvalidOperationException($"No live channel meters were available for {mode} scaling verification.");
        }

        foreach (var view in views)
        {
            var type = view.GetType();
            var inputProperty = mode == MeterDisplayMode.Peak ? "LastPeakDb" : "LastLevelDb";
            var inputDb = Convert.ToDouble(type.GetProperty(inputProperty)!.GetValue(view));
            var actual = Convert.ToDouble(type.GetProperty("TargetLevelRatio")!.GetValue(view));
            var expected = AudioMeterScale.GetRatio(inputDb, mode);
            if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 0.000001)
            {
                throw new InvalidOperationException(
                    $"Live channel meter target {actual:0.000000} did not match {mode} scaling {expected:0.000000} for {inputDb:0.0} dBFS.");
            }
        }
    }

    private static void AssertPeakMarkerVisibility(MainWindow window, Visibility expected)
    {
        var viewsByStream = (IDictionary)PrivateField(window, "_tileAudioMeterViews");
        foreach (var view in viewsByStream.Values.Cast<IEnumerable>().SelectMany(views => views.Cast<object>()))
        {
            var marker = (Border)view.GetType().GetProperty("PeakMarker")!.GetValue(view)!;
            if (marker.Visibility != expected)
            {
                throw new InvalidOperationException($"Expected peak marker visibility {expected}, got {marker.Visibility}.");
            }
        }
    }

    private static void WriteLiveFixtureConfig(IReadOnlyList<int> ports, bool includeAlertFixture)
    {
        var config = new LocalConfig
        {
            ActiveLayoutId = "qa-3x3",
            ClientName = "IP MultiViewer Windows QA",
            AudioMeterMode = "vu"
        };
        var layout = new LayoutConfig
        {
            Id = "qa-3x3",
            Name = "Default 3x3",
            Kind = "3x3"
        };

        for (var index = 0; index < 9; index++)
        {
            var enabled = index < ports.Count;
            var streamId = $"qa-{index + 1:00}";
            config.Streams.Add(new StreamConfig
            {
                Id = streamId,
                Name = enabled ? $"Test_SRT_{index + 1}" : $"Spare_{index + 1}",
                Enabled = enabled,
                InputUrl = enabled
                    ? $"srt://127.0.0.1:{ports[index]}?mode=listener&transtype=live"
                    : "",
                InputProtocol = "srt",
                Group = "QA",
                AnalysisEnabled = false,
                AudioMonitorEnabled = true
            });
            layout.Tiles.Add(new LayoutTileConfig
            {
                Index = index,
                StreamId = streamId,
                Row = index / 3,
                Column = index % 3
            });
        }

        config.Layouts.Add(layout);
        if (includeAlertFixture)
        {
            config.AlertHistory.Add(new AlertRecord
            {
                Id = "qa-alert-04",
                StreamId = "qa-04",
                Type = "qa.overlay",
                Severity = "error",
                Status = "open",
                Message = "QA alert overlay remains actionable above the video surface",
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                SourceClientId = config.ClientId
            });
        }
        LocalConfigStore.Save(config);
    }

    private static void AssertPreviewConfigPromotion()
    {
        var previewPath = Path.Combine(LocalConfigStore.ConfigDirectory, "local-config.next.json");
        var preview = new LocalConfig
        {
            ClientId = "preview-promotion-contract",
            ClientName = "Preview promotion contract",
            ActiveLayoutId = "preview-layout",
            AudioMeterMode = "vu"
        };
        preview.Streams.Add(new StreamConfig
        {
            Id = "preview-stream",
            Name = "Preview stream",
            Enabled = false,
            InputProtocol = "rtsp",
            InputUrl = "rtsp://preview.example.test/live"
        });
        preview.Layouts.Add(new LayoutConfig
        {
            Id = "preview-layout",
            Name = "Preview layout",
            Kind = "1x1",
            Tiles = { new LayoutTileConfig { Index = 0, StreamId = "preview-stream" } }
        });
        LocalConfigStore.SaveTo(preview, previewPath);
        var previewBytes = File.ReadAllBytes(previewPath);

        var migrated = LocalConfigStore.LoadOrCreate();
        if (!File.Exists(LocalConfigStore.DefaultConfigPath) ||
            !string.Equals(migrated.ClientId, preview.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The released application did not promote the preview config to local-config.json.");
        }

        migrated.AudioMeterMode = "peak";
        migrated.ClientName = "Released app changed copy";
        LocalConfigStore.Save(migrated);
        if (!previewBytes.SequenceEqual(File.ReadAllBytes(previewPath)))
        {
            throw new InvalidOperationException("Saving local-config.json modified the preview migration source.");
        }
    }

    private static Dictionary<string, object> WaitForIdleListenerReceivers(
        MainWindow window,
        int expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var registered = 0;
        var availableStartupSlots = 0;
        var startInFlight = true;
        while (DateTime.UtcNow < deadline)
        {
            Pump(window, 100);
            var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
            var startupSlots = (SemaphoreSlim)PrivateField(window, "_playbackStartupSlots");
            registered = sessions.Count;
            availableStartupSlots = startupSlots.CurrentCount;
            startInFlight = (bool)PrivateField(window, "_playbackStartInFlight");
            if (registered == expected && availableStartupSlots == 2 && !startInFlight)
            {
                var snapshot = FfmpegSessionSnapshot(window);
                AssertIdleListenerSessionSet(window, snapshot);
                Console.WriteLine(
                    $"listener_receivers_ready_without_senders_ok registered={registered} available_startup_slots={availableStartupSlots}");
                return snapshot;
            }
        }

        throw new InvalidOperationException(
            $"Timed out waiting for {expected} senderless SRT listeners to register and release both startup slots: " +
            $"registered={registered}, available_startup_slots={availableStartupSlots}, start_in_flight={startInFlight}.");
    }

    private static void AssertIdleListenerReceiversRemainRegistered(
        MainWindow window,
        IReadOnlyDictionary<string, object> baseline,
        TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            Pump(window, 100);
            AssertIdleListenerSessionSet(window, baseline);
            var startupSlots = (SemaphoreSlim)PrivateField(window, "_playbackStartupSlots");
            if (startupSlots.CurrentCount != 2 || (bool)PrivateField(window, "_playbackStartInFlight"))
            {
                throw new InvalidOperationException(
                    "Senderless SRT listeners retained a startup slot after registration.");
            }
        }

        Console.WriteLine(
            $"listener_receivers_waited_for_senders_ok streams={baseline.Count} duration_ms={stopwatch.Elapsed.TotalMilliseconds:0}");
    }

    private static void AssertIdleListenerSessionSet(
        MainWindow window,
        IReadOnlyDictionary<string, object> baseline)
    {
        var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
        if (sessions.Count != baseline.Count)
        {
            throw new InvalidOperationException(
                $"Senderless SRT listener count changed while waiting: {baseline.Count} -> {sessions.Count}.");
        }

        foreach (var (streamId, original) in baseline)
        {
            if (!sessions.Contains(streamId) || !ReferenceEquals(sessions[streamId], original))
            {
                throw new InvalidOperationException(
                    $"Senderless SRT listener {streamId} was stopped or replaced before its sender connected.");
            }

            var type = original.GetType();
            var state = Convert.ToString(type.GetProperty("State")!.GetValue(original)) ?? "unknown";
            var hasFrame = (bool)type.GetProperty("HasFrame")!.GetValue(original)!;
            if (!string.Equals(state, "opening", StringComparison.OrdinalIgnoreCase) || hasFrame)
            {
                throw new InvalidOperationException(
                    $"Senderless SRT listener {streamId} left its idle receive-ready state: state={state}, has_frame={hasFrame}.");
            }
        }
    }

    private static void WaitForVisibleAudioMeters(MainWindow window, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Pump(window, 150);
            var hosts = (IDictionary)PrivateField(window, "_tileAudioMeterHosts");
            var visible = hosts.Values.Cast<Grid>().Count(host => host.Visibility == Visibility.Visible && host.Children.Count > 0);
            if (visible >= expected)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Timed out waiting for {expected} visible per-channel audio meters.");
    }

    private static void AssertLiveChannelMetersMove(MainWindow window, int expectedStreams, int expectedChannels)
    {
        var samples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        for (var sampleIndex = 0; sampleIndex < 20; sampleIndex++)
        {
            Pump(window, 140);
            foreach (var (key, ratio) in LiveChannelRatios(window))
            {
                if (!samples.TryGetValue(key, out var values))
                {
                    values = [];
                    samples[key] = values;
                }

                values.Add(ratio);
            }
        }

        if (samples.Count != expectedStreams * expectedChannels)
        {
            throw new InvalidOperationException(
                $"Expected {expectedStreams} streams x {expectedChannels} independent channel meters, got {samples.Count}.");
        }

        var stationary = samples
            .Where(entry =>
                !entry.Key.StartsWith("qa-01:", StringComparison.OrdinalIgnoreCase) &&
                (entry.Value.Count < 2 || entry.Value.Max() - entry.Value.Min() <= 0.005))
            .Select(entry => entry.Key)
            .ToArray();
        if (stationary.Length > 0)
        {
            throw new InvalidOperationException(
                $"Per-channel meters did not visibly move over time: {string.Join(", ", stationary)}.");
        }
    }

    private static void AssertPresentedAudioVideoPtsAligned(MainWindow window, int expectedStreams)
    {
        var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
        if (sessions.Count < expectedStreams)
        {
            throw new InvalidOperationException($"Expected {expectedStreams} FFmpeg sessions for A/V sync QA, got {sessions.Count}.");
        }

        foreach (DictionaryEntry entry in sessions)
        {
            var session = entry.Value!;
            var type = session.GetType();
            var videoPts = double.NaN;
            var audioPts = double.NaN;
            var offsetMs = double.NaN;
            var aligned = false;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            do
            {
                var audioArgs = new object?[] { null };
                var hasAudio = (bool)type.GetMethod("TryGetAudioSnapshot")!.Invoke(session, audioArgs)!;
                videoPts = Convert.ToDouble(type.GetProperty("LastPresentedVideoPtsSeconds")!.GetValue(session));
                audioPts = Convert.ToDouble(type.GetProperty("LastPresentedAudioPtsSeconds")!.GetValue(session));
                offsetMs = Convert.ToDouble(type.GetProperty("LastAudioVideoSyncOffsetMs")!.GetValue(session));
                aligned = hasAudio &&
                    double.IsFinite(videoPts) &&
                    double.IsFinite(audioPts) &&
                    double.IsFinite(offsetMs) &&
                    offsetMs >= -30 &&
                    offsetMs <= 150;
                if (!aligned)
                {
                    Pump(window, 50);
                }
            }
            while (!aligned && DateTime.UtcNow < deadline);

            if (!double.IsFinite(videoPts) ||
                !double.IsFinite(audioPts) ||
                !double.IsFinite(offsetMs) ||
                offsetMs < -30 ||
                offsetMs > 150)
            {
                throw new InvalidOperationException(
                    $"{entry.Key} A/V sync offset = {offsetMs:0.0}ms (video={videoPts:0.000}, audio={audioPts:0.000}); expected -30..150ms.");
            }
        }
    }

    private static void AssertVisibleMeterTracksVideoPulse(MainWindow window, string streamId)
    {
        var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
        var session = sessions[streamId]
            ?? throw new InvalidOperationException($"Synchronized pulse session {streamId} was not available.");
        var bitmap = (WriteableBitmap)session.GetType().GetProperty("Bitmap")!.GetValue(session)!;
        var viewsByStream = (IDictionary)PrivateField(window, "_tileAudioMeterViews");
        var views = ((IEnumerable)(viewsByStream[streamId]
            ?? throw new InvalidOperationException($"Synchronized pulse meter {streamId} was not available."))).Cast<object>().ToArray();
        var firstView = views.OrderBy(view => Convert.ToInt32(view.GetType().GetProperty("Channel")!.GetValue(view))).First();
        var displayedLevel = firstView.GetType().GetProperty("DisplayedLevelRatio")!;
        var videoRises = new List<DateTimeOffset>();
        var videoFalls = new List<DateTimeOffset>();
        var meterRises = new List<DateTimeOffset>();
        var meterFalls = new List<DateTimeOffset>();
        var pixel = new byte[3];
        bool? previousBright = null;
        bool? previousLoud = null;
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt.AddSeconds(6.5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Pump(window, 20);
            bitmap.CopyPixels(
                new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1),
                pixel,
                stride: 3,
                offset: 0);
            var bright = (pixel[0] + pixel[1] + pixel[2]) / 3.0 >= 180;
            var loud = Convert.ToDouble(displayedLevel.GetValue(firstView)) >= 0.20;
            var now = DateTimeOffset.UtcNow;
            if (previousBright == false && bright && now - startedAt >= TimeSpan.FromMilliseconds(500))
            {
                videoRises.Add(now);
            }
            else if (previousBright == true && !bright && now - startedAt >= TimeSpan.FromMilliseconds(500))
            {
                videoFalls.Add(now);
            }

            if (previousLoud == false && loud && now - startedAt >= TimeSpan.FromMilliseconds(500))
            {
                meterRises.Add(now);
            }
            else if (previousLoud == true && !loud && now - startedAt >= TimeSpan.FromMilliseconds(500))
            {
                meterFalls.Add(now);
            }

            previousBright = bright;
            previousLoud = loud;
        }

        var riseOffsetsMs = PairPulseEdges(videoRises, meterRises);
        var fallOffsetsMs = PairPulseEdges(videoFalls, meterFalls);
        if (riseOffsetsMs.Count < 3 || fallOffsetsMs.Count < 3)
        {
            throw new InvalidOperationException(
                "Synchronized pulse QA did not observe enough matched edges: " +
                $"rises={riseOffsetsMs.Count} (video={videoRises.Count}, meter={meterRises.Count}), " +
                $"falls={fallOffsetsMs.Count} (video={videoFalls.Count}, meter={meterFalls.Count}).");
        }

        var offsetsMs = riseOffsetsMs.Concat(fallOffsetsMs).ToArray();
        var orderedAbsoluteOffsets = offsetsMs.Select(Math.Abs).OrderBy(value => value).ToArray();
        var medianAbsoluteOffset = orderedAbsoluteOffsets[orderedAbsoluteOffsets.Length / 2];
        var maximumAbsoluteOffset = orderedAbsoluteOffsets[^1];
        Console.WriteLine(
            $"windows_av_sync_ok pairs={offsetsMs.Length} rise_pairs={riseOffsetsMs.Count} fall_pairs={fallOffsetsMs.Count} " +
            $"median_abs_ms={medianAbsoluteOffset:0.0} max_abs_ms={maximumAbsoluteOffset:0.0}");
        if (medianAbsoluteOffset > 120 || maximumAbsoluteOffset > 250)
        {
            throw new InvalidOperationException(
                $"Visible meter/video pulse drift exceeded QA limits: median={medianAbsoluteOffset:0.0}ms, max={maximumAbsoluteOffset:0.0}ms.");
        }
    }

    private static List<double> PairPulseEdges(
        IReadOnlyList<DateTimeOffset> videoEdges,
        IReadOnlyList<DateTimeOffset> meterEdges)
    {
        var offsetsMs = new List<double>();
        var usedMeterEdges = new HashSet<int>();
        foreach (var videoEdge in videoEdges)
        {
            var candidate = meterEdges
                .Select((time, index) => new { Index = index, OffsetMs = (time - videoEdge).TotalMilliseconds })
                .Where(item => !usedMeterEdges.Contains(item.Index) && Math.Abs(item.OffsetMs) <= 500)
                .OrderBy(item => Math.Abs(item.OffsetMs))
                .FirstOrDefault();
            if (candidate is null)
            {
                continue;
            }

            usedMeterEdges.Add(candidate.Index);
            offsetsMs.Add(candidate.OffsetMs);
        }

        return offsetsMs;
    }

    private static IEnumerable<(string Key, double Ratio)> LiveChannelRatios(MainWindow window)
    {
        var viewsByStream = (IDictionary)PrivateField(window, "_tileAudioMeterViews");
        foreach (DictionaryEntry entry in viewsByStream)
        {
            var streamId = Convert.ToString(entry.Key) ?? "unknown";
            foreach (var view in ((IEnumerable)entry.Value!).Cast<object>())
            {
                var type = view.GetType();
                var channel = Convert.ToInt32(type.GetProperty("Channel")!.GetValue(view));
                var ratio = Convert.ToDouble(type.GetProperty("TargetLevelRatio")!.GetValue(view));
                yield return ($"{streamId}:CH{channel}", ratio);
            }
        }
    }

    private static void AssertVisibleMeterPanelsFit(MainWindow window)
    {
        var hosts = (IDictionary)PrivateField(window, "_tileAudioMeterHosts");
        foreach (var host in hosts.Values.Cast<Grid>().Where(host => host.Visibility == Visibility.Visible))
        {
            foreach (var panel in host.Children.OfType<Viewbox>())
            {
                if (panel.ActualHeight > host.ActualHeight + 0.5 || panel.ActualWidth > host.ActualWidth + 0.5)
                {
                    throw new InvalidOperationException(
                        $"Meter panel overflowed its tile host: panel={panel.ActualWidth:0.0}x{panel.ActualHeight:0.0}, host={host.ActualWidth:0.0}x{host.ActualHeight:0.0}.");
                }
            }
        }

        var surfaces = (IDictionary)PrivateField(window, "_tileSurfaceHosts");
        foreach (var surface in surfaces.Values.Cast<Grid>().Where(surface => surface.Margin.Left > 0 || surface.Margin.Right > 0))
        {
            var totalWidth = surface.ActualWidth + surface.Margin.Left + surface.Margin.Right;
            if (totalWidth > 0 && surface.ActualWidth < totalWidth * 0.35)
            {
                throw new InvalidOperationException(
                    $"Meter reserves left too little video width: {surface.ActualWidth:0.0} of {totalWidth:0.0}px.");
            }
        }
    }

    private static void AssertNextMeterUsesVerticalSpace(MainWindow window)
    {
        var meterHeight = Convert.ToDouble(
            typeof(MainWindow).GetField("AudioMeterHeight", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue(),
            System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(meterHeight - 152) > 0.01)
        {
            throw new InvalidOperationException($"Next audio meter height regressed to {meterHeight:0.0} DIP; want 152 DIP.");
        }

        var viewsByStream = (IDictionary)PrivateField(window, "_tileAudioMeterViews");
        var trackHeights = viewsByStream.Values
            .Cast<IEnumerable>()
            .SelectMany(views => views.Cast<object>())
            .Select(view => (Border)view.GetType().GetProperty("Track")!.GetValue(view)!)
            .Select(track => track.ActualHeight)
            .ToArray();
        if (trackHeights.Length == 0 || trackHeights.Any(height => height < 148))
        {
            throw new InvalidOperationException(
                $"The 3x3 live meter did not use the available vertical space: {string.Join(", ", trackHeights.Select(height => height.ToString("0.0")))}.");
        }
    }

    private static void AssertAlertOverlayDoesNotResizeSurface(
        MainWindow window,
        string streamId,
        string alertOverlayOutputPath)
    {
        var stacks = (IDictionary)PrivateField(window, "_tileAlertStacks");
        var surfaces = (IDictionary)PrivateField(window, "_tileSurfaceHosts");
        var overlayWindows = (IDictionary)PrivateField(window, "_nextAlertOverlayWindows");
        var overlayTargets = (IDictionary)PrivateField(window, "_nextAlertOverlayTargets");
        var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
        if (stacks[streamId] is not StackPanel stack || stack.Children.Count == 0)
        {
            throw new InvalidOperationException("The live alert fixture did not render an alert banner.");
        }

        if (surfaces[streamId] is not Grid surface || Math.Abs(surface.Margin.Top) > 0.01)
        {
            throw new InvalidOperationException(
                "An alert changed the video surface margin. The video viewport must remain fixed.");
        }

        if (overlayWindows.Contains(streamId) || overlayTargets.Contains(streamId))
        {
            throw new InvalidOperationException(
                "The FFmpeg/SRT alert unexpectedly created a top-level overlay window.");
        }

        if (stack.Parent is not Panel inlineHost ||
            !inlineHost.Children.Contains(surface) ||
            Panel.GetZIndex(stack) <= Panel.GetZIndex(surface))
        {
            throw new InvalidOperationException(
                "The FFmpeg/SRT alert was not rendered inline above its video surface.");
        }

        var initialWidth = surface.ActualWidth;
        var initialHeight = surface.ActualHeight;
        var initialMargin = surface.Margin;
        var initialOrigin = surface.TranslatePoint(new Point(0, 0), window);
        var initialSession = sessions[streamId]
            ?? throw new InvalidOperationException("The alert fixture FFmpeg session was not available.");
        var initialOutputWidth = Convert.ToInt32(initialSession.GetType().GetProperty("OutputWidth")!.GetValue(initialSession));
        var initialOutputHeight = Convert.ToInt32(initialSession.GetType().GetProperty("OutputHeight")!.GetValue(initialSession));
        var config = (LocalConfig)PrivateField(window, "_config");
        var alert = config.AlertHistory.First(item => string.Equals(item.StreamId, streamId, StringComparison.OrdinalIgnoreCase));
        var renderAlertOverlay = typeof(MainWindow).GetMethod(
            "RenderAlertOverlay",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RenderAlertOverlay was not available.");

        var audioSnapshotField = initialSession.GetType().GetField("_audioSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FFmpeg audio snapshot field was not available.");
        var updateAudioMeter = typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "UpdateAudioMeterTargets" && method.GetParameters().Length == 2);
        var savedAudioSnapshot = audioSnapshotField.GetValue(initialSession);
        audioSnapshotField.SetValue(initialSession, null);
        updateAudioMeter.Invoke(window, [streamId, true]);
        AssertSurfaceGeometry(window, surface, initialWidth, initialHeight, initialMargin, initialOrigin, "audio snapshot loss");
        audioSnapshotField.SetValue(initialSession, savedAudioSnapshot);
        updateAudioMeter.Invoke(window, [streamId, true]);
        AssertSurfaceGeometry(window, surface, initialWidth, initialHeight, initialMargin, initialOrigin, "audio snapshot recovery");

        alert.Status = "resolved";
        renderAlertOverlay.Invoke(window, [streamId, true, null]);
        Pump(window, 200);
        AssertSurfaceGeometry(window, surface, initialWidth, initialHeight, initialMargin, initialOrigin, "resolving an alert");
        if (stack.Children.Count != 0 || overlayWindows.Contains(streamId))
        {
            throw new InvalidOperationException("The alert overlay stayed open after the alert was resolved.");
        }
        var noAlertOutputPath = alertOverlayOutputPath.Replace("-alert-overlay", "-no-alert", StringComparison.OrdinalIgnoreCase);
        Capture(window, noAlertOutputPath);

        alert.Status = "open";
        renderAlertOverlay.Invoke(window, [streamId, true, null]);
        Pump(window, 200);
        AssertSurfaceGeometry(window, surface, initialWidth, initialHeight, initialMargin, initialOrigin, "reopening an alert");
        if (stack.Children.Count == 0 || overlayWindows.Contains(streamId))
        {
            throw new InvalidOperationException(
                "The inline alert did not reopen without creating a top-level window or resizing the video.");
        }

        CaptureAlertOverlayContent(stack, alertOverlayOutputPath);

        var foregroundProbe = CreateAlertForegroundProbe(surface);
        Pump(window, 100);
        var foregroundExpectation = CaptureUnrelatedForeground(foregroundProbe, window);
        AssertForegroundUnchanged(foregroundExpectation, window, "initial unrelated window coverage");

        alert.Status = "resolved";
        renderAlertOverlay.Invoke(window, [streamId, true, null]);
        AssertForegroundUnchanged(foregroundExpectation, window, "synchronous inactive alert resolution");
        Pump(window, 100);
        if (stack.Children.Count != 0 || overlayWindows.Contains(streamId))
        {
            throw new InvalidOperationException("The inactive inline alert stayed rendered after it was resolved.");
        }
        AssertForegroundUnchanged(foregroundExpectation, window, "inactive alert resolution");

        alert.Status = "open";
        renderAlertOverlay.Invoke(window, [streamId, true, null]);
        AssertForegroundUnchanged(foregroundExpectation, window, "synchronous inactive alert reopen");
        Pump(window, 150);
        if (stack.Children.Count == 0 || overlayWindows.Contains(streamId))
        {
            throw new InvalidOperationException(
                "The inactive FFmpeg/SRT alert did not stay inline or unexpectedly created a top-level window.");
        }
        AssertForegroundUnchanged(foregroundExpectation, window, "alert opened while inactive");
        AssertSurfaceGeometry(window, surface, initialWidth, initialHeight, initialMargin, initialOrigin, "inactive alert reopen");

        var explicitlyReactivated = TryActivateMonitoringWindow(window);
        if (explicitlyReactivated && foregroundProbe.IsActive)
        {
            throw new InvalidOperationException("The foreground probe stayed active after explicit monitoring-window activation.");
        }
        if (stack.Children.Count == 0 || overlayWindows.Contains(streamId))
        {
            throw new InvalidOperationException("The inline alert disappeared when the monitoring window was explicitly activated.");
        }
        if (!explicitlyReactivated)
        {
            AssertForegroundUnchanged(
                foregroundExpectation,
                window,
                "blocked explicit monitoring-window activation",
                requireMonitoringInactive: false);
        }
        foregroundProbe.Close();

        AssertSyntheticAirspaceAlertIsActiveOnly(window, surface, stacks, overlayWindows, overlayTargets);

        var currentSession = sessions[streamId]
            ?? throw new InvalidOperationException("The alert fixture FFmpeg session disappeared.");
        var currentOutputWidth = Convert.ToInt32(currentSession.GetType().GetProperty("OutputWidth")!.GetValue(currentSession));
        var currentOutputHeight = Convert.ToInt32(currentSession.GetType().GetProperty("OutputHeight")!.GetValue(currentSession));
        if (!ReferenceEquals(initialSession, currentSession) ||
            initialOutputWidth != currentOutputWidth ||
            initialOutputHeight != currentOutputHeight)
        {
            throw new InvalidOperationException("An alert transition restarted or resized the FFmpeg playback session.");
        }
    }

    private static void AssertSyntheticAirspaceAlertIsActiveOnly(
        MainWindow window,
        FrameworkElement target,
        IDictionary alertStacks,
        IDictionary overlayWindows,
        IDictionary overlayTargets)
    {
        const string syntheticStreamId = "qa-airspace-alert";
        var syntheticStack = new StackPanel
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        syntheticStack.Children.Add(new Border
        {
            Background = Brushes.OrangeRed,
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8, 4, 8, 4),
                Text = "QA synthetic airspace alert"
            }
        });
        var syntheticWindow = new Window
        {
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Content = syntheticStack,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None
        };
        var refreshOverlays = typeof(MainWindow).GetMethod(
            "RefreshNextAlertOverlayPositions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RefreshNextAlertOverlayPositions was not available.");

        alertStacks[syntheticStreamId] = syntheticStack;
        overlayWindows[syntheticStreamId] = syntheticWindow;
        overlayTargets[syntheticStreamId] = target;
        Window? foregroundProbe = null;
        try
        {
            var initiallyActive = TryActivateMonitoringWindow(window, 100);
            refreshOverlays.Invoke(window, null);
            Pump(window, 100);
            var mainWindowHandle = new WindowInteropHelper(window).Handle;
            if (initiallyActive &&
                (!syntheticWindow.IsVisible ||
                 syntheticWindow.Owner != window ||
                 syntheticWindow.Topmost ||
                 GetForegroundWindow() != mainWindowHandle ||
                 !window.IsActive))
            {
                throw new InvalidOperationException(
                    "The synthetic airspace alert did not open non-topmost above the explicitly active monitoring window.");
            }
            if (!initiallyActive && syntheticWindow.IsVisible)
            {
                throw new InvalidOperationException(
                    "The synthetic airspace alert opened even though Windows kept the monitoring window inactive.");
            }

            foregroundProbe = CreateAlertForegroundProbe(target);
            Pump(window, 100);
            var foregroundExpectation = CaptureUnrelatedForeground(foregroundProbe, window);
            AssertForegroundUnchanged(foregroundExpectation, window, "airspace alert deactivation");
            if (syntheticWindow.IsVisible)
            {
                throw new InvalidOperationException("The synthetic airspace alert stayed visible over an unrelated foreground window.");
            }

            syntheticStack.Children.Clear();
            refreshOverlays.Invoke(window, null);
            syntheticStack.Children.Add(new Border
            {
                Background = Brushes.OrangeRed,
                Height = 30
            });
            refreshOverlays.Invoke(window, null);
            Pump(window, 150);
            AssertForegroundUnchanged(foregroundExpectation, window, "airspace alert raised while inactive");
            if (syntheticWindow.IsVisible)
            {
                throw new InvalidOperationException(
                    "A newly raised synthetic airspace alert showed its HWND while the monitoring window was inactive.");
            }

            var reactivated = TryActivateMonitoringWindow(window);
            if (reactivated &&
                (!syntheticWindow.IsVisible ||
                 GetForegroundWindow() != mainWindowHandle ||
                 !window.IsActive))
            {
                throw new InvalidOperationException(
                    "The synthetic airspace alert did not return after explicit monitoring-window activation.");
            }
            if (!reactivated)
            {
                AssertForegroundUnchanged(
                    foregroundExpectation,
                    window,
                    "blocked airspace monitoring-window activation",
                    requireMonitoringInactive: false);
                if (syntheticWindow.IsVisible)
                {
                    throw new InvalidOperationException(
                        "The synthetic airspace alert became visible even though the monitoring window remained inactive.");
                }
            }
        }
        finally
        {
            foregroundProbe?.Close();
            alertStacks.Remove(syntheticStreamId);
            overlayWindows.Remove(syntheticStreamId);
            overlayTargets.Remove(syntheticStreamId);
            syntheticWindow.Content = null;
            syntheticWindow.Close();
        }
    }

    private static Window CreateAlertForegroundProbe(FrameworkElement anchor)
    {
        var owner = Window.GetWindow(anchor)
            ?? throw new InvalidOperationException("The alert fixture was not attached to a window.");
        var probe = new Window
        {
            AllowsTransparency = false,
            Background = Brushes.Magenta,
            Height = Math.Max(64, anchor.ActualHeight),
            Left = owner.Left + 24,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = true,
            ShowInTaskbar = false,
            Title = "IP MultiViewer QA Foreground Probe",
            Top = owner.Top + 24,
            Topmost = false,
            Width = Math.Max(200, anchor.ActualWidth),
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None
        };
        probe.Show();
        _ = probe.Activate();
        probe.UpdateLayout();
        var probeHandle = new WindowInteropHelper(probe).Handle;
        if (probeHandle == IntPtr.Zero || !SetWindowPos(
                probeHandle,
                HwndTop,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate))
        {
            probe.Close();
            throw new InvalidOperationException("The unrelated foreground probe could not be placed above the monitoring window.");
        }

        return probe;
    }

    private static ForegroundExpectation CaptureUnrelatedForeground(Window probe, Window monitoringWindow)
    {
        var foregroundHandle = GetForegroundWindow();
        var probeHandle = new WindowInteropHelper(probe).Handle;
        var monitoringHandle = new WindowInteropHelper(monitoringWindow).Handle;
        if (foregroundHandle == IntPtr.Zero ||
            foregroundHandle == monitoringHandle ||
            monitoringWindow.IsActive)
        {
            throw new InvalidOperationException(
                "QA could not establish an unrelated foreground baseline before the alert transition.");
        }

        return new ForegroundExpectation(
            probe,
            foregroundHandle,
            foregroundHandle == probeHandle && probe.IsActive);
    }

    private static void AssertForegroundUnchanged(
        ForegroundExpectation expected,
        Window expectedInactiveWindow,
        string state,
        bool requireMonitoringInactive = true)
    {
        var actualForegroundHandle = GetForegroundWindow();
        if (actualForegroundHandle != expected.Handle ||
            (requireMonitoringInactive && expectedInactiveWindow.IsActive) ||
            (expected.ProbeOwnsForeground && !expected.Probe.IsActive))
        {
            throw new InvalidOperationException(
                $"The alert transition changed the unrelated foreground window for {state} " +
                $"(expected 0x{expected.Handle.ToInt64():X}, actual 0x{actualForegroundHandle.ToInt64():X}, " +
                $"monitoring_active={expectedInactiveWindow.IsActive}).");
        }
    }

    private readonly record struct ForegroundExpectation(Window Probe, IntPtr Handle, bool ProbeOwnsForeground);

    private static bool TryActivateMonitoringWindow(Window window, int pumpMilliseconds = 150)
    {
        _ = window.Activate();
        Pump(window, pumpMilliseconds);
        return GetForegroundWindow() == new WindowInteropHelper(window).Handle && window.IsActive;
    }

    private static void CaptureAlertOverlayContent(FrameworkElement child, string outputPath)
    {
        child.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(child.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(child.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(child);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        if (!pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0))
        {
            throw new InvalidOperationException("Alert overlay QA artifact was fully transparent.");
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static void AssertSurfaceGeometry(
        Window window,
        FrameworkElement surface,
        double width,
        double height,
        Thickness margin,
        Point origin,
        string transition)
    {
        var currentOrigin = surface.TranslatePoint(new Point(0, 0), window);
        if (Math.Abs(surface.ActualWidth - width) > 0.5 ||
            Math.Abs(surface.ActualHeight - height) > 0.5 ||
            Math.Abs(surface.Margin.Left - margin.Left) > 0.01 ||
            Math.Abs(surface.Margin.Top - margin.Top) > 0.01 ||
            Math.Abs(surface.Margin.Right - margin.Right) > 0.01 ||
            Math.Abs(surface.Margin.Bottom - margin.Bottom) > 0.01 ||
            Math.Abs(currentOrigin.X - origin.X) > 0.5 ||
            Math.Abs(currentOrigin.Y - origin.Y) > 0.5)
        {
            throw new InvalidOperationException(
                $"Video surface changed during {transition}: size {width:0.0}x{height:0.0} -> {surface.ActualWidth:0.0}x{surface.ActualHeight:0.0}, " +
                $"margin {margin} -> {surface.Margin}, origin {origin} -> {currentOrigin}.");
        }
    }

    private static void AssertFfmpegSessionTier(
        MainWindow window,
        int expectedWidth,
        int expectedHeight,
        int expectedMinimumCount)
    {
        var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
        if (sessions.Count < expectedMinimumCount)
        {
            throw new InvalidOperationException(
                $"Expected at least {expectedMinimumCount} FFmpeg sessions for the quality tier, got {sessions.Count}.");
        }

        foreach (var session in sessions.Values.Cast<object>())
        {
            var type = session.GetType();
            var width = Convert.ToInt32(type.GetProperty("OutputWidth")!.GetValue(session));
            var height = Convert.ToInt32(type.GetProperty("OutputHeight")!.GetValue(session));
            var bitmap = (WriteableBitmap)type.GetProperty("Bitmap")!.GetValue(session)!;
            if (width != expectedWidth || height != expectedHeight ||
                bitmap.PixelWidth != expectedWidth || bitmap.PixelHeight != expectedHeight)
            {
                throw new InvalidOperationException(
                    $"FFmpeg quality tier mismatch: session={width}x{height}, bitmap={bitmap.PixelWidth}x{bitmap.PixelHeight}, expected={expectedWidth}x{expectedHeight}.");
            }
        }
    }

    private static Dictionary<string, object> FfmpegSessionSnapshot(MainWindow window)
    {
        var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
        return sessions.Keys
            .Cast<string>()
            .ToDictionary(
                key => key,
                key => sessions[key] ?? throw new InvalidOperationException($"FFmpeg session {key} was null."),
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertRepeatedStartPreservesFfmpegSessions(
        MainWindow window,
        IReadOnlyDictionary<string, object> baseline,
        int expectedWidth,
        int expectedHeight)
    {
        var presentedBefore = baseline.ToDictionary(
            entry => entry.Key,
            entry => Convert.ToInt64(entry.Value.GetType().GetProperty("BitmapFramesPresented")!.GetValue(entry.Value)),
            StringComparer.OrdinalIgnoreCase);

        Named<Button>(window, "StartPlaybackButton")
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window, 600);
        AssertFfmpegSessionsPreserved(window, baseline, expectedWidth, expectedHeight);

        foreach (var (streamId, session) in baseline)
        {
            var presentedAfter = Convert.ToInt64(session.GetType().GetProperty("BitmapFramesPresented")!.GetValue(session));
            if (presentedAfter <= presentedBefore[streamId])
            {
                throw new InvalidOperationException(
                    $"Repeated Start interrupted live receiver {streamId}: presented frames did not continue ({presentedBefore[streamId]} -> {presentedAfter}).");
            }
        }

        Console.WriteLine($"repeated_start_preserved_sessions_ok streams={baseline.Count}");
    }

    private static void AssertFfmpegSessionsPreserved(
        MainWindow window,
        IReadOnlyDictionary<string, object> baseline,
        int expectedWidth,
        int expectedHeight)
    {
        var sessions = (IDictionary)PrivateField(window, "_ffmpegPlaybackSessions");
        foreach (var (streamId, original) in baseline)
        {
            if (!sessions.Contains(streamId) || !ReferenceEquals(sessions[streamId], original))
            {
                throw new InvalidOperationException(
                    $"A non-restart operation reconnected persistent stream {streamId}; live receivers must retain their session identity.");
            }

            var width = Convert.ToInt32(original.GetType().GetProperty("OutputWidth")!.GetValue(original));
            var height = Convert.ToInt32(original.GetType().GetProperty("OutputHeight")!.GetValue(original));
            var presented = Convert.ToInt64(original.GetType().GetProperty("BitmapFramesPresented")!.GetValue(original));
            if (width != expectedWidth || height != expectedHeight || presented <= 0)
            {
                throw new InvalidOperationException(
                    $"Persistent stream {streamId} lost playback continuity: {width}x{height}, presented={presented}.");
            }
        }
    }

    private static void AssertRenderBackendIndicator(MainWindow window, string expectedOutput)
    {
        Pump(window, 250);
        var indicator = Named<TextBlock>(window, "RenderBackendText");
        if (string.IsNullOrWhiteSpace(indicator.Text) ||
            !indicator.Text.Contains(expectedOutput, StringComparison.OrdinalIgnoreCase) ||
            indicator.Text.Contains("render gpu", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Decode/output indicator was not explicit: {indicator.Text}");
        }

        var tooltip = indicator.ToolTip?.ToString() ?? "";
        if (!tooltip.Contains("REQUESTED or UNKNOWN is not confirmation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Decode indicator tooltip did not explain its GPU evidence contract.");
        }
    }

    private static void AssertAdaptiveRenderTierContract()
    {
        var method = typeof(MainWindow).GetMethod(
            "NextFfmpegRenderSizeFor",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NextFfmpegRenderSizeFor was not available.");
        var tiers = new[]
        {
            (Kind: "2x2", Width: 960, Height: 540, Tiles: 4),
            (Kind: "3x3", Width: 640, Height: 360, Tiles: 9),
            (Kind: "4x4", Width: 480, Height: 270, Tiles: 16)
        };
        var pixelBudgets = new List<int>();
        foreach (var tier in tiers)
        {
            var tuple = method.Invoke(null, [tier.Kind])!;
            var tupleType = tuple.GetType();
            var width = Convert.ToInt32(tupleType.GetField("Item1")!.GetValue(tuple));
            var height = Convert.ToInt32(tupleType.GetField("Item2")!.GetValue(tuple));
            if (width != tier.Width || height != tier.Height)
            {
                throw new InvalidOperationException(
                    $"Next {tier.Kind} quality tier was {width}x{height}, expected {tier.Width}x{tier.Height}.");
            }

            pixelBudgets.Add(width * height * tier.Tiles);
        }

        if (pixelBudgets.Distinct().Count() != 1)
        {
            throw new InvalidOperationException("Adaptive quality tiers no longer share the same total pixel budget.");
        }

        var viewportMethod = typeof(MainWindow).GetMethod(
            "NextFfmpegRenderSizeForViewport",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NextFfmpegRenderSizeForViewport was not available.");
        var highDpiTiers = new[]
        {
            (Kind: "2x2", Width: 1280, Height: 720, ViewportWidth: 1800d, ViewportHeight: 900d),
            (Kind: "3x3", Width: 960, Height: 540, ViewportWidth: 1100d, ViewportHeight: 620d),
            (Kind: "4x4", Width: 640, Height: 360, ViewportWidth: 760d, ViewportHeight: 420d)
        };
        foreach (var tier in highDpiTiers)
        {
            var tuple = viewportMethod.Invoke(null, [tier.Kind, tier.ViewportWidth, tier.ViewportHeight])!;
            var tupleType = tuple.GetType();
            var width = Convert.ToInt32(tupleType.GetField("Item1")!.GetValue(tuple));
            var height = Convert.ToInt32(tupleType.GetField("Item2")!.GetValue(tuple));
            if (width != tier.Width || height != tier.Height)
            {
                throw new InvalidOperationException(
                    $"High-DPI {tier.Kind} quality tier was {width}x{height}, expected {tier.Width}x{tier.Height}.");
            }
        }

        var sessionType = typeof(MainWindow).Assembly.GetType("IPMultiViewer.Desktop.FfmpegPlaybackSession")
            ?? throw new InvalidOperationException("FfmpegPlaybackSession was not available.");
        var filterMethod = sessionType.GetMethod("BuildVideoFilter", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildVideoFilter was not available.");
        var filter = (string)filterMethod.Invoke(null, [640, 360])!;
        if (!filter.Contains("flags=bicubic", StringComparison.OrdinalIgnoreCase) ||
            !filter.Contains("force_original_aspect_ratio=decrease", StringComparison.OrdinalIgnoreCase) ||
            !filter.Contains("pad=width=640:height=360", StringComparison.OrdinalIgnoreCase) ||
            filter.Contains("fast_bilinear", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Quality-preserving FFmpeg filter contract was lost: {filter}");
        }
    }

    private static void AssertPaddedContentAnalysisContract()
    {
        AssertPaddedWhiteContentIsNotBlack(contentLeft: 80, contentRightExclusive: 560, label: "4:3");
        AssertPaddedWhiteContentIsNotBlack(contentLeft: 218, contentRightExclusive: 422, label: "9:16");
    }

    private static void AssertPaddedWhiteContentIsNotBlack(int contentLeft, int contentRightExclusive, string label)
    {
        const int width = 640;
        const int height = 360;
        var sessionType = typeof(MainWindow).Assembly.GetType("IPMultiViewer.Desktop.FfmpegPlaybackSession")
            ?? throw new InvalidOperationException("FfmpegPlaybackSession was not available.");
        var blackCallbacks = 0;
        using var session = CreateCustomFfmpegSession(
            sessionType,
            width,
            height,
            observeHardwareBackend: true,
            () => blackCallbacks++);
        var frame = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = contentLeft; x < contentRightExclusive; x++)
            {
                var offset = (y * width + x) * 3;
                frame[offset] = 255;
                frame[offset + 1] = 255;
                frame[offset + 2] = 255;
            }
        }

        var update = sessionType.GetMethod("UpdateSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UpdateSnapshot was not available.");
        var tryGet = sessionType.GetMethod("TryGetSnapshot")
            ?? throw new InvalidOperationException("TryGetSnapshot was not available.");
        update.Invoke(session, [frame]);
        var snapshotArgs = new object?[] { null };
        if (!(bool)tryGet.Invoke(session, snapshotArgs)! || snapshotArgs[0] is null)
        {
            throw new InvalidOperationException($"{label} padded frame did not produce an analysis snapshot.");
        }

        var snapshot = snapshotArgs[0]
            ?? throw new InvalidOperationException($"{label} padded frame snapshot was unavailable.");
        var luma = Convert.ToDouble(snapshot.GetType().GetProperty("Luma")!.GetValue(snapshot));
        var darkRatio = Convert.ToDouble(snapshot.GetType().GetProperty("DarkRatio")!.GetValue(snapshot));
        if (luma < 0.95 || darkRatio > 0.05 || blackCallbacks != 0)
        {
            throw new InvalidOperationException(
                $"{label} black padding contaminated content analysis: luma={luma:0.000}, dark={darkRatio:0.000}, callbacks={blackCallbacks}.");
        }

        var blackFrame = new byte[frame.Length];
        update.Invoke(session, [blackFrame]);
        snapshotArgs = [null];
        _ = tryGet.Invoke(session, snapshotArgs);
        snapshot = snapshotArgs[0]
            ?? throw new InvalidOperationException($"{label} black frame snapshot was unavailable.");
        luma = Convert.ToDouble(snapshot.GetType().GetProperty("Luma")!.GetValue(snapshot));
        darkRatio = Convert.ToDouble(snapshot.GetType().GetProperty("DarkRatio")!.GetValue(snapshot));
        if (luma > 0.01 || darkRatio < 0.99 || blackCallbacks != 1)
        {
            throw new InvalidOperationException(
                $"{label} full black frame stopped triggering analysis: luma={luma:0.000}, dark={darkRatio:0.000}, callbacks={blackCallbacks}.");
        }

        for (var index = 1; index < 120; index++)
        {
            update.Invoke(session, [blackFrame]);
        }
        if (blackCallbacks != 1)
        {
            throw new InvalidOperationException(
                $"{label} continuous black frames retriggered the immediate callback {blackCallbacks} times; want one rising edge.");
        }

        update.Invoke(session, [frame]);
        if (blackCallbacks != 1)
        {
            throw new InvalidOperationException($"{label} non-black reset unexpectedly fired a black callback.");
        }

        update.Invoke(session, [blackFrame]);
        if (blackCallbacks != 2)
        {
            throw new InvalidOperationException(
                $"{label} black callback did not fire once after a non-black reset; callbacks={blackCallbacks}.");
        }
    }

    private static void AssertHardwareBackendEvidenceContract()
    {
        var sessionType = typeof(MainWindow).Assembly.GetType("IPMultiViewer.Desktop.FfmpegPlaybackSession")
            ?? throw new InvalidOperationException("FfmpegPlaybackSession was not available.");
        var parser = sessionType.GetMethod(
            "ConfirmedHardwareBackendFromLogLine",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GPU evidence parser was not available.");
        var autoCandidate = parser.Invoke(null, ["Using auto hwaccel type dxva2 with new default device."]) as string;
        var dxva2 = parser.Invoke(null, ["Reinit context to 1920x1080, pix_fmt: dxva2_vld"]) as string;
        var d3d11 = parser.Invoke(null, ["Reinit context to 1920x1080, pix_fmt: d3d11"]) as string;
        var requestOnly = parser.Invoke(null, ["Selecting decoder because of requested hwaccel method d3d11va"]) as string;
        if (autoCandidate is not null ||
            !string.Equals(dxva2, "DXVA2", StringComparison.Ordinal) ||
            !string.Equals(d3d11, "D3D11VA", StringComparison.Ordinal) ||
            requestOnly is not null)
        {
            throw new InvalidOperationException(
                $"GPU evidence parsing was not strict enough: auto={autoCandidate ?? "<null>"}, dxva2={dxva2}, d3d11={d3d11}, request={requestOnly ?? "<null>"}.");
        }

        var failureParser = sessionType.GetMethod("IsFfmpegFailureLogLine", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FFmpeg failure parser was not available.");
        if ((bool)failureParser.Invoke(null, ["30 frames decoded; 0 decode errors"])! ||
            !(bool)failureParser.Invoke(null, ["Error while filtering: Invalid argument"])!)
        {
            throw new InvalidOperationException("Verbose FFmpeg success/failure lines were classified incorrectly.");
        }

        var observe = sessionType.GetMethod("TryObserveHardwareBackendLogLine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GPU evidence observer was not available.");
        var queue = sessionType.GetMethod("QueueBitmapFrame", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Bitmap frame queue was not available.");
        var confirmedProperty = sessionType.GetProperty("DecodeBackendConfirmed")
            ?? throw new InvalidOperationException("DecodeBackendConfirmed was not available.");
        var statusProperty = sessionType.GetProperty("DecodeStatusText")
            ?? throw new InvalidOperationException("DecodeStatusText was not available.");

        using (var confirmedSession = CreateDecodeStateProbeSession(sessionType))
        {
            if ((bool)confirmedProperty.GetValue(confirmedSession)! ||
                (bool)observe.Invoke(confirmedSession, ["Reinit context to 1920x1080, pix_fmt: dxva2_vld"])! != true ||
                (bool)confirmedProperty.GetValue(confirmedSession)!)
            {
                throw new InvalidOperationException("Hardware evidence was confirmed before a frame was presented.");
            }

            queue.Invoke(confirmedSession, [new byte[64 * 64 * 3], DateTime.UtcNow.Ticks]);
            Pump(null, 350);
            if (!(bool)confirmedProperty.GetValue(confirmedSession)! ||
                !string.Equals(statusProperty.GetValue(confirmedSession)?.ToString(), "GPU · DXVA2", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Hardware evidence plus a presented frame did not confirm GPU decode.");
            }
        }

        using (var unconfirmedSession = CreateDecodeStateProbeSession(sessionType))
        {
            if ((bool)observe.Invoke(unconfirmedSession, ["Selecting decoder because of requested hwaccel method d3d11va"])!)
            {
                throw new InvalidOperationException("A request-only FFmpeg line was accepted as hardware evidence.");
            }

            queue.Invoke(unconfirmedSession, [new byte[64 * 64 * 3], DateTime.UtcNow.Ticks]);
            Pump(null, 350);
            var status = statusProperty.GetValue(unconfirmedSession)?.ToString() ?? "";
            if ((bool)confirmedProperty.GetValue(unconfirmedSession)! ||
                (!status.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) &&
                 !status.Contains("REQUESTED", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Request-only decode state was incorrectly confirmed: {status}");
            }
        }
    }

    private static IDisposable CreateDecodeStateProbeSession(Type sessionType)
    {
        return CreateCustomFfmpegSession(
            sessionType,
            outputWidth: 64,
            outputHeight: 64,
            observeHardwareBackend: true,
            blackFrameDetected: () => { });
    }

    private static IDisposable CreateCustomFfmpegSession(
        Type sessionType,
        int outputWidth,
        int outputHeight,
        bool observeHardwareBackend,
        Action blackFrameDetected)
    {
        var constructor = sessionType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(item => item.GetParameters().Length == 13);
        return (IDisposable)constructor.Invoke(
        [
            "ffmpeg",
            "file:///not-started",
            "file",
            Dispatcher.CurrentDispatcher,
            0.04,
            0.015,
            0.04,
            0.85,
            blackFrameDetected,
            (Action)(() => { }),
            outputWidth,
            outputHeight,
            observeHardwareBackend
        ]);
    }

    private static void AssertSixteenChannelPanelFits(MainWindow window)
    {
        var channelType = typeof(MainWindow).Assembly.GetType("IPMultiViewer.Desktop.AudioChannelLevel")
            ?? throw new InvalidOperationException("AudioChannelLevel type was not available.");
        var channels = Array.CreateInstance(channelType, 16);
        for (var index = 0; index < channels.Length; index++)
        {
            channels.SetValue(Activator.CreateInstance(channelType, index + 1, -36d + index, -24d + index, 0), index);
        }

        var host = new Grid { Width = 384, Height = 100 };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var create = typeof(MainWindow).GetMethod("CreateAudioMeterPanels", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateAudioMeterPanels was not available.");
        var views = (Array)create.Invoke(window, [host, channels])!;
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();

        if (views.Length != 16 || host.Children.OfType<Viewbox>().Count() != 2)
        {
            throw new InvalidOperationException("The 16-channel meter did not create two complete eight-channel side banks.");
        }

        foreach (var panel in host.Children.OfType<Viewbox>())
        {
            if (panel.ActualWidth > 192.5 || panel.ActualHeight > host.Height + 0.5)
            {
                throw new InvalidOperationException(
                    $"The 16-channel meter bank overflowed its 4x4-sized reserve: {panel.ActualWidth:0.0}x{panel.ActualHeight:0.0}.");
            }
        }
    }

    private static object PrivateField(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Private field {name} was not available for QA.");
    }

    private static IReadOnlyList<Process> StartLiveFixtureSenders(
        string ffmpeg,
        IReadOnlyList<int> ports,
        int fixtureOffset)
    {
        var sources = new[]
        {
            "color=c=0x202020:size=640x360:rate=30,drawbox=color=white:t=fill:enable='lt(mod(t\\,1.5)\\,0.4)'",
            "smptebars=size=640x360:rate=30",
            "testsrc=size=640x360:rate=30",
            "rgbtestsrc=size=640x360:rate=30"
        };
        var senders = new List<Process>(ports.Count);
        for (var localIndex = 0; localIndex < ports.Count; localIndex++)
        {
            var index = fixtureOffset + localIndex;
            var audio = index == 0
                ? "aevalsrc=if(lt(mod(t\\,1.5)\\,0.4)\\,0.5*sin(2*PI*440*t)\\,0)|if(lt(mod(t\\,1.5)\\,0.4)\\,0.4*sin(2*PI*550*t)\\,0)|if(lt(mod(t\\,1.5)\\,0.4)\\,0.3*sin(2*PI*660*t)\\,0)|if(lt(mod(t\\,1.5)\\,0.4)\\,0.2*sin(2*PI*770*t)\\,0):s=48000:channel_layout=4.0"
                : $"aevalsrc=(0.17+0.07*sin(2*PI*0.7*t))*sin(2*PI*{440 + index * 40}*t)|(0.13+0.06*sin(2*PI*0.9*t))*sin(2*PI*{550 + index * 40}*t)|(0.10+0.05*sin(2*PI*1.1*t))*sin(2*PI*{660 + index * 40}*t)|(0.07+0.04*sin(2*PI*1.3*t))*sin(2*PI*{770 + index * 40}*t):s=48000:channel_layout=4.0";
            senders.Add(StartProcess(
                ffmpeg,
                "-hide_banner", "-loglevel", "error", "-re",
                "-f", "lavfi", "-i", sources[index % sources.Length],
                "-f", "lavfi", "-i", audio,
                "-t", "45",
                "-c:v", "mpeg4", "-q:v", "6",
                "-c:a", "aac", "-b:a", "192k",
                "-f", "mpegts",
                $"srt://127.0.0.1:{ports[localIndex]}?mode=caller&pkt_size=1316"));
        }

        return senders;
    }

    private static int[] FreeUdpPorts(int count)
    {
        var ports = new HashSet<int>();
        while (ports.Count < count)
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            ports.Add(((IPEndPoint)client.Client.LocalEndPoint!).Port);
        }

        return ports.ToArray();
    }

    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("FFMPEG_BIN");
        if (!string.IsNullOrWhiteSpace(configured) &&
            (File.Exists(configured) || string.Equals(configured, "ffmpeg", StringComparison.OrdinalIgnoreCase)))
        {
            return configured;
        }

        return FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool FfmpegSupportsProtocol(string ffmpeg, string protocol)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-protocols");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to inspect FFmpeg protocols.");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            StopProcess(process);
            return false;
        }

        return process.ExitCode == 0 && output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => string.Equals(line, protocol, StringComparison.OrdinalIgnoreCase));
    }

    private static Process StartProcess(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(executable)}.");
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Best effort cleanup for QA-only FFmpeg processes.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Color ColorOf(Brush brush)
    {
        return brush is SolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException("Meter toggle background must be a solid color.");
    }

    private static T Named<T>(FrameworkElement root, string name) where T : class
    {
        return root.FindName(name) as T
            ?? throw new InvalidOperationException($"Expected {typeof(T).Name} named {name}.");
    }

    private static void CaptureAuxiliaryWindows(
        MainWindow window,
        string settingsOutputPath,
        string alertHistoryOutputPath)
    {
        var settings = (Window)PrivateField(window, "_settingsWindow");
        settings.Show();
        Pump(settings, 200);
        if (settings.FindName("SettingsTabs") is not TabControl settingsTabs || settingsTabs.Items.Count != 3)
        {
            throw new InvalidOperationException("The renewed Settings window did not expose its three primary tabs.");
        }

        Capture(settings, settingsOutputPath);
        settings.Hide();

        var openHistory = typeof(MainWindow).GetMethod(
            "OpenAlertHistoryButton_Click",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OpenAlertHistoryButton_Click was not available.");
        openHistory.Invoke(window, [new Button(), new RoutedEventArgs()]);
        Pump(window, 200);
        var history = (Window)PrivateField(window, "_alertHistoryWindow");
        if (history.FindName("SearchTextBox") is not TextBox ||
            history.FindName("StatusFilterCombo") is not ComboBox ||
            history.FindName("SeverityFilterCombo") is not ComboBox ||
            history.FindName("DetailContentPanel") is not FrameworkElement)
        {
            throw new InvalidOperationException("The renewed Alert History search, filters, or detail pane was unavailable.");
        }

        Capture(history, alertHistoryOutputPath);
        history.Hide();
    }

    private static void Capture(Window window, string outputPath)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("IPMV_QA_SCREEN_CAPTURE"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            CaptureFromScreen(window, outputPath);
            return;
        }

        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static void CaptureFromScreen(Window window, string outputPath)
    {
        window.Activate();
        Pump(window, 150);
        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var origin = window.PointToScreen(new Point(0, 0));
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * toDevice.M11));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * toDevice.M22));
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                (int)Math.Round(origin.X),
                (int)Math.Round(origin.Y),
                0,
                0,
                new System.Drawing.Size(width, height),
                System.Drawing.CopyPixelOperation.SourceCopy);
        }

        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Pump(Window? window, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new DispatcherOperationCallback(_ =>
                {
                    frame.Continue = false;
                    return null;
                }),
                null);
            Dispatcher.PushFrame(frame);
            window?.UpdateLayout();
        }
    }
}
