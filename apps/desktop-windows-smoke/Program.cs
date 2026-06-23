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
using System.Runtime.InteropServices;
using System.Threading;
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
            AssertConfigStorePathOverrides();
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

            AssertWindowIconIsSet(window);
            AssertLayoutSwitchDoesNotCrash(window);
            AssertNoStandaloneAnalysisMonitor(window);
            AssertActiveLayoutStreamsAreConcreteAndUnique(window);
            AssertVisibleLayoutOnlyMonitored(window);
            AssertActiveAlertsByStreamIsExhaustive(window);
            AssertTextSetHelperSkipsUnchangedText();
            AssertSrtCandidateModes();
            AssertLowLatencyPlaybackOptions();
            AssertPlaybackRestartBackoff();
            AssertGpuRenderingUsesLibVlcPlayback(window);
            AssertPlaybackStatusWaitsForPresentedVideo(window);
            AssertAudioMeterSidePanels(window);
            AssertAudioLevelMonitorThrottlesCallbacks();
            AssertSharedSidecarAnalysisMonitors(window);
            AssertFfmpegSnapshotSampling();
            AssertFfmpegSidecarAnalysisSnapshotSampling();
            AssertFfmpegAudioStatsParser();
            AssertTileFreshnessTextDoesNotRepaintChrome(window);
            AssertTileAssignOnlyRendersAffectedTiles(window);
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

    private static void AssertConfigStorePathOverrides()
    {
        var previousDataDir = Environment.GetEnvironmentVariable("IPMV_DATA_DIR");
        var previousConfigDir = Environment.GetEnvironmentVariable("IPMV_CONFIG_DIR");
        var previousLogDir = Environment.GetEnvironmentVariable("IPMV_LOG_DIR");
        try
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "ipmv-smoke-data-dir");
            Environment.SetEnvironmentVariable("IPMV_DATA_DIR", baseDir);
            Environment.SetEnvironmentVariable("IPMV_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("IPMV_LOG_DIR", null);
            if (!string.Equals(LocalConfigStore.ConfigDirectory, Path.Combine(baseDir, "config"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(LocalConfigStore.LogDirectory, Path.Combine(baseDir, "logs"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("IPMV_DATA_DIR did not isolate config/log directories.");
            }

            var configDir = Path.Combine(baseDir, "custom-config");
            var logDir = Path.Combine(baseDir, "custom-logs");
            Environment.SetEnvironmentVariable("IPMV_CONFIG_DIR", configDir);
            Environment.SetEnvironmentVariable("IPMV_LOG_DIR", logDir);
            if (!string.Equals(LocalConfigStore.DefaultConfigPath, Path.Combine(configDir, "local-config.json"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(LocalConfigStore.LogDirectory, logDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Explicit config/log directory overrides were not honored.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPMV_DATA_DIR", previousDataDir);
            Environment.SetEnvironmentVariable("IPMV_CONFIG_DIR", previousConfigDir);
            Environment.SetEnvironmentVariable("IPMV_LOG_DIR", previousLogDir);
        }
    }

    private static void AssertWindowIconIsSet(MainWindow window)
    {
        if (window.Icon is null)
        {
            throw new InvalidOperationException("Main window icon must be set so the taskbar uses the IPMV icon.");
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

    private static void AssertVisibleLayoutOnlyMonitored(MainWindow window)
    {
        var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
        var originalActiveLayoutId = config.ActiveLayoutId;
        var hiddenStream = new StreamConfig
        {
            Id = "smoke-hidden-outside-layout",
            Name = "Smoke hidden outside layout",
            Enabled = true,
            InputUrl = "rtsp://example.test/hidden",
            InputProtocol = "rtsp",
            AnalysisEnabled = true,
            AudioMonitorEnabled = true
        };
        LayoutConfig? layout = null;
        LayoutTileConfig? hiddenTile = null;
        AlertRecord? hiddenAlert = null;
        var issueSince = (IDictionary)Field(window, "_issueSince").GetValue(window)!;
        try
        {
            config.Streams.Add(hiddenStream);
            layout = config.Layouts.First(item => string.Equals(item.Kind, "3x3", StringComparison.OrdinalIgnoreCase));
            hiddenTile = new LayoutTileConfig { Index = 9, StreamId = hiddenStream.Id, Row = 3, Column = 0 };
            layout.Tiles.Add(hiddenTile);
            config.ActiveLayoutId = layout.Id;

            var activeStreams = ((IEnumerable)MethodWithParameterCount(typeof(MainWindow), "ActiveLayoutStreams", 0)
                .Invoke(window, [])!).Cast<StreamConfig>().ToList();
            if (activeStreams.Any(stream => string.Equals(stream.Id, hiddenStream.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A stream assigned outside the visible 3x3 tile range was still treated as monitored.");
            }

            hiddenAlert = new AlertRecord
            {
                Id = "smoke-hidden-alert",
                StreamId = hiddenStream.Id,
                Type = "video.black",
                Severity = "error",
                Status = "open",
                Message = "hidden"
            };
            config.AlertHistory.Add(hiddenAlert);
            issueSince[$"{hiddenStream.Id}:video.black"] = DateTimeOffset.UtcNow.AddSeconds(-10);
            var changed = (bool)Method(typeof(MainWindow), "ResolveMonitoringAlertsOutsideActiveLayout", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [null])!;
            if (!changed ||
                hiddenAlert.Status != "resolved" ||
                issueSince.Contains($"{hiddenStream.Id}:video.black"))
            {
                throw new InvalidOperationException("Monitoring state outside the visible layout was not resolved.");
            }
        }
        finally
        {
            config.ActiveLayoutId = originalActiveLayoutId;
            config.Streams.Remove(hiddenStream);
            if (layout is not null && hiddenTile is not null)
            {
                layout.Tiles.Remove(hiddenTile);
            }

            if (hiddenAlert is not null)
            {
                config.AlertHistory.Remove(hiddenAlert);
            }

            issueSince.Remove($"{hiddenStream.Id}:video.black");
        }
    }

    private static void AssertNoStandaloneAnalysisMonitor(MainWindow window)
    {
        var previousGpuEnv = Environment.GetEnvironmentVariable("IPMV_GPU_RENDERING");
        var previousGpuLongEnv = Environment.GetEnvironmentVariable("IPMULTIVIEWER_GPU_RENDERING");
        try
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", null);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_GPU_RENDERING", null);
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
                throw new InvalidOperationException("Analysis-enabled streams must route through shared FFmpeg playback in standard rendering mode.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", previousGpuEnv);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_GPU_RENDERING", previousGpuLongEnv);
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
            !ContainsArgumentPair(args, "-flags", "low_delay") ||
            !ContainsArgumentPair(args, "-threads", "1") ||
            !ContainsArgumentPair(args, "-filter_threads", "1"))
        {
            throw new InvalidOperationException("FFmpeg input arguments must keep low-latency probe and buffer settings.");
        }

        if (OperatingSystem.IsWindows() && !ContainsArgumentPair(args, "-hwaccel", "auto"))
        {
            throw new InvalidOperationException("FFmpeg playback should request best-effort hardware decode on Windows.");
        }

        var previousGpuEnv = Environment.GetEnvironmentVariable("IPMV_GPU_RENDERING");
        try
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", "1");
            var gpuInstanceOptions = ((IEnumerable<string>)Method(typeof(MainWindow), "LowLatencyVlcInstanceOptions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Invoke(null, [])!).ToList();
            if (!gpuInstanceOptions.Contains("--vout=direct3d11", StringComparer.OrdinalIgnoreCase) ||
                !gpuInstanceOptions.Contains("--avcodec-hw=d3d11va", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GPU rendering mode must enable Direct3D11 video output and D3D11VA hardware decode.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", previousGpuEnv);
        }
    }

    private static void AssertPlaybackRestartBackoff()
    {
        var delaySeconds = Method(typeof(MainWindow), "PlaybackRestartDelaySeconds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var delays = new[]
        {
            (double)delaySeconds.Invoke(null, [2.0, 0])!,
            (double)delaySeconds.Invoke(null, [5.0, 0])!,
            (double)delaySeconds.Invoke(null, [5.0, 1])!,
            (double)delaySeconds.Invoke(null, [5.0, 2])!,
            (double)delaySeconds.Invoke(null, [5.0, 3])!,
            (double)delaySeconds.Invoke(null, [5.0, 9])!
        };
        var expected = new[] { 5.0, 5.0, 10.0, 20.0, 40.0, 60.0 };
        if (!delays.SequenceEqual(expected))
        {
            throw new InvalidOperationException($"Playback restart backoff was {string.Join(", ", delays)}, want {string.Join(", ", expected)}.");
        }
    }

    private static void AssertGpuRenderingUsesLibVlcPlayback(MainWindow window)
    {
        var previousGpuEnv = Environment.GetEnvironmentVariable("IPMV_GPU_RENDERING");
        var previousForceEnv = Environment.GetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK");
        var previousGpuAudioMetersEnv = Environment.GetEnvironmentVariable("IPMV_GPU_AUDIO_METERS");
        try
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", "1");
            Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", null);
            Environment.SetEnvironmentVariable("IPMV_GPU_AUDIO_METERS", null);
            var srtStream = new StreamConfig
            {
                Id = "smoke-gpu-srt",
                Name = "Smoke GPU SRT",
                Enabled = true,
                InputUrl = "srt://127.0.0.1:12000",
                InputProtocol = "srt",
                AnalysisEnabled = true,
                AudioMonitorEnabled = true
            };
            var rtspAnalysisStream = new StreamConfig
            {
                Id = "smoke-gpu-rtsp",
                Name = "Smoke GPU RTSP",
                Enabled = true,
                InputUrl = "rtsp://example.test/live",
                InputProtocol = "rtsp",
                AnalysisEnabled = true,
                AudioMonitorEnabled = true
            };
            var shouldUseFfmpeg = (bool)Method(window.GetType(), "ShouldUseFfmpegPlayback").Invoke(window, [rtspAnalysisStream])!;
            if (shouldUseFfmpeg)
            {
                throw new InvalidOperationException("GPU rendering mode must keep analysis-enabled RTSP streams on LibVLC playback instead of FFmpeg bitmap playback.");
            }

            var shouldAttachAudioMonitor = (bool)Method(window.GetType(), "ShouldAttachAudioMonitor", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [rtspAnalysisStream])!;
            if (shouldAttachAudioMonitor)
            {
                throw new InvalidOperationException("GPU rendering mode must keep LibVLC audio callbacks disabled unless explicitly requested.");
            }

            Environment.SetEnvironmentVariable("IPMV_GPU_AUDIO_METERS", "1");
            shouldAttachAudioMonitor = (bool)Method(window.GetType(), "ShouldAttachAudioMonitor", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [rtspAnalysisStream])!;
            if (!shouldAttachAudioMonitor)
            {
                throw new InvalidOperationException("IPMV_GPU_AUDIO_METERS=1 must re-enable GPU audio meter callbacks.");
            }

            var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
            var previousGlobalMute = config.GlobalMute;
            try
            {
                var disablePlaybackAudio = Method(window.GetType(), "ShouldDisableLibVlcPlaybackAudio", BindingFlags.Instance | BindingFlags.NonPublic);
                config.GlobalMute = true;
                Environment.SetEnvironmentVariable("IPMV_GPU_AUDIO_METERS", null);
                if ((bool)disablePlaybackAudio.Invoke(window, [rtspAnalysisStream, null])! != true)
                {
                    throw new InvalidOperationException("Muted GPU playback without audio callbacks must pass :no-audio to LibVLC.");
                }

                config.GlobalMute = false;
                if ((bool)disablePlaybackAudio.Invoke(window, [rtspAnalysisStream, null])!)
                {
                    throw new InvalidOperationException("Unmuted GPU playback must keep audio available when the stream requests audio monitoring.");
                }

                rtspAnalysisStream.AudioMonitorEnabled = false;
                if ((bool)disablePlaybackAudio.Invoke(window, [rtspAnalysisStream, null])! != true)
                {
                    throw new InvalidOperationException("Streams with audio monitoring disabled must pass :no-audio to LibVLC.");
                }
            }
            finally
            {
                rtspAnalysisStream.AudioMonitorEnabled = true;
                config.GlobalMute = previousGlobalMute;
            }

            if (FindOnPath("ffmpeg.exe") is not null || FindOnPath("ffmpeg") is not null)
            {
                var srtUsesFfmpeg = (bool)Method(window.GetType(), "ShouldUseFfmpegPlayback").Invoke(window, [srtStream])!;
                if (srtUsesFfmpeg)
                {
                    throw new InvalidOperationException("GPU rendering mode must keep SRT playback on LibVLC and move analysis to the sidecar.");
                }
            }

            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", null);
            var standardSrtUsesFfmpeg = (bool)Method(window.GetType(), "ShouldUseFfmpegPlayback").Invoke(window, [srtStream])!;
            if (!standardSrtUsesFfmpeg)
            {
                throw new InvalidOperationException("Standard rendering mode should still use the FFmpeg fallback for SRT streams.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", previousGpuEnv);
            Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", previousForceEnv);
            Environment.SetEnvironmentVariable("IPMV_GPU_AUDIO_METERS", previousGpuAudioMetersEnv);
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
        Field(window, "_lastAudioMeterAnimationAt").SetValue(window, DateTimeOffset.UtcNow.AddMilliseconds(-100));
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

        meterViewType.GetProperty("TargetLevelRatio")!.SetValue(firstView, 0.05);
        Method(typeof(MainWindow), "AudioMeterCompositionTarget_Rendering", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, [null, EventArgs.Empty]);
        var throttledLevel = (double)meterViewType.GetProperty("DisplayedLevelRatio")!.GetValue(firstView)!;
        if (Math.Abs(throttledLevel - displayedLevel) > 0.0001)
        {
            throw new InvalidOperationException("Audio meter animation throttling did not hold an immediate render tick.");
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

    private static void AssertAudioLevelMonitorThrottlesCallbacks()
    {
        var monitorType = DesktopType("AudioLevelMonitor");
        var monitor = Activator.CreateInstance(
            monitorType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [],
            culture: null) ?? throw new InvalidOperationException("Failed to create audio level monitor.");
        try
        {
            Field(monitor, "_channelCount").SetValue(monitor, 2);
            var play = MethodWithParameterTypes(
                monitorType,
                "OnAudioPlay",
                [typeof(IntPtr), typeof(IntPtr), typeof(uint), typeof(long)]);
            var tryGetSnapshot = Method(monitorType, "TryGetSnapshot");
            var silent = new short[480];
            var loud = Enumerable.Repeat((short)20000, 480).ToArray();

            InvokeAudioPlay(play, monitor, silent, frames: 240);
            var levelAfterSilence = AudioLevelDb(tryGetSnapshot, monitor);
            if (levelAfterSilence > -100)
            {
                throw new InvalidOperationException($"Silent audio snapshot level = {levelAfterSilence:0.0} dBFS, want silence floor.");
            }

            InvokeAudioPlay(play, monitor, loud, frames: 240);
            var levelAfterImmediateLoud = AudioLevelDb(tryGetSnapshot, monitor);
            if (levelAfterImmediateLoud > -100)
            {
                throw new InvalidOperationException("Audio level monitor processed an immediate callback instead of throttling it.");
            }

            Thread.Sleep(75);
            InvokeAudioPlay(play, monitor, loud, frames: 240);
            var levelAfterThrottledWindow = AudioLevelDb(tryGetSnapshot, monitor);
            if (levelAfterThrottledWindow < -10)
            {
                throw new InvalidOperationException($"Audio level monitor did not resume after throttle window: {levelAfterThrottledWindow:0.0} dBFS.");
            }
        }
        finally
        {
            DisposeReflective(monitor);
        }
    }

    private static void InvokeAudioPlay(MethodInfo play, object monitor, short[] samples, uint frames)
    {
        var byteCount = samples.Length * sizeof(short);
        var sampleBuffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            Marshal.Copy(samples, 0, sampleBuffer, samples.Length);
            play.Invoke(monitor, [IntPtr.Zero, sampleBuffer, frames, 0L]);
        }
        finally
        {
            Marshal.FreeHGlobal(sampleBuffer);
        }
    }

    private static double AudioLevelDb(MethodInfo tryGetSnapshot, object monitor)
    {
        var args = new object?[] { null };
        if ((bool)tryGetSnapshot.Invoke(monitor, args)! != true || args[0] is null)
        {
            throw new InvalidOperationException("Audio level monitor did not expose a snapshot.");
        }

        return (double)args[0]!.GetType().GetProperty("LevelDb")!.GetValue(args[0])!;
    }

    private static void AssertSharedSidecarAnalysisMonitors(MainWindow window)
    {
        var profile = new AlertProfileConfig();
        var streamA = new StreamConfig
        {
            Id = "shared-sidecar-a",
            Name = "Shared sidecar A",
            InputUrl = "rtsp://example.test/shared",
            InputProtocol = "rtsp",
            AnalysisEnabled = true
        };
        var streamB = new StreamConfig
        {
            Id = "shared-sidecar-b",
            Name = "Shared sidecar B",
            InputUrl = "rtsp://example.test/shared",
            InputProtocol = "rtsp",
            AnalysisEnabled = true
        };
        var streamDifferentThreshold = new StreamConfig
        {
            Id = "shared-sidecar-c",
            Name = "Shared sidecar C",
            InputUrl = "rtsp://example.test/shared",
            InputProtocol = "rtsp",
            AnalysisEnabled = true
        };
        var keyMethod = Method(typeof(MainWindow), "SidecarAnalysisShareKey", BindingFlags.Static | BindingFlags.NonPublic);
        var keyA = (string)keyMethod.Invoke(null, [streamA, profile])!;
        var keyB = (string)keyMethod.Invoke(null, [streamB, profile])!;
        var differentProfile = new AlertProfileConfig { BlackPixelRatioThreshold = 0.50 };
        var keyC = (string)keyMethod.Invoke(null, [streamDifferentThreshold, differentProfile])!;
        if (!string.Equals(keyA, keyB, StringComparison.Ordinal) ||
            string.Equals(keyA, keyC, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sidecar analysis share keys did not match identical inputs or separate threshold variants.");
        }

        var monitorType = DesktopType("FfmpegVideoAnalysisMonitor");
        var monitor = CreateSidecarMonitor(monitorType, blackFrameDetected: null);
        var videoMonitors = (IDictionary)Field(window, "_videoMonitors").GetValue(window)!;
        var shareKeys = (IDictionary)Field(window, "_videoMonitorShareKeys").GetValue(window)!;
        var sharedMonitors = (IDictionary)Field(window, "_sharedVideoMonitors").GetValue(window)!;
        try
        {
            videoMonitors[streamA.Id] = monitor;
            videoMonitors[streamB.Id] = monitor;
            shareKeys[streamA.Id] = keyA;
            shareKeys[streamB.Id] = keyA;
            sharedMonitors[keyA] = monitor;

            Method(typeof(MainWindow), "StopVideoMonitor", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [streamA.Id]);
            if ((bool)Field(monitor, "_disposed").GetValue(monitor)! ||
                !videoMonitors.Contains(streamB.Id) ||
                !sharedMonitors.Contains(keyA))
            {
                throw new InvalidOperationException("Shared sidecar monitor was disposed while another stream still referenced it.");
            }

            Method(typeof(MainWindow), "StopVideoMonitor", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [streamB.Id]);
            if (!(bool)Field(monitor, "_disposed").GetValue(monitor)! ||
                sharedMonitors.Contains(keyA))
            {
                throw new InvalidOperationException("Shared sidecar monitor was not disposed after the last stream reference stopped.");
            }
        }
        finally
        {
            videoMonitors.Remove(streamA.Id);
            videoMonitors.Remove(streamB.Id);
            shareKeys.Remove(streamA.Id);
            shareKeys.Remove(streamB.Id);
            sharedMonitors.Remove(keyA);
            DisposeReflective(monitor);
        }
    }

    private static void AssertFfmpegSidecarAnalysisSnapshotSampling()
    {
        var monitorType = DesktopType("FfmpegVideoAnalysisMonitor");
        var ffmpegInput = (string)Method(monitorType, "FfmpegInputUrl", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, ["file:///C:/Temp/ipmv/input.mp4"])!;
        if (!string.Equals(ffmpegInput, @"C:\Temp\ipmv\input.mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FFmpeg sidecar file URI conversion was unexpected: {ffmpegInput}");
        }

        var filter = (string)(monitorType.GetField("AnalysisVideoFilter", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) ?? throw new InvalidOperationException("AnalysisVideoFilter value was unavailable."));
        if (!filter.Contains("fps=10", StringComparison.OrdinalIgnoreCase) ||
            !filter.Contains("flags=fast_bilinear", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FFmpeg sidecar analysis filter lost realtime black-detection sampling or fast scaling: {filter}");
        }

        if (OperatingSystem.IsWindows())
        {
            var args = new Collection<string>();
            Method(monitorType, "AddLowCostAnalysisInputArguments", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [args]);
            if (!ContainsArgumentPair(args, "-hwaccel", "auto") ||
                !ContainsArgumentPair(args, "-threads", "1") ||
                !ContainsArgumentPair(args, "-filter_threads", "1") ||
                !ContainsArgumentPair(args, "-fflags", "nobuffer") ||
                !ContainsArgumentPair(args, "-flags", "low_delay") ||
                !ContainsArgumentPair(args, "-probesize", "32768") ||
                !ContainsArgumentPair(args, "-analyzeduration", "0"))
            {
                throw new InvalidOperationException("FFmpeg sidecar analysis must request low-latency, low-cost decode arguments on Windows.");
            }
        }

        var blackCallbacks = 0;
        var monitor = CreateSidecarMonitor(monitorType, () => blackCallbacks++);
        try
        {
            Method(monitorType, "AddBlackFrameDetectedCallback")
                .Invoke(monitor, [(Action)(() => blackCallbacks++)]);
            var frameBytes = (int)(monitorType.GetField("FrameBytes", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue() ?? throw new InvalidOperationException("FrameBytes constant was unavailable."));
            var frame = new byte[frameBytes];
            Method(monitorType, "UpdateSnapshot").Invoke(monitor, [frame]);
            if (blackCallbacks != 2)
            {
                throw new InvalidOperationException($"Sidecar black frame callback count = {blackCallbacks}, want 2.");
            }

            var tryGetSnapshot = Method(monitorType, "TryGetSnapshot");
            var args = new object?[] { null };
            if ((bool)tryGetSnapshot.Invoke(monitor, args)! != true || args[0] is null)
            {
                throw new InvalidOperationException("FFmpeg sidecar monitor did not expose a sampled black snapshot.");
            }

            Array.Fill(frame, (byte)255);
            Method(monitorType, "UpdateSnapshot").Invoke(monitor, [frame]);
            if (blackCallbacks != 2)
            {
                throw new InvalidOperationException("FFmpeg sidecar monitor fired black callback for a white frame.");
            }
        }
        finally
        {
            DisposeReflective(monitor);
        }
    }

    private static object CreateSidecarMonitor(Type monitorType, Action? blackFrameDetected)
    {
        return Activator.CreateInstance(
            monitorType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                "ffmpeg",
                "file:///not-started",
                "file",
                0.04,
                0.015,
                0.04,
                0.85,
                blackFrameDetected
            ],
            culture: null) ?? throw new InvalidOperationException("Failed to create FFmpeg sidecar analysis monitor.");
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

            Method(sessionType, "QueueBitmapFrame").Invoke(session, [frame, DateTime.UtcNow.Ticks]);
            Pump(window, 150);
            playbackSnapshots[stream.Id] = Method(sessionType, "CurrentPlaybackSnapshot").Invoke(session, []);
            Method(typeof(MainWindow), "UpdateTileChrome", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [stream, null]);

            var chromeKeys = (IDictionary)Field(window, "_tileChromeRenderKeys").GetValue(window)!;
            var statusKeys = (IDictionary)Field(window, "_tileStatusTextRenderKeys").GetValue(window)!;
            var chromeKeyBefore = (string?)chromeKeys[stream.Id]
                ?? throw new InvalidOperationException("Tile chrome key was not recorded.");
            var statusKeyBefore = (string?)statusKeys[stream.Id]
                ?? throw new InvalidOperationException("Tile status text key was not recorded.");

            Method(sessionType, "QueueBitmapFrame").Invoke(session, [frame, DateTime.UtcNow.Ticks]);
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

    private static void AssertTileAssignOnlyRendersAffectedTiles(MainWindow window)
    {
        var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
        var originalActiveLayoutId = config.ActiveLayoutId;
        var streamA = new StreamConfig { Id = "smoke-assign-a", Name = "Smoke assign A", Enabled = true, InputUrl = "rtsp://example.test/a", InputProtocol = "rtsp" };
        var streamB = new StreamConfig { Id = "smoke-assign-b", Name = "Smoke assign B", Enabled = true, InputUrl = "rtsp://example.test/b", InputProtocol = "rtsp" };
        var streamC = new StreamConfig { Id = "smoke-assign-c", Name = "Smoke assign C", Enabled = true, InputUrl = "rtsp://example.test/c", InputProtocol = "rtsp" };
        var layout = new LayoutConfig
        {
            Id = "smoke-assign-layout",
            Name = "Smoke assign layout",
            Kind = "2x2",
            Tiles =
            [
                new LayoutTileConfig { Index = 0, StreamId = streamA.Id, Row = 0, Column = 0 },
                new LayoutTileConfig { Index = 1, StreamId = streamB.Id, Row = 0, Column = 1 },
                new LayoutTileConfig { Index = 2, StreamId = streamC.Id, Row = 1, Column = 0 },
                new LayoutTileConfig { Index = 3, StreamId = "", Row = 1, Column = 1 }
            ]
        };

        try
        {
            config.Streams.Add(streamA);
            config.Streams.Add(streamB);
            config.Streams.Add(streamC);
            config.Layouts.Add(layout);
            config.ActiveLayoutId = layout.Id;
            Method(typeof(MainWindow), "RenderTiles", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, []);
            Pump(window, 200);

            var chromeKeys = (IDictionary)Field(window, "_tileChromeRenderKeys").GetValue(window)!;
            chromeKeys[streamC.Id] = "unchanged-unaffected-tile";
            Method(typeof(MainWindow), "AssignStreamToTile", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [streamA.Id, 1]);
            Method(typeof(MainWindow), "RenderTilesAt", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [new[] { 0, 1 }]);

            if (!string.Equals((string?)chromeKeys[streamC.Id], "unchanged-unaffected-tile", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Assigning one tile repainted an unaffected monitoring tile.");
            }
        }
        finally
        {
            config.ActiveLayoutId = originalActiveLayoutId;
            config.Layouts.Remove(layout);
            config.Streams.Remove(streamA);
            config.Streams.Remove(streamB);
            config.Streams.Remove(streamC);
            Method(typeof(MainWindow), "RenderTiles", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, []);
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
