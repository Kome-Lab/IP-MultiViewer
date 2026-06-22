using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IPMultiViewer.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        object? sessionA = null;
        object? sessionB = null;
        try
        {
            using var appScope = new WpfAppScope();
            var window = new MainWindow
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };
            window.Show();
            Pump(window, 500);

            AssertLayoutSwitchDoesNotCrash(window);
            AssertNoStandaloneAnalysisMonitor(window);
            AssertActiveLayoutStreamsAreConcreteAndUnique(window);
            AssertActiveAlertsByStreamIsExhaustive(window);
            AssertTextSetHelperSkipsUnchangedText();
            AssertSrtCandidateModes();
            AssertLowLatencyPlaybackOptions();
            AssertPlaybackStatusWaitsForPresentedVideo(window);
            AssertAudioMeterSidePanels(window);
            AssertFfmpegSnapshotSampling();
            AssertFfmpegAudioStatsParser();
            AssertTileFreshnessTextDoesNotRepaintChrome(window);
            (sessionA, sessionB) = AssertTwoFfmpegSessionsCanRun();

            window.Close();
            Pump(null, 100);
            Console.WriteLine("desktop_smoke_ok");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            DisposeReflective(sessionA);
            DisposeReflective(sessionB);
        }
    }

    private static void AssertLayoutSwitchDoesNotCrash(MainWindow window)
    {
        var combo = (ComboBox)Field(window, "MainLayoutCombo").GetValue(window)!;
        var tileGrid = (UniformGrid)Field(window, "TileGrid").GetValue(window)!;
        var original = combo.SelectedItem as LayoutConfig;

        try
        {
            SwitchLayout(window, combo, tileGrid, "3x3", 3);
            SwitchLayout(window, combo, tileGrid, "4x4", 4);
            SwitchLayout(window, combo, tileGrid, "2x2", 2);
            Console.WriteLine("layout_switch_smoke_ok");
        }
        finally
        {
            if (original is not null && !ReferenceEquals(combo.SelectedItem, original))
            {
                combo.SelectedItem = original;
                Pump(window, 500);
            }
        }
    }

    private static void SwitchLayout(MainWindow window, ComboBox combo, UniformGrid tileGrid, string kind, int size)
    {
        var layout = combo.Items.Cast<LayoutConfig>().FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Layout {kind} was not available in the main layout selector.");
        combo.SelectedItem = layout;
        Pump(window, 800);

        if (tileGrid.Rows != size || tileGrid.Columns != size)
        {
            throw new InvalidOperationException($"Layout {kind} rendered as {tileGrid.Rows}x{tileGrid.Columns}.");
        }

        var expectedTiles = size * size;
        if (tileGrid.Children.Count != expectedTiles)
        {
            throw new InvalidOperationException($"Layout {kind} rendered {tileGrid.Children.Count} tiles instead of {expectedTiles}.");
        }
    }

    private static void AssertNoStandaloneAnalysisMonitor(MainWindow window)
    {
        var stream = new StreamConfig
        {
            Id = "smoke-analysis",
            Name = "Smoke analysis",
            Enabled = true,
            InputUrl = "rtsp://example.test/live",
            InputProtocol = "rtsp",
            AnalysisEnabled = true,
            AudioMonitorEnabled = true
        };
        var monitors = (IDictionary)Field(window, "_videoMonitors").GetValue(window)!;
        var before = monitors.Count;
        Method(window.GetType(), "StartVideoMonitor").Invoke(window, [stream]);
        Pump(window, 300);
        if (monitors.Count != before)
        {
            throw new InvalidOperationException("StartVideoMonitor created a standalone source connection.");
        }

        var shouldUse = (bool)Method(window.GetType(), "ShouldUseFfmpegPlayback").Invoke(window, [stream])!;
        if (!shouldUse)
        {
            throw new InvalidOperationException("Analysis-enabled streams must route through shared FFmpeg playback.");
        }
    }

    private static void AssertActiveLayoutStreamsAreConcreteAndUnique(MainWindow window)
    {
        var activeLayoutStreams = MethodWithParameterCount(typeof(MainWindow), "ActiveLayoutStreams", 0);
        var result = activeLayoutStreams
            .Invoke(window, [])!;
        if (result is not IList<StreamConfig> streams)
        {
            throw new InvalidOperationException("Active layout stream resolution should return a concrete list for reuse in timer updates.");
        }

        var distinctIds = streams
            .Select(stream => stream.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (distinctIds.Count != streams.Count)
        {
            throw new InvalidOperationException("Active layout stream resolution returned duplicate stream ids.");
        }
    }

    private static void AssertActiveAlertsByStreamIsExhaustive(MainWindow window)
    {
        var streams = new List<StreamConfig>
        {
            new() { Id = "smoke-alerts-empty-a", Name = "Smoke alerts empty A", Enabled = true },
            new() { Id = "smoke-alerts-empty-b", Name = "Smoke alerts empty B", Enabled = true }
        };
        var activeAlertsByStream = Method(typeof(MainWindow), "ActiveAlertsByStream", BindingFlags.Instance | BindingFlags.NonPublic);
        var result = (Dictionary<string, IReadOnlyList<AlertRecord>>)activeAlertsByStream.Invoke(window, [streams])!;
        if (result.Count != streams.Count ||
            !result.TryGetValue(streams[0].Id, out var firstAlerts) ||
            !result.TryGetValue(streams[1].Id, out var secondAlerts) ||
            firstAlerts.Count != 0 ||
            secondAlerts.Count != 0)
        {
            throw new InvalidOperationException("Active alert lookup should include active layout streams even when they have no alerts.");
        }
    }

    private static void AssertTextSetHelperSkipsUnchangedText()
    {
        var textBlock = new TextBlock { Text = "unchanged" };
        var method = Method(typeof(MainWindow), "SetTextIfChanged", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if ((bool)method.Invoke(null, [textBlock, "unchanged"])!)
        {
            throw new InvalidOperationException("SetTextIfChanged returned true for unchanged text.");
        }
        if (!(bool)method.Invoke(null, [textBlock, "changed"])! ||
            textBlock.Text != "changed")
        {
            throw new InvalidOperationException("SetTextIfChanged did not update changed text.");
        }
    }

    private static void AssertSrtCandidateModes()
    {
        var sessionType = DesktopType("FfmpegPlaybackSession");
        var build = Method(sessionType, "BuildInputCandidates", BindingFlags.Static | BindingFlags.NonPublic);
        var bare = CandidateList(build, "srt://127.0.0.1:12000", "srt");
        var listener = CandidateList(build, "srt://127.0.0.1:12000", "srt-listener");
        var caller = CandidateList(build, "srt://127.0.0.1:12000", "srt-caller");
        var urlMode = CandidateList(build, "srt://127.0.0.1:12000?mode=caller", "srt-listener");

        if (bare.Count != 1 || bare[0] != "srt://127.0.0.1:12000")
        {
            throw new InvalidOperationException("Bare SRT must not add implicit modes.");
        }

        if (listener.Count != 1 || !listener[0].Contains("mode=listener", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("srt-listener did not add listener mode.");
        }

        if (caller.Count != 1 || !caller[0].Contains("mode=caller", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("srt-caller did not add caller mode.");
        }

        if (urlMode.Count != 1 || !urlMode[0].Contains("mode=caller", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Explicit SRT URL mode was not preserved.");
        }
    }

    private static void AssertLowLatencyPlaybackOptions()
    {
        var instanceOptions = ((IEnumerable<string>)Method(typeof(MainWindow), "LowLatencyVlcInstanceOptions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, [])!).ToList();
        var mediaOptions = ((IEnumerable<string>)Method(typeof(MainWindow), "LowLatencyVlcMediaOptions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, [false])!).ToList();
        var analysisMediaOptions = ((IEnumerable<string>)Method(typeof(MainWindow), "LowLatencyVlcMediaOptions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, [true])!).ToList();

        AssertNoLegacyCache(instanceOptions, "VLC instance");
        AssertNoLegacyCache(mediaOptions, "VLC media");
        if (!analysisMediaOptions.Contains(":no-audio", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("VLC analysis media options must disable audio.");
        }
        if (!mediaOptions.Contains(":drop-late-frames", StringComparer.OrdinalIgnoreCase) ||
            !mediaOptions.Contains(":skip-frames", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("VLC media options must drop late frames for live monitoring.");
        }

        var sessionType = DesktopType("FfmpegPlaybackSession");
        var args = new Collection<string>();
        Method(sessionType, "AddCommonFfmpegInputArguments", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [args, "rtsp", "rtsp://example.test/live"]);
        if (!ContainsArgumentPair(args, "-probesize", "32768") ||
            !ContainsArgumentPair(args, "-analyzeduration", "0") ||
            !ContainsArgumentPair(args, "-fflags", "nobuffer") ||
            !ContainsArgumentPair(args, "-flags", "low_delay"))
        {
            throw new InvalidOperationException("FFmpeg input arguments must keep low-latency probe and buffer settings.");
        }
    }

    private static void AssertNoLegacyCache(IEnumerable<string> options, string label)
    {
        var list = options.ToList();
        if (list.Any(option => option.Contains("caching=300", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{label} options still contain legacy 300ms caching.");
        }
        if (!list.Any(option => option.Contains("network-caching=100", StringComparison.OrdinalIgnoreCase)) ||
            !list.Any(option => option.Contains("live-caching=100", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{label} options must use 100ms live-monitoring caches.");
        }
    }

    private static bool ContainsArgumentPair(IReadOnlyList<string> args, string name, string value)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(args[index + 1], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertAudioMeterSidePanels(MainWindow window)
    {
        var channelType = DesktopType("AudioChannelLevel");
        var channels = Array.CreateInstance(channelType, 4);
        for (var index = 0; index < 4; index++)
        {
            channels.SetValue(Activator.CreateInstance(channelType, index + 1, -24.0 - index, -12.0 - index, 0), index);
        }

        var splitResult = Method(typeof(MainWindow), "SplitAudioChannelsForSides", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [channels])!;
        var left = (Array)splitResult.GetType().GetField("Item1")!.GetValue(splitResult)!;
        var right = (Array)splitResult.GetType().GetField("Item2")!.GetValue(splitResult)!;
        if (left.Length == 0 || right.Length == 0)
        {
            throw new InvalidOperationException("Audio meters were not split across both video sides.");
        }

        var meterViewType = DesktopType("AudioMeterView");
        var views = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(meterViewType))!;
        var panel = Method(typeof(MainWindow), "CreateAudioMeterSidePanel", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [left, HorizontalAlignment.Left, views]);
        _ = Method(typeof(MainWindow), "CreateAudioMeterSidePanel", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [right, HorizontalAlignment.Right, views]);
        if (panel is not Border)
        {
            throw new InvalidOperationException("Audio meter side panel did not render as a video-side overlay border.");
        }
        if (views.Count == 0)
        {
            throw new InvalidOperationException("Audio meter side panel did not create reusable meter views.");
        }

        var viewsArray = Array.CreateInstance(meterViewType, views.Count);
        views.CopyTo(viewsArray, 0);
        var matches = (bool)MethodWithParameterTypes(
                typeof(MainWindow),
                "AudioMeterViewsMatch",
                [viewsArray.GetType(), channels.GetType()],
                BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [viewsArray, channels])!;
        if (!matches)
        {
            throw new InvalidOperationException("Reusable audio meter views did not match their source channels.");
        }
        var viewLookup = Method(typeof(MainWindow), "AudioMeterViewLookup", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [viewsArray])!;
        var lookupMatches = (bool)MethodWithParameterTypes(
                typeof(MainWindow),
                "AudioMeterViewsMatch",
                [typeof(IReadOnlyDictionary<,>).MakeGenericType(typeof(int), meterViewType), channels.GetType()],
                BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [viewLookup, channels])!;
        if (!lookupMatches)
        {
            throw new InvalidOperationException("Audio meter channel lookup did not match its source channels.");
        }

        var firstView = views[0]!;
        var fill = (Border)meterViewType.GetProperty("Fill")!.GetValue(firstView)!;
        var fillClip = (RectangleGeometry)meterViewType.GetProperty("FillClip")!.GetValue(firstView)!;
        var peakMarker = (Border)meterViewType.GetProperty("PeakMarker")!.GetValue(firstView)!;
        var peakOffset = (TranslateTransform)meterViewType.GetProperty("PeakOffset")!.GetValue(firstView)!;
        var audioMeterHeight = Convert.ToDouble(typeof(MainWindow).GetField("AudioMeterHeight", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue(), CultureInfo.InvariantCulture);
        if (Math.Abs(fill.Height - audioMeterHeight) > 0.001 ||
            fill.Clip is not RectangleGeometry ||
            peakMarker.RenderTransform is not TranslateTransform)
        {
            throw new InvalidOperationException("Audio meters must keep stable dimensions with clip/transform updates.");
        }

        meterViewType.GetProperty("TargetLevelRatio")!.SetValue(firstView, 0.75);
        meterViewType.GetProperty("TargetPeakRatio")!.SetValue(firstView, 0.9);
        meterViewType.GetProperty("Clipping")!.SetValue(firstView, true);
        var meterViews = Array.CreateInstance(meterViewType, 1);
        meterViews.SetValue(firstView, 0);
        var audioMeterViews = (IDictionary)Field(window, "_tileAudioMeterViews").GetValue(window)!;
        audioMeterViews["smoke-meter"] = meterViews;
        var audioMeterHosts = (IDictionary)Field(window, "_tileAudioMeterHosts").GetValue(window)!;
        var audioMeterHost = new Grid { Visibility = Visibility.Visible };
        audioMeterHosts["smoke-meter"] = audioMeterHost;
        var audioMeterTimer = (DispatcherTimer)Field(window, "_audioMeterTimer").GetValue(window)!;
        if (audioMeterTimer.Interval != TimeSpan.FromMilliseconds(50))
        {
            throw new InvalidOperationException($"Audio meter target refresh timer interval = {audioMeterTimer.Interval.TotalMilliseconds}ms, want 50ms.");
        }

        var advanceAudioMeterViews = MethodWithParameterTypes(
            typeof(MainWindow),
            "AdvanceAudioMeterViews",
            [typeof(string), typeof(bool)],
            BindingFlags.Instance | BindingFlags.NonPublic);
        advanceAudioMeterViews.Invoke(window, ["smoke-meter", true]);
        if (Math.Abs(fill.Height - audioMeterHeight) > 0.001 ||
            fillClip.Rect.Height < audioMeterHeight * 0.7 ||
            peakOffset.Y >= audioMeterHeight * 0.15)
        {
            throw new InvalidOperationException("Audio meter clip/peak transforms did not reflect target levels.");
        }

        meterViewType.GetProperty("DisplayedLevelRatio")!.SetValue(firstView, 0.10);
        meterViewType.GetProperty("DisplayedPeakRatio")!.SetValue(firstView, 0.85);
        meterViewType.GetProperty("TargetLevelRatio")!.SetValue(firstView, 0.80);
        meterViewType.GetProperty("TargetPeakRatio")!.SetValue(firstView, 0.35);
        meterViewType.GetProperty("LastAnimationAt")!.SetValue(firstView, DateTimeOffset.UtcNow.AddMilliseconds(-66));
        Method(typeof(MainWindow), "AudioMeterCompositionTarget_Rendering", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, [null, EventArgs.Empty]);
        var displayedLevel = (double)meterViewType.GetProperty("DisplayedLevelRatio")!.GetValue(firstView)!;
        var displayedPeak = (double)meterViewType.GetProperty("DisplayedPeakRatio")!.GetValue(firstView)!;
        if (displayedLevel <= 0.10 ||
            displayedLevel >= 0.80 ||
            displayedPeak >= 0.85 ||
            displayedPeak < 0.35 ||
            Math.Abs(fill.Height - audioMeterHeight) > 0.001)
        {
            throw new InvalidOperationException($"Audio meter time-based smoothing was unexpected: level={displayedLevel}, peak={displayedPeak}.");
        }

        MethodWithParameterCount(typeof(MainWindow), "UpdateAudioMeterTargets", 2, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, ["smoke-meter", false]);
        displayedLevel = (double)meterViewType.GetProperty("DisplayedLevelRatio")!.GetValue(firstView)!;
        displayedPeak = (double)meterViewType.GetProperty("DisplayedPeakRatio")!.GetValue(firstView)!;
        if (audioMeterHost.Visibility != Visibility.Collapsed ||
            displayedLevel != 0 ||
            displayedPeak != 0 ||
            fillClip.Rect.Height > 1.001)
        {
            throw new InvalidOperationException("Audio meter views were not reset when no fresh audio snapshot was available.");
        }

        var targetRefreshes = (IDictionary)Field(window, "_lastAudioMeterTargetRefreshAt").GetValue(window)!;
        var hiddenRefreshedAt = (DateTimeOffset)targetRefreshes["smoke-meter"]!;
        Method(typeof(MainWindow), "RefreshAudioMeterTargets", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, []);
        var afterHiddenRefreshAttempt = (DateTimeOffset)targetRefreshes["smoke-meter"]!;
        if (afterHiddenRefreshAttempt != hiddenRefreshedAt)
        {
            throw new InvalidOperationException("Hidden audio meter targets should be probed slower than visible meters.");
        }
    }

    private static void AssertPlaybackStatusWaitsForPresentedVideo(MainWindow window)
    {
        var stream = new StreamConfig
        {
            Id = "smoke-vlc-status",
            Name = "Smoke VLC Status",
            Enabled = true,
            InputUrl = "rtsp://example.invalid/live"
        };
        var snapshotType = DesktopType("PlaybackSnapshot");
        var statusTextFor = MethodWithParameterTypes(
            typeof(MainWindow),
            "StatusTextFor",
            [typeof(StreamConfig), typeof(AlertRecord), typeof(bool), snapshotType],
            BindingFlags.Instance | BindingFlags.NonPublic);
        var statusColorFor = MethodWithParameterTypes(
            typeof(MainWindow),
            "StatusColorFor",
            [typeof(StreamConfig), typeof(AlertRecord), typeof(bool), snapshotType],
            BindingFlags.Instance | BindingFlags.NonPublic);
        var hasPresentedVlcVideo = Method(typeof(MainWindow), "HasPresentedVlcVideo", BindingFlags.Instance | BindingFlags.NonPublic);

        var pendingSnapshot = CreatePlaybackSnapshot("playing", displayedPictures: 0);
        var pendingText = (string)statusTextFor.Invoke(window, [stream, null, true, pendingSnapshot])!;
        var pendingColor = (string)statusColorFor.Invoke(window, [stream, null, true, pendingSnapshot])!;
        var pendingPresented = (bool)hasPresentedVlcVideo.Invoke(window, [stream.Id, pendingSnapshot])!;
        if (pendingPresented ||
            pendingText.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pendingText, "monitoring / rendering", StringComparison.Ordinal) ||
            !string.Equals(pendingColor, "#f59e0b", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Playback status should wait for a presented video frame: text={pendingText}, color={pendingColor}, presented={pendingPresented}.");
        }

        var presentedSnapshot = CreatePlaybackSnapshot("playing", displayedPictures: 1);
        var presentedText = (string)statusTextFor.Invoke(window, [stream, null, true, presentedSnapshot])!;
        var presentedColor = (string)statusColorFor.Invoke(window, [stream, null, true, presentedSnapshot])!;
        var presented = (bool)hasPresentedVlcVideo.Invoke(window, [stream.Id, presentedSnapshot])!;
        if (!presented ||
            !presentedText.StartsWith("OK / video", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(presentedColor, "#22c55e", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Playback status did not turn OK after video presentation: text={presentedText}, color={presentedColor}, presented={presented}.");
        }

        var lastVideoPresentedAt = (IDictionary)Field(window, "_lastVideoPresentedAt").GetValue(window)!;
        lastVideoPresentedAt[stream.Id] = DateTimeOffset.UtcNow;
        var freshRememberedText = (string)statusTextFor.Invoke(window, [stream, null, true, pendingSnapshot])!;
        var freshRememberedColor = (string)statusColorFor.Invoke(window, [stream, null, true, pendingSnapshot])!;
        if (!freshRememberedText.StartsWith("OK / video", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(freshRememberedColor, "#22c55e", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Fresh remembered presentation should keep color and text aligned: text={freshRememberedText}, color={freshRememberedColor}.");
        }

        lastVideoPresentedAt[stream.Id] = DateTimeOffset.UtcNow.AddSeconds(-2);
        var staleText = (string)statusTextFor.Invoke(window, [stream, null, true, presentedSnapshot])!;
        var staleColor = (string)statusColorFor.Invoke(window, [stream, null, true, presentedSnapshot])!;
        if (staleText.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
            !staleText.StartsWith("monitoring / video", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(staleColor, "#f59e0b", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Stale presented video should not stay OK: text={staleText}, color={staleColor}.");
        }
    }

    private static object CreatePlaybackSnapshot(string state, int displayedPictures)
    {
        var snapshotType = DesktopType("PlaybackSnapshot");
        var constructor = snapshotType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(item => item.GetParameters().Length == 17);
        return constructor.Invoke(
        [
            state,
            30f,
            1000L,
            -1L,
            true,
            1,
            0,
            1024L,
            1024L,
            displayedPictures > 0 ? 1 : 0,
            displayedPictures,
            0,
            1,
            1,
            1.0f,
            1.0f,
            DateTimeOffset.UtcNow
        ]);
    }

    private static void AssertFfmpegSnapshotSampling()
    {
        var sessionType = DesktopType("FfmpegPlaybackSession");
        var blackCallbacks = 0;
        var presentedCallbacks = 0;
        var session = CreateFfmpegSession(
            sessionType,
            "ffmpeg",
            "file:///not-started",
            "file",
            () => blackCallbacks++,
            () => presentedCallbacks++);
        try
        {
            var frameBytes = (int)(sessionType.GetField("FrameBytes", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue() ?? throw new InvalidOperationException("FrameBytes constant was unavailable."));
            var frame = new byte[frameBytes];
            Method(sessionType, "UpdateSnapshot").Invoke(session, [frame]);
            if (blackCallbacks != 1)
            {
                throw new InvalidOperationException($"Black frame callback count = {blackCallbacks}, want 1.");
            }

            var tryGetSnapshot = Method(sessionType, "TryGetSnapshot");
            var args = new object?[] { null };
            if ((bool)tryGetSnapshot.Invoke(session, args)! != true || args[0] is null)
            {
                throw new InvalidOperationException("FFmpeg session did not expose a sampled black snapshot.");
            }

            var snapshot = args[0]!;
            var luma = (double)snapshot.GetType().GetProperty("Luma")!.GetValue(snapshot)!;
            var darkRatio = (double)snapshot.GetType().GetProperty("DarkRatio")!.GetValue(snapshot)!;
            if (luma > 0.001 || darkRatio < 0.999)
            {
                throw new InvalidOperationException($"Sampled black frame metrics were unexpected: luma={luma}, dark={darkRatio}.");
            }

            Array.Fill(frame, (byte)255);
            Method(sessionType, "UpdateSnapshot").Invoke(session, [frame]);
            args = [null];
            _ = tryGetSnapshot.Invoke(session, args);
            snapshot = args[0]!;
            luma = (double)snapshot.GetType().GetProperty("Luma")!.GetValue(snapshot)!;
            darkRatio = (double)snapshot.GetType().GetProperty("DarkRatio")!.GetValue(snapshot)!;
            if (luma < 0.999 || darkRatio > 0.001 || blackCallbacks != 1)
            {
                throw new InvalidOperationException($"Sampled white frame metrics were unexpected: luma={luma}, dark={darkRatio}, callbacks={blackCallbacks}.");
            }

            var queueBitmapFrame = Method(sessionType, "QueueBitmapFrame");
            var bitmapFramesCoalesced = sessionType.GetProperty("BitmapFramesCoalesced")!;
            var bitmapFramesPresented = sessionType.GetProperty("BitmapFramesPresented")!;
            var firstReadTicks = DateTime.UtcNow.Ticks;
            queueBitmapFrame.Invoke(session, [frame, firstReadTicks]);
            queueBitmapFrame.Invoke(session, [frame, DateTime.UtcNow.Ticks]);
            Pump(null, 500);
            if ((long)bitmapFramesCoalesced.GetValue(session)! <= 0 ||
                (long)bitmapFramesPresented.GetValue(session)! < 2)
            {
                throw new InvalidOperationException("Queued bitmap frames were not coalesced and presented.");
            }
            if (presentedCallbacks != 1)
            {
                throw new InvalidOperationException($"First presented frame callback count = {presentedCallbacks}, want 1.");
            }
            var lastFramePresentedAt = (DateTimeOffset)sessionType.GetProperty("LastFramePresentedAt")!.GetValue(session)!;
            var lastRenderDelayMs = (double)sessionType.GetProperty("LastRenderDelayMs")!.GetValue(session)!;
            if (lastFramePresentedAt <= DateTimeOffset.MinValue ||
                lastRenderDelayMs < 0)
            {
                throw new InvalidOperationException("FFmpeg frame freshness metrics were not updated after presentation.");
            }
        }
        finally
        {
            DisposeReflective(session);
        }
    }

    private static void AssertFfmpegAudioStatsParser()
    {
        var sessionType = DesktopType("FfmpegPlaybackSession");
        var session = CreateFfmpegSession(sessionType, "ffmpeg", "file:///not-started", "file");
        try
        {
            var parse = Method(sessionType, "TryParseAudioStatsLine");
            AssertParsedAudioLine(parse, session, "lavfi.astats.Overall.RMS_level=-20.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.Overall.Peak_level=-2.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.1.RMS_level=-18.5");
            AssertParsedAudioLine(parse, session, "lavfi.astats.1.Peak_level=-3.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.2.RMS_level=-24.25");
            AssertParsedAudioLine(parse, session, "lavfi.astats.2.Peak_level=-0.5");
            AssertParsedAudioLine(parse, session, "lavfi.astats.3.RMS_level=-30.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.4.Peak_level=-4.0");

            var args = new object?[] { null };
            if ((bool)Method(sessionType, "TryGetAudioSnapshot").Invoke(session, args)! != true || args[0] is null)
            {
                throw new InvalidOperationException("FFmpeg audio stats parser did not expose an audio snapshot.");
            }

            var snapshot = args[0]!;
            AssertNear((double)snapshot.GetType().GetProperty("LevelDb")!.GetValue(snapshot)!, -20.0, "overall RMS");
            AssertNear((double)snapshot.GetType().GetProperty("PeakDb")!.GetValue(snapshot)!, -2.0, "overall peak");

            var channels = (Array)snapshot.GetType().GetProperty("Channels")!.GetValue(snapshot)!;
            if (channels.Length != 4)
            {
                throw new InvalidOperationException($"FFmpeg audio stats parser exposed {channels.Length} channels, want 4.");
            }

            var channel1 = channels.GetValue(0)!;
            var channel2 = channels.GetValue(1)!;
            var channel3 = channels.GetValue(2)!;
            var channel4 = channels.GetValue(3)!;
            AssertNear((double)channel1.GetType().GetProperty("LevelDb")!.GetValue(channel1)!, -18.5, "channel 1 RMS");
            AssertNear((double)channel1.GetType().GetProperty("PeakDb")!.GetValue(channel1)!, -3.0, "channel 1 peak");
            AssertNear((double)channel2.GetType().GetProperty("LevelDb")!.GetValue(channel2)!, -24.25, "channel 2 RMS");
            AssertNear((double)channel2.GetType().GetProperty("PeakDb")!.GetValue(channel2)!, -0.5, "channel 2 peak");
            AssertNear((double)channel3.GetType().GetProperty("LevelDb")!.GetValue(channel3)!, -30.0, "channel 3 RMS");
            AssertNear((double)channel3.GetType().GetProperty("PeakDb")!.GetValue(channel3)!, -2.0, "channel 3 peak fallback");
            AssertNear((double)channel4.GetType().GetProperty("LevelDb")!.GetValue(channel4)!, -20.0, "channel 4 RMS fallback");
            AssertNear((double)channel4.GetType().GetProperty("PeakDb")!.GetValue(channel4)!, -4.0, "channel 4 peak");
            if ((int)channel2.GetType().GetProperty("ClippingSamples")!.GetValue(channel2)! != 1)
            {
                throw new InvalidOperationException("FFmpeg audio stats parser did not mark near-clip channel peak.");
            }
        }
        finally
        {
            DisposeReflective(session);
        }
    }

    private static void AssertParsedAudioLine(MethodInfo parse, object session, string line)
    {
        if ((bool)parse.Invoke(session, [line])! != true)
        {
            throw new InvalidOperationException($"FFmpeg audio stats parser rejected line: {line}");
        }
    }

    private static void AssertNear(double actual, double expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.001)
        {
            throw new InvalidOperationException($"{label} = {actual}, want {expected}.");
        }
    }

    private static void AssertTileFreshnessTextDoesNotRepaintChrome(MainWindow window)
    {
        var activeStreams = ((IEnumerable)MethodWithParameterCount(typeof(MainWindow), "ActiveLayoutStreams", 0, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, [])!).Cast<StreamConfig>().ToList();
        var stream = activeStreams.FirstOrDefault()
            ?? throw new InvalidOperationException("No active layout stream was available for tile chrome smoke.");
        var sessionType = DesktopType("FfmpegPlaybackSession");
        var session = CreateFfmpegSession(sessionType, "ffmpeg", "file:///not-started");
        try
        {
            var frameBytes = (int)(sessionType.GetField("FrameBytes", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue() ?? throw new InvalidOperationException("FrameBytes constant was unavailable."));
            var frame = new byte[frameBytes];
            Field(session, "_state").SetValue(session, "playing");
            Method(sessionType, "QueueBitmapFrame").Invoke(session, [frame, DateTime.UtcNow.Ticks]);
            Pump(window, 300);

            var ffmpegSessions = (IDictionary)Field(window, "_ffmpegPlaybackSessions").GetValue(window)!;
            var playbackSnapshots = (IDictionary)Field(window, "_playbackSnapshots").GetValue(window)!;
            ffmpegSessions[stream.Id] = session;
            playbackSnapshots[stream.Id] = Method(sessionType, "CurrentPlaybackSnapshot").Invoke(session, []);
            Method(typeof(MainWindow), "UpdateTileChrome", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [stream, null]);

            var chromeKeys = (IDictionary)Field(window, "_tileChromeRenderKeys").GetValue(window)!;
            var statusKeys = (IDictionary)Field(window, "_tileStatusTextRenderKeys").GetValue(window)!;
            var chromeKeyBefore = (string?)chromeKeys[stream.Id]
                ?? throw new InvalidOperationException("Tile chrome key was not recorded.");
            var statusKeyBefore = (string?)statusKeys[stream.Id]
                ?? throw new InvalidOperationException("Tile status text key was not recorded.");

            Pump(window, 300);
            playbackSnapshots[stream.Id] = Method(sessionType, "CurrentPlaybackSnapshot").Invoke(session, []);
            Method(typeof(MainWindow), "UpdateTileChrome", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [stream, null]);
            var chromeKeyAfter = (string?)chromeKeys[stream.Id];
            var statusKeyAfter = (string?)statusKeys[stream.Id];
            if (!string.Equals(chromeKeyBefore, chromeKeyAfter, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Video freshness text changes should not repaint tile chrome.");
            }
            if (string.Equals(statusKeyBefore, statusKeyAfter, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Video freshness text key did not update.");
            }

            var otherStream = activeStreams.Skip(1).FirstOrDefault();
            if (otherStream is not null)
            {
                chromeKeys[otherStream.Id] = "unchanged-other-tile";
                Method(typeof(MainWindow), "UpdateSingleTileChrome", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, [activeStreams, stream.Id]);
                if (!string.Equals((string?)chromeKeys[otherStream.Id], "unchanged-other-tile", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Immediate single-tile chrome update touched another active tile.");
                }
            }
        }
        finally
        {
            var ffmpegSessions = (IDictionary)Field(window, "_ffmpegPlaybackSessions").GetValue(window)!;
            ffmpegSessions.Remove(stream.Id);
            DisposeReflective(session);
        }
    }

    private static (object? SessionA, object? SessionB) AssertTwoFfmpegSessionsCanRun()
    {
        var ffmpeg = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        if (ffmpeg is null)
        {
            Console.WriteLine("ffmpeg_session_smoke_skipped: ffmpeg not found");
            return (null, null);
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"ipmv-desktop-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var fileA = Path.Combine(workDir, "stream-a.mp4");
        var fileB = Path.Combine(workDir, "stream-b.mp4");
        RunFfmpeg(ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", fileA);
        RunFfmpeg(ffmpeg, "-y", "-f", "lavfi", "-i", "smptebars=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "3", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", fileB);

        var sessionType = DesktopType("FfmpegPlaybackSession");
        var sessionA = CreateFfmpegSession(sessionType, ffmpeg, fileA);
        var sessionB = CreateFfmpegSession(sessionType, ffmpeg, fileB);
        Method(sessionType, "Start").Invoke(sessionA, []);
        Method(sessionType, "Start").Invoke(sessionB, []);

        var hasFrame = sessionType.GetProperty("HasFrame")!;
        var hasPresentedFrame = sessionType.GetProperty("HasPresentedFrame")!;
        var bitmapFramesQueued = sessionType.GetProperty("BitmapFramesQueued")!;
        var bitmapFramesCoalesced = sessionType.GetProperty("BitmapFramesCoalesced")!;
        var bitmapFramesPresented = sessionType.GetProperty("BitmapFramesPresented")!;
        var lastFramePresentedAt = sessionType.GetProperty("LastFramePresentedAt")!;
        var lastRenderDelayMs = sessionType.GetProperty("LastRenderDelayMs")!;
        var state = sessionType.GetProperty("State")!;
        var tryAudio = Method(sessionType, "TryGetAudioSnapshot");
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var gotFrameA = false;
        var gotFrameB = false;
        var gotPresentedFrameA = false;
        var gotPresentedFrameB = false;
        var gotAudioA = false;
        var gotAudioB = false;
        while (DateTime.UtcNow < deadline)
        {
            Pump(null, 150);
            gotFrameA = (bool)hasFrame.GetValue(sessionA)!;
            gotFrameB = (bool)hasFrame.GetValue(sessionB)!;
            gotPresentedFrameA = (bool)hasPresentedFrame.GetValue(sessionA)!;
            gotPresentedFrameB = (bool)hasPresentedFrame.GetValue(sessionB)!;
            gotAudioA = (bool)tryAudio.Invoke(sessionA, [null])!;
            gotAudioB = (bool)tryAudio.Invoke(sessionB, [null])!;
            if (gotFrameA && gotFrameB && gotPresentedFrameA && gotPresentedFrameB && gotAudioA && gotAudioB)
            {
                break;
            }
        }

        Console.WriteLine($"ffmpeg_session_a state={state.GetValue(sessionA)} frame={gotFrameA} presented={gotPresentedFrameA} coalesced={bitmapFramesCoalesced.GetValue(sessionA)} audio={gotAudioA}");
        Console.WriteLine($"ffmpeg_session_b state={state.GetValue(sessionB)} frame={gotFrameB} presented={gotPresentedFrameB} coalesced={bitmapFramesCoalesced.GetValue(sessionB)} audio={gotAudioB}");
        if (!gotFrameA || !gotFrameB || !gotPresentedFrameA || !gotPresentedFrameB || !gotAudioA || !gotAudioB ||
            (long)bitmapFramesQueued.GetValue(sessionA)! <= 0 ||
            (long)bitmapFramesQueued.GetValue(sessionB)! <= 0 ||
            (long)bitmapFramesPresented.GetValue(sessionA)! <= 0 ||
            (long)bitmapFramesPresented.GetValue(sessionB)! <= 0 ||
            (DateTimeOffset)lastFramePresentedAt.GetValue(sessionA)! <= DateTimeOffset.MinValue ||
            (DateTimeOffset)lastFramePresentedAt.GetValue(sessionB)! <= DateTimeOffset.MinValue ||
            (double)lastRenderDelayMs.GetValue(sessionA)! < 0 ||
            (double)lastRenderDelayMs.GetValue(sessionB)! < 0)
        {
            throw new InvalidOperationException("Two FFmpeg playback/analysis sessions did not run concurrently.");
        }

        AssertTwoSrtLoopbackSessionsCanRun(sessionType, ffmpeg);
        return (sessionA, sessionB);
    }

    private static void AssertTwoSrtLoopbackSessionsCanRun(Type sessionType, string ffmpeg)
    {
        if (!FfmpegSupportsProtocol(ffmpeg, "srt"))
        {
            Console.WriteLine("srt_loopback_smoke_skipped: ffmpeg srt protocol not available");
            return;
        }

        object? sessionA = null;
        object? sessionB = null;
        Process? senderA = null;
        Process? senderB = null;
        try
        {
            var portA = FreeUdpPort();
            var portB = FreeUdpPort();
            var inputA = $"srt://127.0.0.1:{portA}?mode=listener&transtype=live";
            var inputB = $"srt://127.0.0.1:{portB}?mode=listener&transtype=live";
            sessionA = CreateFfmpegSession(sessionType, ffmpeg, inputA, "srt");
            sessionB = CreateFfmpegSession(sessionType, ffmpeg, inputB, "srt");
            Method(sessionType, "Start").Invoke(sessionA, []);
            Method(sessionType, "Start").Invoke(sessionB, []);
            Pump(null, 250);

            senderA = StartFfmpegProcess(ffmpeg, "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=540:sample_rate=48000", "-t", "6", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", "-f", "mpegts", $"srt://127.0.0.1:{portA}?mode=caller&pkt_size=1316");
            senderB = StartFfmpegProcess(ffmpeg, "-re", "-f", "lavfi", "-i", "smptebars=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=960:sample_rate=48000", "-t", "6", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", "-f", "mpegts", $"srt://127.0.0.1:{portB}?mode=caller&pkt_size=1316");

            var hasFrame = sessionType.GetProperty("HasFrame")!;
            var hasPresentedFrame = sessionType.GetProperty("HasPresentedFrame")!;
            var state = sessionType.GetProperty("State")!;
            var tryAudio = Method(sessionType, "TryGetAudioSnapshot");
            var gotFrameA = false;
            var gotFrameB = false;
            var gotPresentedFrameA = false;
            var gotPresentedFrameB = false;
            var gotAudioA = false;
            var gotAudioB = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Pump(null, 150);
                gotFrameA = (bool)hasFrame.GetValue(sessionA)!;
                gotFrameB = (bool)hasFrame.GetValue(sessionB)!;
                gotPresentedFrameA = (bool)hasPresentedFrame.GetValue(sessionA)!;
                gotPresentedFrameB = (bool)hasPresentedFrame.GetValue(sessionB)!;
                gotAudioA = (bool)tryAudio.Invoke(sessionA, [null])!;
                gotAudioB = (bool)tryAudio.Invoke(sessionB, [null])!;
                if (gotFrameA && gotFrameB && gotPresentedFrameA && gotPresentedFrameB && gotAudioA && gotAudioB)
                {
                    break;
                }
            }

            Console.WriteLine($"srt_loopback_a state={state.GetValue(sessionA)} frame={gotFrameA} presented={gotPresentedFrameA} audio={gotAudioA}");
            Console.WriteLine($"srt_loopback_b state={state.GetValue(sessionB)} frame={gotFrameB} presented={gotPresentedFrameB} audio={gotAudioB}");
            if (!gotFrameA || !gotFrameB || !gotPresentedFrameA || !gotPresentedFrameB || !gotAudioA || !gotAudioB)
            {
                throw new InvalidOperationException("Two SRT loopback playback/analysis sessions did not run concurrently.");
            }
        }
        finally
        {
            DisposeReflective(sessionA);
            DisposeReflective(sessionB);
            StopProcess(senderA);
            StopProcess(senderB);
        }
    }

    private static List<string> CandidateList(MethodInfo build, string inputUrl, string protocol)
    {
        return ((IEnumerable)build.Invoke(null, [inputUrl, protocol])!).Cast<string>().ToList();
    }

    private static object CreateFfmpegSession(
        Type sessionType,
        string ffmpeg,
        string input,
        string protocol = "file",
        Action? blackFrameDetected = null,
        Action? firstFramePresented = null)
    {
        var constructor = sessionType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(item =>
            {
                var parameterCount = item.GetParameters().Length;
                return parameterCount is 9 or 10;
            });
        var args = new List<object?>
        {
            ffmpeg,
            input,
            protocol,
            Dispatcher.CurrentDispatcher,
            0.04,
            0.015,
            0.04,
            0.85,
            blackFrameDetected ?? (() => { })
        };
        if (constructor.GetParameters().Length == 10)
        {
            args.Add(firstFramePresented ?? (() => { }));
        }

        return constructor.Invoke(args.ToArray());
    }

    private static void RunFfmpeg(string ffmpeg, params string[] args)
    {
        using var process = StartFfmpegProcess(ffmpeg, args);
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private static Process StartFfmpegProcess(string ffmpeg, params string[] args)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }.WithArguments(args)) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
    }

    private static bool FfmpegSupportsProtocol(string ffmpeg, string protocol)
    {
        using var process = StartFfmpegProcess(ffmpeg, "-hide_banner", "-protocols");
        process.WaitForExit(10000);
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), protocol, StringComparison.OrdinalIgnoreCase));
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup for smoke-only ffmpeg senders.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? FindOnPath(string executable)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paths.Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists);
    }

    private static Type DesktopType(string name)
    {
        return typeof(MainWindow).Assembly.GetType($"IPMultiViewer.Desktop.{name}", throwOnError: true)!;
    }

    private static FieldInfo Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
    }

    private static MethodInfo Method(Type type, string name, BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    {
        return type.GetMethod(name, flags) ?? throw new MissingMethodException(type.FullName, name);
    }

    private static MethodInfo MethodWithParameterCount(
        Type type,
        string name,
        int parameterCount,
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    {
        return type
            .GetMethods(flags)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);
    }

    private static MethodInfo MethodWithParameterTypes(
        Type type,
        string name,
        Type[] parameterTypes,
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    {
        return type.GetMethod(name, flags, binder: null, types: parameterTypes, modifiers: null)
            ?? throw new MissingMethodException(type.FullName, name);
    }

    private static void DisposeReflective(object? target)
    {
        target?.GetType().GetMethod("Dispose")?.Invoke(target, []);
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

    private sealed class WpfAppScope : IDisposable
    {
        public WpfAppScope()
        {
            if (Application.Current is null)
            {
                _ = new Application();
            }
        }

        public void Dispose()
        {
            Application.Current?.Shutdown();
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }
}
