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
    private const double MinimumSmoothPresentedFps = 20.0;
    private const int SmoothFpsMeasurementMs = 2500;

    [STAThread]
    private static int Main()
    {
        object? sessionA = null;
        object? sessionB = null;
        var previousDataDir = Environment.GetEnvironmentVariable("IPMV_DATA_DIR");
        var previousConfigDir = Environment.GetEnvironmentVariable("IPMV_CONFIG_DIR");
        var previousLogDir = Environment.GetEnvironmentVariable("IPMV_LOG_DIR");
        var previousLongDataDir = Environment.GetEnvironmentVariable("IPMULTIVIEWER_DATA_DIR");
        var previousLongConfigDir = Environment.GetEnvironmentVariable("IPMULTIVIEWER_CONFIG_DIR");
        var previousLongLogDir = Environment.GetEnvironmentVariable("IPMULTIVIEWER_LOG_DIR");
        var previousDisableInitialPlayback = Environment.GetEnvironmentVariable("IPMV_DISABLE_INITIAL_PLAYBACK");
        var smokeBaseDir = Path.Combine(Path.GetTempPath(), $"ipmv-runtime-smoke-{Guid.NewGuid():N}");
        try
        {
            AssertConfigStorePathOverrides();
            Environment.SetEnvironmentVariable("IPMV_DATA_DIR", smokeBaseDir);
            Environment.SetEnvironmentVariable("IPMV_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("IPMV_LOG_DIR", null);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_DATA_DIR", null);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_LOG_DIR", null);
            Environment.SetEnvironmentVariable("IPMV_DISABLE_INITIAL_PLAYBACK", "1");
            AssertQueuedConfigPersistence(smokeBaseDir);
            WriteOfflineWindowFixture();
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
            StopWindowTimers(window);

            AssertWindowIconIsSet(window);
            AssertLayoutSwitchDoesNotCrash(window);
            AssertNoStandaloneAnalysisMonitor(window);
            AssertActiveLayoutStreamsAreConcreteAndUnique(window);
            AssertVisibleLayoutOnlyMonitored(window);
            AssertActiveAlertsByStreamIsExhaustive(window);
            AssertTextSetHelperSkipsUnchangedText();
            AssertSrtCandidateModes();
            AssertLowLatencyPlaybackOptions();
            AssertPlaybackStartPlanning();
            AssertPlaybackRestartBackoff();
            AssertReceiverRestartPolicy();
            AssertPlaybackStartupSlotsAreEnforced(window);
            AssertPlaybackConcurrencyWiring(window);
            AssertGpuRenderingUsesLibVlcPlayback(window);
            Console.WriteLine("playback_routing_smoke_ok");
            AssertPlaybackStatusWaitsForPresentedVideo(window);
            AssertPlaybackSurfaceClearsStaleVideo(window);
            AssertAudioLevelMonitorThrottlesCallbacks();
            AssertSharedSidecarAnalysisMonitors(window);
            AssertFfmpegSnapshotSampling();
            AssertImmediateBlackFrameRaisesAlert(window);
            Console.WriteLine("alert_evaluation_smoke_ok");
            AssertFfmpegSidecarAnalysisSnapshotSampling();
            Console.WriteLine("sidecar_analysis_contract_smoke_ok");
            AssertFfmpegAudioStatsParser();
            Console.WriteLine("audio_stats_contract_smoke_ok");
            AssertMissingVideoPtsFallbackIsBounded();
            Console.WriteLine("offline_media_contract_smoke_ok");
            if (string.Equals(
                    Environment.GetEnvironmentVariable("IPMV_SKIP_MEDIA_INTEGRATION_QA"),
                    "1",
                    StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("ffmpeg_media_integration_smoke_skipped: explicitly disabled for deterministic verification");
            }
            else
            {
                (sessionA, sessionB) = AssertTwoFfmpegSessionsCanRun(Path.Combine(smokeBaseDir, "ffmpeg-sessions"));
            }

            AssertMainWindowCloseReleasesAuxiliaryWindows(window);
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
            Environment.SetEnvironmentVariable("IPMV_DATA_DIR", previousDataDir);
            Environment.SetEnvironmentVariable("IPMV_CONFIG_DIR", previousConfigDir);
            Environment.SetEnvironmentVariable("IPMV_LOG_DIR", previousLogDir);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_DATA_DIR", previousLongDataDir);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_CONFIG_DIR", previousLongConfigDir);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_LOG_DIR", previousLongLogDir);
            Environment.SetEnvironmentVariable("IPMV_DISABLE_INITIAL_PLAYBACK", previousDisableInitialPlayback);
            try
            {
                if (Directory.Exists(smokeBaseDir))
                {
                    Directory.Delete(smokeBaseDir, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup; a failed delete should not mask the smoke result.
            }
        }
    }

    private static void WriteOfflineWindowFixture()
    {
        var config = LocalConfigStore.CreateDefault();
        foreach (var stream in config.Streams)
        {
            stream.Enabled = false;
        }

        config.ServerSyncEnabled = false;
        config.AlertSoundEnabled = false;
        LocalConfigStore.Save(config);
    }

    private static void StopWindowTimers(MainWindow window)
    {
        ((DispatcherTimer)Field(window, "_playbackTimer").GetValue(window)!).Stop();
        ((DispatcherTimer)Field(window, "_audioMeterTimer").GetValue(window)!).Stop();
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

    private static void AssertQueuedConfigPersistence(string smokeBaseDir)
    {
        var previousConfigDir = Environment.GetEnvironmentVariable("IPMV_CONFIG_DIR");
        var previousLongConfigDir = Environment.GetEnvironmentVariable("IPMULTIVIEWER_CONFIG_DIR");
        var queueConfigDir = Path.Combine(smokeBaseDir, "queued-config");
        var config = new LocalConfig
        {
            ClientId = "queued-save-smoke",
            ClientName = "Queued save smoke",
            AlertSoundEnabled = false
        };
        config.AlertProfiles.Add(new AlertProfileConfig());
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 1250; index++)
        {
            config.AlertHistory.Add(new AlertRecord
            {
                Id = $"terminal-{index:0000}",
                StreamId = "retention-smoke",
                Type = "video.no_signal",
                Severity = "warning",
                Status = index % 2 == 0 ? "resolved" : "ignored",
                Message = $"Terminal alert {index}",
                StartedAt = now.AddSeconds(-index - 10),
                UpdatedAt = now.AddSeconds(-index),
                SourceClientId = config.ClientId
            });
        }

        var activeIds = new[] { "active-open", "active-acknowledged", "active-snoozed" };
        var activeStatuses = new[] { "open", "acknowledged", "snoozed" };
        for (var index = 0; index < activeIds.Length; index++)
        {
            config.AlertHistory.Add(new AlertRecord
            {
                Id = activeIds[index],
                StreamId = "retention-smoke",
                Type = "video.freeze",
                Severity = "error",
                Status = activeStatuses[index],
                Message = $"Active alert {index}",
                StartedAt = now.AddSeconds(index),
                UpdatedAt = now.AddSeconds(index),
                SnoozedUntil = activeStatuses[index] == "snoozed" ? now.AddMinutes(5) : null,
                SourceClientId = config.ClientId
            });
        }

        Environment.SetEnvironmentVariable("IPMV_CONFIG_DIR", queueConfigDir);
        Environment.SetEnvironmentVariable("IPMULTIVIEWER_CONFIG_DIR", null);
        try
        {
            LocalConfigStore.QueueSave(config);
            AssertAlertRetention(config, activeIds, "queued in-memory config");
            LocalConfigStore.FlushQueuedSave(config);

            var json = File.ReadAllText(LocalConfigStore.DefaultConfigPath);
            if (json.Contains('\n') || json.Contains('\r'))
            {
                throw new InvalidOperationException("Queued configuration persistence regressed to indented JSON.");
            }

            var reloaded = LocalConfigStore.LoadOrCreate();
            AssertAlertRetention(reloaded, activeIds, "reloaded queued config");

            config.ClientName = "Queued save after exclusive lock";
            long queueElapsedMs;
            using (var lockStream = new FileStream(
                       LocalConfigStore.DefaultConfigPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var stopwatch = Stopwatch.StartNew();
                LocalConfigStore.QueueSave(config);
                stopwatch.Stop();
                queueElapsedMs = stopwatch.ElapsedMilliseconds;
                Thread.Sleep(220);
            }

            LocalConfigStore.FlushQueuedSave(config);
            if (queueElapsedMs > 100)
            {
                throw new InvalidOperationException(
                    $"QueueSave blocked its caller for {queueElapsedMs}ms while the destination was locked; want <= 100ms.");
            }

            var afterLockedWrite = LocalConfigStore.LoadOrCreate();
            AssertAlertRetention(afterLockedWrite, activeIds, "queued config after exclusive-lock retry");
            if (!string.Equals(afterLockedWrite.ClientName, config.ClientName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Queued configuration did not persist the latest payload after the file lock was released.");
            }
        }
        finally
        {
            try
            {
                LocalConfigStore.FlushQueuedSave(config);
            }
            catch
            {
                // Preserve the primary smoke failure while ensuring the writer is drained before restoring its path.
            }
            Environment.SetEnvironmentVariable("IPMV_CONFIG_DIR", previousConfigDir);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_CONFIG_DIR", previousLongConfigDir);
        }
    }

    private static void AssertAlertRetention(LocalConfig config, IReadOnlyCollection<string> activeIds, string state)
    {
        var terminalCount = config.AlertHistory.Count(alert =>
            string.Equals(alert.Status, "resolved", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(alert.Status, "ignored", StringComparison.OrdinalIgnoreCase));
        if (terminalCount > LocalConfigStore.TerminalAlertRetentionLimit)
        {
            throw new InvalidOperationException(
                $"{state} retained {terminalCount} terminal alerts; limit is {LocalConfigStore.TerminalAlertRetentionLimit}.");
        }

        var retainedActiveIds = config.AlertHistory
            .Where(alert => activeIds.Contains(alert.Id, StringComparer.Ordinal))
            .Select(alert => alert.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (retainedActiveIds.Count != activeIds.Count)
        {
            throw new InvalidOperationException(
                $"{state} lost active alerts: {string.Join(", ", activeIds.Where(id => !retainedActiveIds.Contains(id)))}.");
        }
    }

    private static void AssertWindowIconIsSet(MainWindow window)
    {
        if (window.Icon is null)
        {
            throw new InvalidOperationException("Main window icon must be set so the taskbar uses the IPMV icon.");
        }
    }

    private static void AssertMainWindowCloseReleasesAuxiliaryWindows(MainWindow window)
    {
        var app = Application.Current ?? throw new InvalidOperationException("WPF Application was not initialized.");
        var settingsWindow = (Window)Field(window, "_settingsWindow").GetValue(window)!;
        settingsWindow.Show();
        Pump(window, 100);
        if (!app.Windows.Cast<Window>().Contains(settingsWindow))
        {
            throw new InvalidOperationException("Settings window was not registered with the WPF application before close verification.");
        }

        window.Close();
        Pump(null, 100);
        var remainingWindows = app.Windows
            .Cast<Window>()
            .Where(item => ReferenceEquals(item, window) ||
                ReferenceEquals(item, settingsWindow) ||
                item.GetType().Name.Contains("AlertHistory", StringComparison.Ordinal))
            .Select(item => item.GetType().Name)
            .ToArray();
        if (remainingWindows.Length > 0)
        {
            throw new InvalidOperationException($"Main window close left auxiliary windows alive: {string.Join(", ", remainingWindows)}.");
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

            var shouldRoute = (bool)Method(
                    window.GetType(),
                    "ShouldRouteThroughFfmpegPlayback",
                    BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [stream])!;
            if (!shouldRoute)
            {
                throw new InvalidOperationException("Analysis-enabled streams must route through shared FFmpeg playback in standard rendering mode.");
            }

            AssertFfmpegAvailabilityGatesRouting(window, stream);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", previousGpuEnv);
            Environment.SetEnvironmentVariable("IPMULTIVIEWER_GPU_RENDERING", previousGpuLongEnv);
        }
    }

    private static void AssertFfmpegAvailabilityGatesRouting(MainWindow window, StreamConfig stream)
    {
        var previousFfmpeg = Environment.GetEnvironmentVariable("FFMPEG_BIN");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var sentinel = Path.Combine(LocalConfigStore.ConfigDirectory, "ffmpeg-routing-sentinel.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
            File.WriteAllBytes(sentinel, []);
            Environment.SetEnvironmentVariable("FFMPEG_BIN", sentinel);
            var shouldUse = Method(window.GetType(), "ShouldUseFfmpegPlayback");
            if (!(bool)shouldUse.Invoke(window, [stream])!)
            {
                throw new InvalidOperationException("Available FFmpeg did not enable the required playback route.");
            }

            Environment.SetEnvironmentVariable("FFMPEG_BIN", $"{sentinel}.missing");
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            if ((bool)shouldUse.Invoke(window, [stream])!)
            {
                throw new InvalidOperationException("Unavailable FFmpeg incorrectly enabled FFmpeg playback.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FFMPEG_BIN", previousFfmpeg);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            File.Delete(sentinel);
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
        var effectiveMode = Method(
            sessionType,
            "EffectiveSrtMode",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var isPassive = Method(
            typeof(MainWindow),
            "IsPassiveReceiverInput",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
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

        var listenerFromProtocol = (string)effectiveMode.Invoke(null, ["srt://127.0.0.1:12000", "srt-listener"])!;
        var callerFromUrl = (string)effectiveMode.Invoke(null, ["srt://127.0.0.1:12000?latency=80&MODE=caller", "srt-listener"])!;
        var rendezvousFromUrl = (string)effectiveMode.Invoke(null, ["srt://127.0.0.1:12000?mode=rendezvous", "srt-caller"])!;
        var bareMode = (string)effectiveMode.Invoke(null, ["srt://127.0.0.1:12000", "srt"])!;
        if (!string.Equals(listenerFromProtocol, "listener", StringComparison.Ordinal) ||
            !string.Equals(callerFromUrl, "caller", StringComparison.Ordinal) ||
            !string.Equals(rendezvousFromUrl, "rendezvous", StringComparison.Ordinal) ||
            !string.Equals(bareMode, "caller", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Effective SRT mode precedence changed: protocol={listenerFromProtocol}, caller-url={callerFromUrl}, rendezvous-url={rendezvousFromUrl}, bare={bareMode}.");
        }

        if (!(bool)isPassive.Invoke(null, ["srt://127.0.0.1:12000", "srt-listener"])! ||
            (bool)isPassive.Invoke(null, ["srt://127.0.0.1:12000?mode=caller", "srt-listener"])! ||
            !(bool)isPassive.Invoke(null, ["srt://127.0.0.1:12000?mode=listener", "srt-caller"])! ||
            !(bool)isPassive.Invoke(null, ["srt://127.0.0.1:12000?mode=rendezvous", "srt-caller"])! ||
            (bool)isPassive.Invoke(null, ["rtsp://127.0.0.1/live", "rtsp"])!)
        {
            throw new InvalidOperationException("Passive receiver classification did not honor the explicit SRT URL mode before the protocol hint.");
        }

        Console.WriteLine("srt_mode_precedence_smoke_ok url_mode=authoritative passive=listener+rendezvous");
    }

    private static void AssertLowLatencyPlaybackOptions()
    {
        var instanceOptions = ((IEnumerable<string>)Method(typeof(MainWindow), "LowLatencyVlcInstanceOptions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, [])!).ToList();
        var mediaOptions = ((IEnumerable<string>)Method(typeof(MainWindow), "LowLatencyVlcMediaOptions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, [false])!).ToList();
        var analysisMediaOptions = ((IEnumerable<string>)Method(typeof(MainWindow), "LowLatencyVlcMediaOptions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, [true])!).ToList();

        AssertNoDeprecatedCache(instanceOptions, "VLC instance");
        AssertNoDeprecatedCache(mediaOptions, "VLC media");
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

        var audioStatsFilter = (string)Method(sessionType, "BuildAudioStatsFilter", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, [])!;
        if (!audioStatsFilter.Contains("measure_perchannel=RMS_level+Peak_level", StringComparison.Ordinal) ||
            !audioStatsFilter.Contains("measure_overall=RMS_level+Peak_level", StringComparison.Ordinal) ||
            !audioStatsFilter.Contains("file='pipe\\:1':direct=1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FFmpeg audio stats filter must keep bounded RMS/peak-only metadata on stdout: {audioStatsFilter}");
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
            if (!gpuInstanceOptions.Contains("--vout=direct3d9", StringComparer.OrdinalIgnoreCase) ||
                !gpuInstanceOptions.Contains("--avcodec-hw=dxva2", StringComparer.OrdinalIgnoreCase) ||
                !gpuInstanceOptions.Contains("--embedded-video", StringComparer.OrdinalIgnoreCase) ||
                !gpuInstanceOptions.Contains("--no-video-deco", StringComparer.OrdinalIgnoreCase) ||
                gpuInstanceOptions.Contains("--vout=direct3d11", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GPU rendering mode must use embedded Direct3D9/DXVA2 video output for the tile HWND.");
            }

            var gpuFfmpegArgs = new Collection<string>();
            Method(sessionType, "AddCommonFfmpegInputArguments", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [gpuFfmpegArgs, "rtsp", "rtsp://example.test/live"]);
            if (OperatingSystem.IsWindows() && !ContainsArgumentPair(gpuFfmpegArgs, "-hwaccel", "d3d11va"))
            {
                throw new InvalidOperationException("GPU rendering mode must request D3D11VA hardware decode for FFmpeg playback.");
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

        var streamDelaySeconds = Method(
            typeof(MainWindow),
            "PlaybackRestartDelaySecondsForStream",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var streamIds = new[] { "smoke-restart-a", "smoke-restart-b", "smoke-restart-c", "smoke-restart-d" };
        foreach (var (inputTimeoutSec, attempt) in new[]
                 {
                     (2.0, 0),
                     (5.0, 1),
                     (20.0, 0),
                     (5.0, 3),
                     (30.0, 1),
                     (5.0, 9)
                 })
        {
            var baseDelay = (double)delaySeconds.Invoke(null, [inputTimeoutSec, attempt])!;
            var maximumDelay = Math.Min(60.0, baseDelay + Math.Min(2.0, baseDelay * 0.25));
            var jittered = streamIds
                .Select(streamId => (double)streamDelaySeconds.Invoke(null, [inputTimeoutSec, attempt, streamId])!)
                .ToArray();
            if (jittered.Any(delay => !double.IsFinite(delay) || delay < baseDelay || delay > maximumDelay) ||
                jittered.Where((delay, index) =>
                    delay != (double)streamDelaySeconds.Invoke(null, [inputTimeoutSec, attempt, streamIds[index]])!).Any() ||
                (baseDelay >= 60.0 && jittered.Any(delay => delay != 60.0)) ||
                (baseDelay < 60.0 && jittered.Distinct().Count() < 2))
            {
                throw new InvalidOperationException(
                    $"Per-stream restart jitter was not stable and bounded for timeout={inputTimeoutSec}, attempt={attempt}: {string.Join(", ", jittered)}.");
            }
        }
    }

    private static void AssertPlaybackStartPlanning()
    {
        var shouldRestart = Method(
            typeof(MainWindow),
            "ShouldRestartLoadedPlayback",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var state in new[] { "starting", "opening", "buffering", "rendering", "playing" })
        {
            if ((bool)shouldRestart.Invoke(null, [state, true])!)
            {
                throw new InvalidOperationException($"Manual Start would replace a healthy or opening receiver in state '{state}'.");
            }
        }

        foreach (var state in new[] { "waiting", "stopped", "ended", "error" })
        {
            if (!(bool)shouldRestart.Invoke(null, [state, true])!)
            {
                throw new InvalidOperationException($"Manual Start would not retry terminal receiver state '{state}'.");
            }

            if ((bool)shouldRestart.Invoke(null, [state, false])!)
            {
                throw new InvalidOperationException($"Automatic initial Start would replace loaded receiver state '{state}'.");
            }
        }
    }

    private static void AssertReceiverRestartPolicy()
    {
        var shouldRestart = Method(
            typeof(MainWindow),
            "ShouldRestartReceiver",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if ((bool)shouldRestart.Invoke(null, ["playing", false, false])! ||
            !(bool)shouldRestart.Invoke(null, ["playing", false, true])! ||
            !(bool)shouldRestart.Invoke(null, ["opening", true, false])! ||
            !(bool)shouldRestart.Invoke(null, ["ended", false, false])!)
        {
            throw new InvalidOperationException("Receiver restart policy did not separate render freshness from input progress and terminal state.");
        }

        var startupReady = Method(
            typeof(MainWindow),
            "IsPlaybackStartupReady",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var startupFailed = Method(
            typeof(MainWindow),
            "IsPlaybackStartupFailed",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if ((bool)startupReady.Invoke(null, ["opening", false])! ||
            !(bool)startupReady.Invoke(null, ["opening", true])! ||
            !(bool)startupReady.Invoke(null, ["playing", false])! ||
            (bool)startupReady.Invoke(null, ["error", false])! ||
            (bool)startupReady.Invoke(null, ["error", true])! ||
            (bool)startupFailed.Invoke(null, ["opening"])! ||
            (bool)startupFailed.Invoke(null, ["playing"])! ||
            !(bool)startupFailed.Invoke(null, ["waiting"])! ||
            !(bool)startupFailed.Invoke(null, ["error"])!)
        {
            throw new InvalidOperationException("Playback startup readiness did not distinguish usable input from terminal failure.");
        }
    }

    private static void AssertPlaybackStartupSlotsAreEnforced(MainWindow window)
    {
        var startupTimeout = (TimeSpan)(typeof(MainWindow)
            .GetField("PlaybackStartupSlotTimeout", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) ?? throw new InvalidOperationException("Playback startup timeout field was not found."));
        if (startupTimeout != TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException($"Playback startup soft admission budget changed unexpectedly: {startupTimeout}.");
        }

        var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
        var activeLayout = config.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, config.ActiveLayoutId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Startup slot smoke requires an active layout.");
        var activeStreams = activeLayout.Tiles
            .OrderBy(tile => tile.Index)
            .Select(tile => config.Streams.FirstOrDefault(stream =>
                string.Equals(stream.Id, tile.StreamId, StringComparison.OrdinalIgnoreCase)))
            .Where(stream => stream is not null)
            .Cast<StreamConfig>()
            .DistinctBy(stream => stream.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (activeStreams.Length < 4)
        {
            throw new InvalidOperationException("Startup slot smoke requires four streams assigned to the active layout.");
        }

        var testStreams = activeStreams.Take(4).ToArray();
        var testStreamIds = testStreams.Select(stream => stream.Id).ToArray();
        var originalStreamStates = activeStreams.ToDictionary(
            stream => stream.Id,
            stream => new PlaybackStreamFixtureState(
                stream.Enabled,
                stream.InputUrl,
                stream.InputProtocol,
                stream.AnalysisEnabled,
                stream.AudioMonitorEnabled),
            StringComparer.OrdinalIgnoreCase);
        var ffmpegSessions = (IDictionary)Field(window, "_ffmpegPlaybackSessions").GetValue(window)!;
        var playbackSnapshots = (IDictionary)Field(window, "_playbackSnapshots").GetValue(window)!;
        var lastProgressAt = (IDictionary)Field(window, "_lastPlaybackProgressAt").GetValue(window)!;
        var restartAfter = (IDictionary)Field(window, "_playbackRestartAfter").GetValue(window)!;
        var restartAttempts = (IDictionary)Field(window, "_playbackRestartAttempts").GetValue(window)!;
        var pendingRestarts = (ISet<string>)Field(window, "_pendingPlaybackRestarts").GetValue(window)!;
        var monitoringRequests = (ISet<string>)Field(window, "_monitoringRequestedStreamIds").GetValue(window)!;
        var touchedDictionaries = new[]
        {
            ffmpegSessions,
            playbackSnapshots,
            lastProgressAt,
            restartAfter,
            restartAttempts
        };
        var dictionaryStates = touchedDictionaries.ToDictionary(
            dictionary => dictionary,
            dictionary => testStreamIds.ToDictionary(
                streamId => streamId,
                streamId => new DictionaryEntryFixtureState(dictionary.Contains(streamId), dictionary[streamId]),
                StringComparer.OrdinalIgnoreCase));
        var originalMonitoringRequests = testStreamIds
            .Where(monitoringRequests.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originalPendingRestarts = testStreamIds
            .Where(pendingRestarts.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var startButton = (Button)Field(window, "StartPlaybackButton").GetValue(window)!;
        var stopButton = (Button)Field(window, "StopPlaybackButton").GetValue(window)!;
        var footer = (TextBlock)Field(window, "FooterText").GetValue(window)!;
        var startInFlightField = Field(window, "_playbackStartInFlight");
        var starterOverrideField = Field(window, "_playbackStarterOverride");
        var nextLaunchAtField = Field(window, "_nextPlaybackLaunchAt");
        var operationGenerationField = Field(window, "_playbackOperationGeneration");
        var startupSlots = (SemaphoreSlim)Field(window, "_playbackStartupSlots").GetValue(window)!;
        var originalStartButtonEnabled = startButton.IsEnabled;
        var originalStopButtonEnabled = stopButton.IsEnabled;
        var originalFooter = footer.Text;
        var originalStartInFlight = (bool)startInFlightField.GetValue(window)!;
        var originalStarterOverride = starterOverrideField.GetValue(window);
        var originalNextLaunchAt = nextLaunchAtField.GetValue(window);
        var originalOperationGeneration = operationGenerationField.GetValue(window);
        var originalSynchronizationContext = SynchronizationContext.Current;
        var sessionType = DesktopType("FfmpegPlaybackSession");
        var createdSessions = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var allCreatedSessions = new List<object>();
        var launchTimes = new List<DateTimeOffset>();

        void PumpUntil(Func<bool> predicate, TimeSpan timeout, string failure)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (!predicate() && DateTime.UtcNow < deadline)
            {
                Pump(window, 50);
            }

            if (!predicate())
            {
                throw new InvalidOperationException(failure);
            }
        }

        try
        {
            if (!originalStartButtonEnabled || originalStartInFlight || startupSlots.CurrentCount != 2)
            {
                throw new InvalidOperationException("Playback startup slots were not idle before the offline concurrency smoke.");
            }

            foreach (var stream in activeStreams)
            {
                stream.Enabled = false;
            }

            for (var index = 0; index < testStreams.Length; index++)
            {
                var stream = testStreams[index];
                stream.Enabled = true;
                stream.InputUrl = $"rtsp://127.0.0.1:{19201 + index}/offline";
                stream.InputProtocol = "rtsp";
                stream.AnalysisEnabled = false;
                stream.AudioMonitorEnabled = false;
            }

            foreach (var dictionary in touchedDictionaries)
            {
                foreach (var streamId in testStreamIds)
                {
                    dictionary.Remove(streamId);
                }
            }

            Func<StreamConfig, System.Threading.Tasks.Task> fakeStarter = stream =>
            {
                launchTimes.Add(DateTimeOffset.UtcNow);
                restartAfter.Remove(stream.Id);
                var session = CreateFfmpegSession(sessionType, "ffmpeg", stream.InputUrl, stream.InputProtocol);
                Field(session, "_state").SetValue(session, "opening");
                createdSessions[stream.Id] = session;
                allCreatedSessions.Add(session);
                ffmpegSessions[stream.Id] = session;
                playbackSnapshots[stream.Id] = CreatePlaybackSnapshot("opening", displayedPictures: 0);
                lastProgressAt[stream.Id] = DateTimeOffset.UtcNow;
                return System.Threading.Tasks.Task.CompletedTask;
            };
            starterOverrideField.SetValue(window, fakeStarter);
            nextLaunchAtField.SetValue(window, DateTimeOffset.MinValue);
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(window.Dispatcher));

            startButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(
                () => createdSessions.Count >= 2,
                TimeSpan.FromSeconds(1.5),
                "Two startup slots were not filled by the first two receivers.");
            Pump(window, 300);
            if (createdSessions.Count != 2 || startupSlots.CurrentCount != 0 || !(bool)startInFlightField.GetValue(window)!)
            {
                throw new InvalidOperationException(
                    $"Playback startup exceeded its two-slot limit before admission: created={createdSessions.Count}, available={startupSlots.CurrentCount}.");
            }

            PumpUntil(
                () => !(bool)startInFlightField.GetValue(window)!,
                TimeSpan.FromSeconds(13),
                "Playback startup did not release all slots after the soft admission budgets elapsed.");
            const string expectedSummary = "Starting playback complete: 4 started, 0 skipped, 0 failed.";
            if (createdSessions.Count != 4 ||
                startupSlots.CurrentCount != 2 ||
                testStreamIds.Any(streamId => !ffmpegSessions.Contains(streamId)) ||
                testStreamIds.Any(streamId => !ReferenceEquals(ffmpegSessions[streamId], createdSessions[streamId])) ||
                createdSessions.Values.Any(session =>
                    !string.Equals(Field(session, "_state").GetValue(session)?.ToString(), "opening", StringComparison.OrdinalIgnoreCase)) ||
                testStreamIds.Any(streamId => !monitoringRequests.Contains(streamId)) ||
                testStreamIds.Any(restartAfter.Contains) ||
                !footer.Text.StartsWith(expectedSummary, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Soft admission budget did not retain four opening owners and monitoring requests: created={createdSessions.Count}, available={startupSlots.CurrentCount}, owners={testStreamIds.Count(ffmpegSessions.Contains)}, intents={testStreamIds.Count(monitoringRequests.Contains)}, footer={footer.Text}");
            }

            if (launchTimes.Count != 4)
            {
                throw new InvalidOperationException($"Startup spacing smoke recorded {launchTimes.Count} launches; expected 4.");
            }
            for (var index = 1; index < launchTimes.Count; index++)
            {
                var spacing = launchTimes[index] - launchTimes[index - 1];
                if (spacing < TimeSpan.FromMilliseconds(200))
                {
                    throw new InvalidOperationException($"Receiver launches were not globally staggered: spacing={spacing.TotalMilliseconds:0.0}ms.");
                }
            }

            var terminalSession = createdSessions.Last();
            var healthyOwners = createdSessions
                .Where(pair => !string.Equals(pair.Key, terminalSession.Key, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var streamId in healthyOwners.Keys)
            {
                Field(healthyOwners[streamId], "_state").SetValue(healthyOwners[streamId], "playing");
                playbackSnapshots[streamId] = CreatePlaybackSnapshot("playing", displayedPictures: 12);
                lastProgressAt[streamId] = DateTimeOffset.UtcNow;
            }
            Field(terminalSession.Value, "_state").SetValue(terminalSession.Value, "ended");
            playbackSnapshots[terminalSession.Key] = CreatePlaybackSnapshot("ended", displayedPictures: 0);

            var mixedStartLaunchBaseline = launchTimes.Count;
            startButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(
                () => launchTimes.Count == mixedStartLaunchBaseline + 1,
                TimeSpan.FromSeconds(1.5),
                "Mixed healthy/failed Start did not select the failed receiver.");
            var retriedTerminalSession = createdSessions[terminalSession.Key];
            Field(retriedTerminalSession, "_state").SetValue(retriedTerminalSession, "ended");
            PumpUntil(
                () => !(bool)startInFlightField.GetValue(window)! &&
                    !ffmpegSessions.Contains(terminalSession.Key) &&
                    restartAfter.Contains(terminalSession.Key),
                TimeSpan.FromSeconds(1.5),
                "Terminal startup did not enter retrying while preserving the healthy receivers.");
            const string expectedMixedSummary = "Starting playback complete: 1 started, 3 skipped, 0 failed.";
            var terminalSnapshot = playbackSnapshots[terminalSession.Key];
            var terminalState = terminalSnapshot?.GetType().GetProperty("State")?.GetValue(terminalSnapshot)?.ToString();
            var terminalRetryAt = (DateTimeOffset)restartAfter[terminalSession.Key]!;
            if (healthyOwners.Any(pair => !ReferenceEquals(ffmpegSessions[pair.Key], pair.Value)) ||
                ReferenceEquals(retriedTerminalSession, terminalSession.Value) ||
                !string.Equals(terminalState, "retrying", StringComparison.OrdinalIgnoreCase) ||
                terminalRetryAt <= DateTimeOffset.UtcNow ||
                !monitoringRequests.Contains(terminalSession.Key) ||
                !footer.Text.StartsWith(expectedMixedSummary, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Mixed Start reset a healthy receiver or lost terminal retry intent: state={terminalState}, retryAt={terminalRetryAt:O}, footer={footer.Text}");
            }

            stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window, 100);
            var cancellationLaunchBaseline = launchTimes.Count;
            startButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(
                () => launchTimes.Count >= cancellationLaunchBaseline + 2,
                TimeSpan.FromSeconds(1.5),
                "Cancellation smoke did not fill the two startup slots.");
            if (!stopButton.IsEnabled)
            {
                throw new InvalidOperationException("Stop remained disabled while receiver startup was in progress.");
            }

            stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(
                () => !(bool)startInFlightField.GetValue(window)!,
                TimeSpan.FromSeconds(1.5),
                "Stop did not cancel receivers waiting in the startup coordinator.");
            var launchesAfterCancellation = launchTimes.Count;
            Pump(window, 400);
            if (launchTimes.Count != launchesAfterCancellation ||
                testStreamIds.Any(ffmpegSessions.Contains) ||
                testStreamIds.Any(monitoringRequests.Contains) ||
                testStreamIds.Any(restartAfter.Contains) ||
                testStreamIds.Any(restartAttempts.Contains) ||
                testStreamIds.Any(pendingRestarts.Contains) ||
                startupSlots.CurrentCount != 2 ||
                !footer.Text.StartsWith("Playback start cancelled", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stop retained an owner, monitoring intent, retry, pending restart, launch, or startup slot.");
            }

            Console.WriteLine("playback_startup_slots_smoke_ok limit=2 streams=4 admission=soft owners=retained terminal=retrying mixed=preserved stop=clean");
        }
        finally
        {
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(3);
            while ((bool)startInFlightField.GetValue(window)! && DateTime.UtcNow < cleanupDeadline)
            {
                foreach (var session in createdSessions.Values)
                {
                    Field(session, "_state").SetValue(session, "error");
                }
                Pump(window, 50);
            }

            StopWindowTimers(window);
            SynchronizationContext.SetSynchronizationContext(originalSynchronizationContext);
            starterOverrideField.SetValue(window, originalStarterOverride);
            nextLaunchAtField.SetValue(window, originalNextLaunchAt);
            operationGenerationField.SetValue(window, originalOperationGeneration);
            startInFlightField.SetValue(window, originalStartInFlight);
            startButton.IsEnabled = originalStartButtonEnabled;
            stopButton.IsEnabled = originalStopButtonEnabled;
            footer.Text = originalFooter;
            foreach (var stream in activeStreams)
            {
                var state = originalStreamStates[stream.Id];
                stream.Enabled = state.Enabled;
                stream.InputUrl = state.InputUrl;
                stream.InputProtocol = state.InputProtocol;
                stream.AnalysisEnabled = state.AnalysisEnabled;
                stream.AudioMonitorEnabled = state.AudioMonitorEnabled;
            }

            foreach (var streamId in testStreamIds)
            {
                monitoringRequests.Remove(streamId);
                pendingRestarts.Remove(streamId);
                if (originalMonitoringRequests.Contains(streamId))
                {
                    monitoringRequests.Add(streamId);
                }
                if (originalPendingRestarts.Contains(streamId))
                {
                    pendingRestarts.Add(streamId);
                }
            }

            foreach (var dictionary in touchedDictionaries)
            {
                foreach (var streamId in testStreamIds)
                {
                    var state = dictionaryStates[dictionary][streamId];
                    if (state.Exists)
                    {
                        dictionary[streamId] = state.Value;
                    }
                    else
                    {
                        dictionary.Remove(streamId);
                    }
                }
            }

            foreach (var session in allCreatedSessions.Distinct())
            {
                DisposeReflective(session);
            }
        }
    }

    private static void AssertPlaybackConcurrencyWiring(MainWindow window)
    {
        var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
        var activeLayout = config.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, config.ActiveLayoutId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Concurrency smoke requires an active layout.");
        var activeStreamIds = activeLayout.Tiles
            .OrderBy(tile => tile.Index)
            .Select(tile => tile.StreamId)
            .Where(streamId => !string.IsNullOrWhiteSpace(streamId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeStreams = activeStreamIds
            .Select(streamId => config.Streams.FirstOrDefault(stream =>
                string.Equals(stream.Id, streamId, StringComparison.OrdinalIgnoreCase)))
            .Where(stream => stream is not null)
            .Cast<StreamConfig>()
            .ToArray();
        if (activeStreams.Length < 2)
        {
            throw new InvalidOperationException("Concurrency smoke requires two streams assigned to the active layout.");
        }

        var streamA = activeStreams[0];
        var streamB = activeStreams[1];
        var originalStreamStates = activeStreams.ToDictionary(
            stream => stream.Id,
            stream => new PlaybackStreamFixtureState(
                stream.Enabled,
                stream.InputUrl,
                stream.InputProtocol,
                stream.AnalysisEnabled,
                stream.AudioMonitorEnabled),
            StringComparer.OrdinalIgnoreCase);
        var startButton = (Button)Field(window, "StartPlaybackButton").GetValue(window)!;
        var stopButton = (Button)Field(window, "StopPlaybackButton").GetValue(window)!;
        var footer = (TextBlock)Field(window, "FooterText").GetValue(window)!;
        var originalStartButtonEnabled = startButton.IsEnabled;
        var originalStopButtonEnabled = stopButton.IsEnabled;
        var originalFooterText = footer.Text;
        var startInFlightField = Field(window, "_playbackStartInFlight");
        var originalStartInFlight = (bool)startInFlightField.GetValue(window)!;
        var lastSlowRefreshField = Field(window, "_lastSlowPlaybackRefreshAt");
        var originalLastSlowRefreshAt = lastSlowRefreshField.GetValue(window);
        var operationGenerationField = Field(window, "_playbackOperationGeneration");
        var originalOperationGeneration = operationGenerationField.GetValue(window);
        var starterOverrideField = Field(window, "_playbackStarterOverride");
        var originalStarterOverride = starterOverrideField.GetValue(window);
        var ffmpegSessions = (IDictionary)Field(window, "_ffmpegPlaybackSessions").GetValue(window)!;
        var playbackSnapshots = (IDictionary)Field(window, "_playbackSnapshots").GetValue(window)!;
        var lastProgressAt = (IDictionary)Field(window, "_lastPlaybackProgressAt").GetValue(window)!;
        var lastPresentedAt = (IDictionary)Field(window, "_lastVideoPresentedAt").GetValue(window)!;
        var lastDisplayedPictures = (IDictionary)Field(window, "_lastDisplayedPictures").GetValue(window)!;
        var restartAfter = (IDictionary)Field(window, "_playbackRestartAfter").GetValue(window)!;
        var restartAttempts = (IDictionary)Field(window, "_playbackRestartAttempts").GetValue(window)!;
        var pendingRestarts = (ISet<string>)Field(window, "_pendingPlaybackRestarts").GetValue(window)!;
        var monitoringRequests = (ISet<string>)Field(window, "_monitoringRequestedStreamIds").GetValue(window)!;
        var touchedDictionaries = new[]
        {
            ffmpegSessions,
            playbackSnapshots,
            lastProgressAt,
            lastPresentedAt,
            lastDisplayedPictures,
            restartAfter,
            restartAttempts
        };
        var dictionaryStates = touchedDictionaries.ToDictionary(
            dictionary => dictionary,
            dictionary => new[] { streamA.Id, streamB.Id }.ToDictionary(
                streamId => streamId,
                streamId => new DictionaryEntryFixtureState(dictionary.Contains(streamId), dictionary[streamId]),
                StringComparer.OrdinalIgnoreCase));
        var streamAPendingBefore = pendingRestarts.Contains(streamA.Id);
        var streamBPendingBefore = pendingRestarts.Contains(streamB.Id);
        var streamAMonitoringBefore = monitoringRequests.Contains(streamA.Id);
        var streamBMonitoringBefore = monitoringRequests.Contains(streamB.Id);
        var sessionType = DesktopType("FfmpegPlaybackSession");
        object? sessionA = null;
        object? sessionB = null;
        object? replacementSessionB = null;
        object? cancelledRestartSessionB = null;

        try
        {
            if (!originalStartButtonEnabled || originalStartInFlight)
            {
                throw new InvalidOperationException("Start button must be idle and enabled before the concurrency smoke.");
            }

            foreach (var stream in activeStreams)
            {
                stream.Enabled = false;
            }

            ConfigureOfflinePlaybackStream(streamA, 19101);
            ConfigureOfflinePlaybackStream(streamB, 19102);
            sessionA = CreateFfmpegSession(sessionType, "ffmpeg", streamA.InputUrl, streamA.InputProtocol);
            sessionB = CreateFfmpegSession(sessionType, "ffmpeg", streamB.InputUrl, streamB.InputProtocol);
            Field(sessionA, "_state").SetValue(sessionA, "playing");
            Field(sessionB, "_state").SetValue(sessionB, "playing");
            var playingSnapshotA = CreatePlaybackSnapshot("playing", displayedPictures: 12);
            var playingSnapshotB = CreatePlaybackSnapshot("playing", displayedPictures: 12);
            ffmpegSessions[streamA.Id] = sessionA;
            ffmpegSessions[streamB.Id] = sessionB;
            playbackSnapshots[streamA.Id] = playingSnapshotA;
            playbackSnapshots[streamB.Id] = playingSnapshotB;
            lastProgressAt[streamA.Id] = DateTimeOffset.UtcNow;
            lastProgressAt[streamB.Id] = DateTimeOffset.UtcNow;

            var generationBeforeIndividualStop = operationGenerationField.GetValue(window);
            Method(typeof(MainWindow), "StopStreamPlayback", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, ["smoke-unrelated-stream", true]);
            if (!Equals(operationGenerationField.GetValue(window), generationBeforeIndividualStop) ||
                !ReferenceEquals(ffmpegSessions[streamA.Id], sessionA) ||
                !ReferenceEquals(ffmpegSessions[streamB.Id], sessionB))
            {
                throw new InvalidOperationException("Stopping one stream cancelled or replaced unrelated healthy receiver operations.");
            }

            startButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StopWindowTimers(window);
            if ((bool)startInFlightField.GetValue(window)! ||
                !startButton.IsEnabled ||
                !ReferenceEquals(ffmpegSessions[streamA.Id], sessionA) ||
                !ReferenceEquals(ffmpegSessions[streamB.Id], sessionB))
            {
                throw new InvalidOperationException("Repeated Start replaced a healthy FFmpeg session or did not restore its in-flight state.");
            }

            const string expectedStartSummary = "Starting playback complete: 0 started, 2 skipped, 0 failed.";
            if (!footer.Text.StartsWith(expectedStartSummary, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Repeated Start did not complete through the button path: footer={footer.Text}");
            }

            var shouldStartPlayback = Method(typeof(MainWindow), "ShouldStartPlayback", BindingFlags.Instance | BindingFlags.NonPublic);
            lastProgressAt[streamA.Id] = DateTimeOffset.UtcNow.AddSeconds(-10);
            if (!(bool)shouldStartPlayback.Invoke(window, [streamA.Id, true])!)
            {
                throw new InvalidOperationException("Manual Start did not select an input-stalled playing receiver for retry.");
            }

            lastProgressAt[streamA.Id] = DateTimeOffset.UtcNow;
            if ((bool)shouldStartPlayback.Invoke(window, [streamA.Id, true])!)
            {
                throw new InvalidOperationException("Manual Start selected a healthy playing receiver for replacement.");
            }

            var restartAfterCount = restartAfter.Count;
            var restartAttemptCount = restartAttempts.Count;
            var pendingRestartCount = pendingRestarts.Count;
            lastPresentedAt[streamA.Id] = DateTimeOffset.UtcNow.AddSeconds(-10);
            lastDisplayedPictures[streamA.Id] = 12;
            if ((bool)Method(typeof(MainWindow), "IsPlaybackVideoFreshForSurface", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, [streamA, playingSnapshotA])!)
            {
                throw new InvalidOperationException("Concurrency smoke did not create a render-stale playing surface.");
            }

            Method(typeof(MainWindow), "SchedulePlaybackRestartIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [streamA.Id, playingSnapshotA]);
            if (restartAfter.Count != restartAfterCount ||
                restartAttempts.Count != restartAttemptCount ||
                pendingRestarts.Count != pendingRestartCount ||
                restartAfter.Contains(streamA.Id) ||
                restartAttempts.Contains(streamA.Id) ||
                pendingRestarts.Contains(streamA.Id) ||
                !ReferenceEquals(ffmpegSessions[streamA.Id], sessionA))
            {
                throw new InvalidOperationException("Render staleness incorrectly scheduled a receiver restart while input progress was fresh.");
            }

            var errorSnapshot = CreatePlaybackSnapshot("error", displayedPictures: 12);
            playbackSnapshots[streamB.Id] = errorSnapshot;
            restartAfter[streamB.Id] = DateTimeOffset.UtcNow.AddSeconds(-1);
            Method(typeof(MainWindow), "SchedulePlaybackRestartIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [streamB.Id, errorSnapshot]);
            if (!pendingRestarts.Contains(streamB.Id) || !ReferenceEquals(ffmpegSessions[streamB.Id], sessionB))
            {
                throw new InvalidOperationException("Receiver restart was not queued against the expected session owner.");
            }

            replacementSessionB = CreateFfmpegSession(sessionType, "ffmpeg", streamB.InputUrl, streamB.InputProtocol);
            Field(replacementSessionB, "_state").SetValue(replacementSessionB, "playing");
            ffmpegSessions[streamB.Id] = replacementSessionB;
            Pump(window, 100);
            StopWindowTimers(window);
            if (!ReferenceEquals(ffmpegSessions[streamB.Id], replacementSessionB) || pendingRestarts.Contains(streamB.Id))
            {
                throw new InvalidOperationException("A queued restart stopped a replacement session owner or did not clear its pending latch.");
            }

            playbackSnapshots[streamB.Id] = errorSnapshot;
            ffmpegSessions.Remove(streamB.Id);
            restartAfter[streamB.Id] = DateTimeOffset.UtcNow.AddSeconds(-1);
            Func<StreamConfig, System.Threading.Tasks.Task> stalledRestartStarter = stream =>
            {
                cancelledRestartSessionB = CreateFfmpegSession(sessionType, "ffmpeg", stream.InputUrl, stream.InputProtocol);
                Field(cancelledRestartSessionB, "_state").SetValue(cancelledRestartSessionB, "opening");
                ffmpegSessions[stream.Id] = cancelledRestartSessionB;
                playbackSnapshots[stream.Id] = CreatePlaybackSnapshot("opening", displayedPictures: 0);
                return System.Threading.Tasks.Task.CompletedTask;
            };
            starterOverrideField.SetValue(window, stalledRestartStarter);
            lastSlowRefreshField.SetValue(window, DateTimeOffset.MinValue);
            Method(typeof(MainWindow), "RefreshPlaybackMetadata", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, null);
            if (!pendingRestarts.Contains(streamB.Id))
            {
                throw new InvalidOperationException("The slow refresh did not discover and queue an ownerless failed receiver for backoff retry.");
            }

            var restartStartDeadline = DateTime.UtcNow.AddSeconds(2);
            while (!ffmpegSessions.Contains(streamB.Id) && DateTime.UtcNow < restartStartDeadline)
            {
                Pump(window, 50);
            }
            if (!ffmpegSessions.Contains(streamB.Id))
            {
                throw new InvalidOperationException("Ownerless retry did not enter its startup wait before the cancellation check.");
            }

            Method(typeof(MainWindow), "StopAllPlayback", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, null);
            var restartCancelDeadline = DateTime.UtcNow.AddSeconds(2);
            while (pendingRestarts.Contains(streamB.Id) && DateTime.UtcNow < restartCancelDeadline)
            {
                Pump(window, 50);
            }
            StopWindowTimers(window);
            if (ffmpegSessions.Contains(streamB.Id) ||
                pendingRestarts.Contains(streamB.Id) ||
                restartAfter.Contains(streamB.Id) ||
                restartAttempts.Contains(streamB.Id))
            {
                throw new InvalidOperationException("Explicit Stop did not cancel an ownerless retry already waiting for startup readiness.");
            }
            starterOverrideField.SetValue(window, originalStarterOverride);

            monitoringRequests.Add(streamB.Id);
            playbackSnapshots[streamB.Id] = errorSnapshot;
            restartAfter[streamB.Id] = DateTimeOffset.UtcNow.AddSeconds(-1);
            lastSlowRefreshField.SetValue(window, DateTimeOffset.MinValue);
            Method(typeof(MainWindow), "RefreshPlaybackMetadata", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, null);
            if (!pendingRestarts.Contains(streamB.Id))
            {
                throw new InvalidOperationException("Ownerless retry was not queued again after the explicit-stop cancellation check.");
            }

            ffmpegSessions[streamB.Id] = replacementSessionB;
            playbackSnapshots[streamB.Id] = playingSnapshotB;
            Pump(window, 100);
            StopWindowTimers(window);
            if (!ReferenceEquals(ffmpegSessions[streamB.Id], replacementSessionB) || pendingRestarts.Contains(streamB.Id))
            {
                throw new InvalidOperationException("An ownerless retry request replaced a receiver that appeared before its Dispatcher turn.");
            }

            Console.WriteLine("playback_concurrency_wiring_smoke_ok streams=2");
        }
        finally
        {
            StopWindowTimers(window);
            startInFlightField.SetValue(window, originalStartInFlight);
            lastSlowRefreshField.SetValue(window, originalLastSlowRefreshAt);
            operationGenerationField.SetValue(window, originalOperationGeneration);
            starterOverrideField.SetValue(window, originalStarterOverride);
            startButton.IsEnabled = originalStartButtonEnabled;
            stopButton.IsEnabled = originalStopButtonEnabled;
            footer.Text = originalFooterText;
            foreach (var stream in activeStreams)
            {
                var state = originalStreamStates[stream.Id];
                stream.Enabled = state.Enabled;
                stream.InputUrl = state.InputUrl;
                stream.InputProtocol = state.InputProtocol;
                stream.AnalysisEnabled = state.AnalysisEnabled;
                stream.AudioMonitorEnabled = state.AudioMonitorEnabled;
            }

            foreach (var dictionary in touchedDictionaries)
            {
                foreach (var streamId in new[] { streamA.Id, streamB.Id })
                {
                    var state = dictionaryStates[dictionary][streamId];
                    if (state.Exists)
                    {
                        dictionary[streamId] = state.Value;
                    }
                    else
                    {
                        dictionary.Remove(streamId);
                    }
                }
            }

            if (streamAPendingBefore)
            {
                pendingRestarts.Add(streamA.Id);
            }
            else
            {
                pendingRestarts.Remove(streamA.Id);
            }

            if (streamBPendingBefore)
            {
                pendingRestarts.Add(streamB.Id);
            }
            else
            {
                pendingRestarts.Remove(streamB.Id);
            }

            if (streamAMonitoringBefore)
            {
                monitoringRequests.Add(streamA.Id);
            }
            else
            {
                monitoringRequests.Remove(streamA.Id);
            }

            if (streamBMonitoringBefore)
            {
                monitoringRequests.Add(streamB.Id);
            }
            else
            {
                monitoringRequests.Remove(streamB.Id);
            }

            DisposeReflective(sessionA);
            DisposeReflective(sessionB);
            DisposeReflective(replacementSessionB);
            DisposeReflective(cancelledRestartSessionB);
        }
    }

    private static void ConfigureOfflinePlaybackStream(StreamConfig stream, int port)
    {
        stream.Enabled = true;
        stream.InputUrl = $"srt://127.0.0.1:{port}";
        stream.InputProtocol = "srt";
        stream.AnalysisEnabled = false;
        stream.AudioMonitorEnabled = false;
    }

    private static void AssertGpuRenderingUsesLibVlcPlayback(MainWindow window)
    {
        var previousGpuEnv = Environment.GetEnvironmentVariable("IPMV_GPU_RENDERING");
        var previousForceEnv = Environment.GetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK");
        try
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", "1");
            Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", null);
            var shouldRouteThroughFfmpeg = Method(
                window.GetType(),
                "ShouldRouteThroughFfmpegPlayback",
                BindingFlags.Static | BindingFlags.NonPublic);
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
            var hlsAnalysisStream = new StreamConfig
            {
                Id = "smoke-gpu-hls",
                Name = "Smoke GPU HLS",
                Enabled = true,
                InputUrl = "https://example.test/live.m3u8",
                InputProtocol = "hls",
                AnalysisEnabled = true,
                AudioMonitorEnabled = true
            };
            var httpAnalysisStream = new StreamConfig
            {
                Id = "smoke-gpu-http",
                Name = "Smoke GPU HTTP",
                Enabled = true,
                InputUrl = "http://example.test/live.mp4",
                InputProtocol = "http",
                AnalysisEnabled = true,
                AudioMonitorEnabled = true
            };
            var rtmpAnalysisStream = new StreamConfig
            {
                Id = "smoke-gpu-rtmp",
                Name = "Smoke GPU RTMP",
                Enabled = true,
                InputUrl = "rtmp://example.test/live/stream",
                InputProtocol = "rtmp",
                AnalysisEnabled = true,
                AudioMonitorEnabled = true
            };
            var shouldUseFfmpeg = (bool)shouldRouteThroughFfmpeg.Invoke(null, [rtspAnalysisStream])!;
            if (shouldUseFfmpeg)
            {
                throw new InvalidOperationException("GPU rendering mode must keep analysis-enabled RTSP streams on embedded LibVLC GPU playback instead of FFmpeg bitmap playback.");
            }

            foreach (var nonSrtStream in new[] { hlsAnalysisStream, httpAnalysisStream, rtmpAnalysisStream })
            {
                var shouldUseNonSrtFfmpeg = (bool)shouldRouteThroughFfmpeg.Invoke(null, [nonSrtStream])!;
                if (shouldUseNonSrtFfmpeg)
                {
                    throw new InvalidOperationException($"GPU rendering mode must keep non-SRT {nonSrtStream.InputProtocol.ToUpperInvariant()} streams on embedded LibVLC GPU playback by default.");
                }
            }

            var shouldAttachAudioMonitor = (bool)Method(window.GetType(), "ShouldAttachAudioMonitor", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [rtspAnalysisStream])!;
            if (!shouldAttachAudioMonitor)
            {
                throw new InvalidOperationException("GPU rendering mode must still attach audio callbacks so VU meters stay visible.");
            }

            var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
            var previousGlobalMute = config.GlobalMute;
            object? audioMonitor = null;
            try
            {
                var disablePlaybackAudio = Method(window.GetType(), "ShouldDisableLibVlcPlaybackAudio", BindingFlags.Instance | BindingFlags.NonPublic);
                var mutePlaybackAudio = Method(window.GetType(), "ShouldMuteLibVlcPlaybackAudio", BindingFlags.Static | BindingFlags.NonPublic);
                audioMonitor = Activator.CreateInstance(DesktopType("AudioLevelMonitor"));
                config.GlobalMute = true;
                if ((bool)disablePlaybackAudio.Invoke(window, [rtspAnalysisStream, null])! != true)
                {
                    throw new InvalidOperationException("GPU playback without audio callbacks must pass :no-audio to LibVLC.");
                }

                config.GlobalMute = false;
                if ((bool)disablePlaybackAudio.Invoke(window, [rtspAnalysisStream, audioMonitor])!)
                {
                    throw new InvalidOperationException("GPU playback with audio callbacks must keep decode available for VU meters.");
                }

                if ((bool)mutePlaybackAudio.Invoke(null, [rtspAnalysisStream])! != true)
                {
                    throw new InvalidOperationException("Monitoring playback must keep LibVLC speaker output muted.");
                }

                rtspAnalysisStream.AudioMonitorEnabled = false;
                if ((bool)disablePlaybackAudio.Invoke(window, [rtspAnalysisStream, null])! != true)
                {
                    throw new InvalidOperationException("Streams with audio monitoring disabled must pass :no-audio to LibVLC.");
                }
            }
            finally
            {
                DisposeReflective(audioMonitor);
                rtspAnalysisStream.AudioMonitorEnabled = true;
                config.GlobalMute = previousGlobalMute;
            }

            var srtUsesFfmpeg = (bool)shouldRouteThroughFfmpeg.Invoke(null, [srtStream])!;
            if (!srtUsesFfmpeg)
            {
                throw new InvalidOperationException("GPU rendering mode must keep SRT on FFmpeg/D3D11VA tile playback because LibVLC can hang on no-signal SRT endpoints.");
            }

            Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", "1");
            var forcedSrtUsesFfmpeg = (bool)shouldRouteThroughFfmpeg.Invoke(null, [srtStream])!;
            if (!forcedSrtUsesFfmpeg)
            {
                throw new InvalidOperationException("IPMV_FORCE_FFMPEG_PLAYBACK must still force SRT through FFmpeg for diagnostics.");
            }

            Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", null);
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", null);
            var standardSrtUsesFfmpeg = (bool)shouldRouteThroughFfmpeg.Invoke(null, [srtStream])!;
            if (!standardSrtUsesFfmpeg)
            {
                throw new InvalidOperationException("Standard rendering mode should still use the FFmpeg fallback for SRT streams.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPMV_GPU_RENDERING", previousGpuEnv);
            Environment.SetEnvironmentVariable("IPMV_FORCE_FFMPEG_PLAYBACK", previousForceEnv);
        }
    }

    private static void AssertNoDeprecatedCache(IEnumerable<string> options, string label)
    {
        var list = options.ToList();
        if (list.Any(option => option.Contains("caching=300", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{label} options still contain obsolete 300ms caching.");
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
        var panel = Method(typeof(MainWindow), "CreateAudioMeterSidePanel", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, [left, HorizontalAlignment.Left, views]);
        _ = Method(typeof(MainWindow), "CreateAudioMeterSidePanel", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, [right, HorizontalAlignment.Right, views]);
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
        var track = (Border)meterViewType.GetProperty("Track")!.GetValue(firstView)!;
        var fill = (Border)meterViewType.GetProperty("Fill")!.GetValue(firstView)!;
        var fillClip = (RectangleGeometry)meterViewType.GetProperty("FillClip")!.GetValue(firstView)!;
        var peakMarker = (Border)meterViewType.GetProperty("PeakMarker")!.GetValue(firstView)!;
        var peakOffset = (TranslateTransform)meterViewType.GetProperty("PeakOffset")!.GetValue(firstView)!;
        var audioMeterHeight = Convert.ToDouble(typeof(MainWindow).GetField("AudioMeterHeight", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue(), CultureInfo.InvariantCulture);
        var audioMeterBarWidth = Convert.ToDouble(typeof(MainWindow).GetField("AudioMeterBarWidth", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue(), CultureInfo.InvariantCulture);
        if (Math.Abs(fill.Height - audioMeterHeight) > 0.001 ||
            fill.Clip is not RectangleGeometry ||
            peakMarker.RenderTransform is not TranslateTransform)
        {
            throw new InvalidOperationException("Audio meters must keep stable dimensions with clip/transform updates.");
        }
        if (track.Child is not Canvas trackCanvas ||
            trackCanvas.Children.OfType<Border>().Count(child =>
                Math.Abs(child.Width - audioMeterBarWidth) < 0.001 &&
                child.Opacity >= 0.15 &&
                child.Height > 1) < 3)
        {
            throw new InvalidOperationException("Audio meters must render visible green/yellow/red dB zones behind the level fill.");
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
        if (Math.Abs(displayedLevel - 0.80) > 0.001 ||
            displayedPeak >= 0.85 ||
            displayedPeak < 0.35 ||
            Math.Abs(fill.Height - audioMeterHeight) > 0.001)
        {
            throw new InvalidOperationException($"Audio meter immediate attack / time-based release was unexpected: level={displayedLevel}, peak={displayedPeak}.");
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

    private static void AssertPlaybackSurfaceClearsStaleVideo(MainWindow window)
    {
        var stream = new StreamConfig
        {
            Id = "smoke-clear-stale-video",
            Name = "Smoke Clear Stale Video",
            Enabled = true,
            InputUrl = "rtsp://example.invalid/live",
            AlertProfileId = "default"
        };
        var lastVideoPresentedAt = (IDictionary)Field(window, "_lastVideoPresentedAt").GetValue(window)!;
        var shouldClearSurface = Method(typeof(MainWindow), "ShouldClearPlaybackSurface", BindingFlags.Instance | BindingFlags.NonPublic);
        var isFreshForSurface = Method(typeof(MainWindow), "IsPlaybackVideoFreshForSurface", BindingFlags.Instance | BindingFlags.NonPublic);
        var playingSnapshot = CreatePlaybackSnapshot("playing", displayedPictures: 12);

        try
        {
            lastVideoPresentedAt[stream.Id] = DateTimeOffset.UtcNow.AddSeconds(-10);
            if ((bool)isFreshForSurface.Invoke(window, [stream, playingSnapshot])! ||
                (bool)shouldClearSurface.Invoke(window, [stream, playingSnapshot])! != true)
            {
                throw new InvalidOperationException("Stale presented video must be treated as a clear/no-signal surface.");
            }

            lastVideoPresentedAt[stream.Id] = DateTimeOffset.UtcNow;
            if (!(bool)isFreshForSurface.Invoke(window, [stream, playingSnapshot])! ||
                (bool)shouldClearSurface.Invoke(window, [stream, playingSnapshot])!)
            {
                throw new InvalidOperationException("Fresh presented video should remain visible.");
            }

            var endedSnapshot = CreatePlaybackSnapshot("ended", displayedPictures: 12);
            if ((bool)shouldClearSurface.Invoke(window, [stream, endedSnapshot])! != true)
            {
                throw new InvalidOperationException("Ended playback must clear the displayed video surface.");
            }
        }
        finally
        {
            lastVideoPresentedAt.Remove(stream.Id);
        }
    }

    private static void AssertTileVideoSurfaceHasDedicatedFrameRows(MainWindow window)
    {
        var stream = new StreamConfig
        {
            Id = "smoke-tile-frame-clip",
            Name = "Smoke Tile Frame Clip",
            Enabled = true,
            InputUrl = "rtsp://example.invalid/live"
        };
        var createTile = Method(typeof(MainWindow), "CreateTile", BindingFlags.Instance | BindingFlags.NonPublic);
        var removeRenderedTileBindings = Method(typeof(MainWindow), "RemoveRenderedTileBindings", BindingFlags.Instance | BindingFlags.NonPublic);
        var surfaceHosts = (IDictionary)Field(window, "_tileSurfaceHosts").GetValue(window)!;
        var statusFrames = (IDictionary)Field(window, "_tileStatusFrames").GetValue(window)!;
        const int tileIndex = 987;

        try
        {
            _ = (Border)createTile.Invoke(window, [stream, tileIndex])!;
            var surfaceHost = (Grid)surfaceHosts[stream.Id]!;
            if (!surfaceHost.ClipToBounds ||
                Grid.GetRow(surfaceHost) != 1 ||
                Grid.GetColumn(surfaceHost) != 1)
            {
                throw new InvalidOperationException("Video surface host must be clipped and placed inside the dedicated inner video row.");
            }

            var videoFrame = (Grid)surfaceHost.Parent;
            var frameThickness = Convert.ToDouble(typeof(MainWindow).GetField("TileFrameThickness", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue(), CultureInfo.InvariantCulture);
            if (videoFrame.RowDefinitions.Count != 3 ||
                videoFrame.RowDefinitions[0].Height.Value != frameThickness ||
                videoFrame.RowDefinitions[2].Height.Value != frameThickness)
            {
                throw new InvalidOperationException("Video frame must reserve top and bottom rows outside the HWND video surface.");
            }

            var frames = (IList)statusFrames[stream.Id]!;
            var topFrame = (Border)frames[0]!;
            var leftFrame = (Border)frames[1]!;
            var rightFrame = (Border)frames[2]!;
            var bottomFrame = (Border)frames[3]!;
            if (Grid.GetRow(topFrame) != 0 ||
                Grid.GetRow(bottomFrame) != 2 ||
                Grid.GetRowSpan(leftFrame) != 3 ||
                Grid.GetRowSpan(rightFrame) != 3)
            {
                throw new InvalidOperationException("Tile frame pieces must not overlap the video surface row.");
            }
        }
        finally
        {
            removeRenderedTileBindings.Invoke(window, [tileIndex]);
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
            var targetFps = Convert.ToDouble(sessionType.GetField("TargetFps", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue(), CultureInfo.InvariantCulture);
            if (targetFps < 24)
            {
                throw new InvalidOperationException($"FFmpeg tile playback target FPS is too low for live monitoring: {targetFps:0.###}.");
            }

            var outputWidth = Convert.ToInt32(sessionType.GetProperty("OutputWidth")!.GetValue(session), CultureInfo.InvariantCulture);
            var outputHeight = Convert.ToInt32(sessionType.GetProperty("OutputHeight")!.GetValue(session), CultureInfo.InvariantCulture);
            var bitmap = sessionType.GetProperty("Bitmap")!.GetValue(session)
                ?? throw new InvalidOperationException("Fallback FFmpeg bitmap was unavailable.");
            var pixelWidth = Convert.ToInt32(bitmap.GetType().GetProperty("PixelWidth")!.GetValue(bitmap), CultureInfo.InvariantCulture);
            var pixelHeight = Convert.ToInt32(bitmap.GetType().GetProperty("PixelHeight")!.GetValue(bitmap), CultureInfo.InvariantCulture);
            var fallbackFilter = (string)Method(sessionType, "BuildFallbackVideoFilter", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [])!;
            if (outputWidth != 426 || outputHeight != 240 || pixelWidth != 426 || pixelHeight != 240 ||
                !fallbackFilter.Contains("scale=426:240:flags=fast_bilinear", StringComparison.OrdinalIgnoreCase) ||
                fallbackFilter.Contains("force_original_aspect_ratio", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Fallback FFmpeg display contract changed: output={outputWidth}x{outputHeight}, bitmap={pixelWidth}x{pixelHeight}, filter={fallbackFilter}");
            }

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

    private static void AssertImmediateBlackFrameRaisesAlert(MainWindow window)
    {
        var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
        var originalActiveLayoutId = config.ActiveLayoutId;
        var originalServerSyncEnabled = config.ServerSyncEnabled;
        var stream = new StreamConfig
        {
            Id = "smoke-immediate-black",
            Name = "Smoke Immediate Black",
            Enabled = true,
            InputUrl = "file:///not-started",
            InputProtocol = "file",
            AnalysisEnabled = true,
            AudioMonitorEnabled = false,
            AlertProfileId = config.AlertProfiles.FirstOrDefault()?.Id ?? "default"
        };
        var layout = new LayoutConfig
        {
            Id = "smoke-immediate-black-layout",
            Name = "Smoke immediate black layout",
            Kind = "2x2",
            Tiles =
            [
                new LayoutTileConfig { Index = 0, StreamId = stream.Id, Row = 0, Column = 0 },
                new LayoutTileConfig { Index = 1, StreamId = "", Row = 0, Column = 1 },
                new LayoutTileConfig { Index = 2, StreamId = "", Row = 1, Column = 0 },
                new LayoutTileConfig { Index = 3, StreamId = "", Row = 1, Column = 1 }
            ]
        };
        var sessionType = DesktopType("FfmpegPlaybackSession");
        object? session = null;

        try
        {
            config.ServerSyncEnabled = false;
            config.Streams.Add(stream);
            config.Layouts.Add(layout);
            config.ActiveLayoutId = layout.Id;

            var queueImmediate = MethodWithParameterTypes(
                typeof(MainWindow),
                "QueueImmediateVideoAlertEvaluation",
                [typeof(string), typeof(bool)],
                BindingFlags.Instance | BindingFlags.NonPublic);
            session = CreateFfmpegSession(
                sessionType,
                "ffmpeg",
                stream.InputUrl,
                stream.InputProtocol,
                () => queueImmediate.Invoke(window, [stream.Id, true]));

            Field(session, "_state").SetValue(session, "playing");
            var ffmpegSessions = (IDictionary)Field(window, "_ffmpegPlaybackSessions").GetValue(window)!;
            var videoMonitors = (IDictionary)Field(window, "_videoMonitors").GetValue(window)!;
            var playbackSnapshots = (IDictionary)Field(window, "_playbackSnapshots").GetValue(window)!;
            ffmpegSessions[stream.Id] = session;
            videoMonitors[stream.Id] = session;

            var frameBytes = (int)(sessionType.GetField("FrameBytes", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue() ?? throw new InvalidOperationException("FrameBytes constant was unavailable."));
            var blackFrame = new byte[frameBytes];
            var whiteFrame = new byte[frameBytes];
            Array.Fill(whiteFrame, (byte)255);
            var updateSnapshot = Method(sessionType, "UpdateSnapshot");

            updateSnapshot.Invoke(session, [blackFrame]);
            var pendingBlackFrames = (IDictionary)Field(window, "_pendingImmediateBlackFrameDetections").GetValue(window)!;
            if (!pendingBlackFrames.Contains(stream.Id))
            {
                throw new InvalidOperationException("Black-frame callback did not latch the immediate video.black evidence.");
            }

            playbackSnapshots[stream.Id] = Method(sessionType, "CurrentPlaybackSnapshot").Invoke(session, []);
            updateSnapshot.Invoke(session, [whiteFrame]);
            playbackSnapshots[stream.Id] = Method(sessionType, "CurrentPlaybackSnapshot").Invoke(session, []);
            Pump(window, 500);

            var raised = config.AlertHistory.Any(alert =>
                string.Equals(alert.StreamId, stream.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(alert.Type, "video.black", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(alert.Status, "open", StringComparison.OrdinalIgnoreCase));
            if (!raised)
            {
                var activeStreams = ((IEnumerable)MethodWithParameterCount(typeof(MainWindow), "ActiveLayoutStreams", 0)
                    .Invoke(window, [])!).Cast<StreamConfig>().Select(item => item.Id);
                throw new InvalidOperationException($"A single black frame must raise video.black even when the next frame is already non-black. active={string.Join(",", activeStreams)} pending={pendingBlackFrames.Contains(stream.Id)} alerts={config.AlertHistory.Count}");
            }
        }
        finally
        {
            config.ServerSyncEnabled = originalServerSyncEnabled;
            config.ActiveLayoutId = originalActiveLayoutId;
            config.Streams.Remove(stream);
            config.Layouts.Remove(layout);
            foreach (var alert in config.AlertHistory.Where(alert => string.Equals(alert.StreamId, stream.Id, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                config.AlertHistory.Remove(alert);
            }

            ((IDictionary)Field(window, "_ffmpegPlaybackSessions").GetValue(window)!).Remove(stream.Id);
            ((IDictionary)Field(window, "_videoMonitors").GetValue(window)!).Remove(stream.Id);
            ((IDictionary)Field(window, "_playbackSnapshots").GetValue(window)!).Remove(stream.Id);
            ((IDictionary)Field(window, "_issueSince").GetValue(window)!).Remove($"{stream.Id}:video.black");
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
            var updateSnapshot = Method(monitorType, "UpdateSnapshot");
            updateSnapshot.Invoke(monitor, [frame]);
            if (blackCallbacks != 2)
            {
                throw new InvalidOperationException($"Sidecar black frame callback count = {blackCallbacks}, want 2.");
            }

            for (var index = 1; index < 120; index++)
            {
                updateSnapshot.Invoke(monitor, [frame]);
            }
            if (blackCallbacks != 2)
            {
                throw new InvalidOperationException(
                    $"Continuous sidecar black frames retriggered callbacks {blackCallbacks} times; want one rising edge per subscriber.");
            }

            var tryGetSnapshot = Method(monitorType, "TryGetSnapshot");
            var args = new object?[] { null };
            if ((bool)tryGetSnapshot.Invoke(monitor, args)! != true || args[0] is null)
            {
                throw new InvalidOperationException("FFmpeg sidecar monitor did not expose a sampled black snapshot.");
            }

            Array.Fill(frame, (byte)255);
            updateSnapshot.Invoke(monitor, [frame]);
            if (blackCallbacks != 2)
            {
                throw new InvalidOperationException("FFmpeg sidecar monitor fired black callback for a white frame.");
            }

            Array.Clear(frame);
            updateSnapshot.Invoke(monitor, [frame]);
            if (blackCallbacks != 4)
            {
                throw new InvalidOperationException(
                    $"Sidecar black callbacks did not fire once per subscriber after a white-frame reset; callbacks={blackCallbacks}.");
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
            AssertParsedAudioLine(parse, session, "frame:0    pts:60000    pts_time:1.25");
            AssertParsedAudioLine(parse, session, "lavfi.astats.1.Peak_level=-3.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.1.RMS_level=-18.5");
            AssertParsedAudioLine(parse, session, "lavfi.astats.2.Peak_level=-0.5");
            AssertParsedAudioLine(parse, session, "lavfi.astats.2.RMS_level=-24.25");
            AssertParsedAudioLine(parse, session, "lavfi.astats.3.RMS_level=-30.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.4.Peak_level=-4.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.Overall.Peak_level=-2.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.Overall.RMS_level=-20.0");

            var parsedSnapshot = Field(session, "_audioSnapshot").GetValue(session)
                ?? throw new InvalidOperationException("FFmpeg audio stats parser did not assemble a PTS-stamped audio snapshot.");
            AssertNear(
                (double)parsedSnapshot.GetType().GetProperty("MediaTimeSeconds")!.GetValue(parsedSnapshot)!,
                1.25,
                "audio media PTS");

            Field(session, "_lastPresentedVideoPtsSeconds").SetValue(session, 1.30);

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

            AssertParsedAudioLine(parse, session, "frame:1    pts:84000    pts_time:1.75");
            AssertParsedAudioLine(parse, session, "lavfi.astats.1.Peak_level=-0.25");
            AssertParsedAudioLine(parse, session, "lavfi.astats.1.RMS_level=-4.0");
            AssertParsedAudioLine(parse, session, "lavfi.astats.Overall.Peak_level=-0.25");
            AssertParsedAudioLine(parse, session, "lavfi.astats.Overall.RMS_level=-4.0");

            args[0] = null;
            _ = Method(sessionType, "TryGetAudioSnapshot").Invoke(session, args);
            AssertNear(
                (double)args[0]!.GetType().GetProperty("MediaTimeSeconds")!.GetValue(args[0])!,
                1.25,
                "future audio held behind presented video");

            Field(session, "_lastPresentedVideoPtsSeconds").SetValue(session, 1.80);
            args[0] = null;
            _ = Method(sessionType, "TryGetAudioSnapshot").Invoke(session, args);
            AssertNear(
                (double)args[0]!.GetType().GetProperty("MediaTimeSeconds")!.GetValue(args[0])!,
                1.75,
                "audio released with presented video");

            var videoPtsArgs = new object?[] { "42 3.5", null, null };
            if (!(bool)Method(sessionType, "TryParseVideoPtsLine", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, videoPtsArgs)! ||
                (long)videoPtsArgs[1]! != 42 ||
                Math.Abs((double)videoPtsArgs[2]! - 3.5) > 0.001)
            {
                throw new InvalidOperationException("FFmpeg video PTS stats parser did not preserve ordinal/time.");
            }

            var showInfoPtsArgs = new object?[]
            {
                "[Parsed_showinfo_4 @ 000001] n:   7 pts:  11200 pts_time:0.233333 duration:1600",
                null,
                null
            };
            if (!(bool)Method(sessionType, "TryParseShowInfoVideoPtsLine", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, showInfoPtsArgs)! ||
                (long)showInfoPtsArgs[1]! != 7 ||
                Math.Abs((double)showInfoPtsArgs[2]! - 0.233333) > 0.001)
            {
                throw new InvalidOperationException("Fallback FFmpeg showinfo PTS parser did not preserve ordinal/time.");
            }

            Field(session, "_audioMetadataGeneration").SetValue(session, 2);
            var generationParse = MethodWithParameterTypes(
                sessionType,
                "TryParseAudioStatsLineForGeneration",
                [typeof(string), typeof(int)]);
            if ((bool)generationParse.Invoke(session, ["frame:9 pts:999 pts_time:9.0", 1])!)
            {
                throw new InvalidOperationException("A stale FFmpeg audio metadata reader modified the active parser generation.");
            }
            AssertNear(
                (double)Field(session, "_activeAudioPtsSeconds").GetValue(session)!,
                1.75,
                "stale audio metadata generation rejection");
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

    private static void AssertMissingVideoPtsFallbackIsBounded()
    {
        var sessionType = DesktopType("FfmpegPlaybackSession");
        var contextType = sessionType.GetNestedType("FfmpegOutputPipeContext", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FFmpeg output pipe context was not available.");
        using var cts = new CancellationTokenSource();
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [cts.Token, true, true],
            culture: null) ?? throw new InvalidOperationException("Failed to create an FFmpeg output pipe context.");
        try
        {
            var waitForPts = Method(
                sessionType,
                "WaitForVideoPtsAsync",
                BindingFlags.Static | BindingFlags.NonPublic);
            var firstWait = Stopwatch.StartNew();
            var firstResult = AwaitVideoPtsWithDeadline(
                waitForPts,
                context,
                0L,
                "initial missing PTS");
            firstWait.Stop();
            if (!double.IsNaN(firstResult) ||
                firstWait.Elapsed < TimeSpan.FromMilliseconds(40) ||
                firstWait.Elapsed > TimeSpan.FromMilliseconds(500))
            {
                throw new InvalidOperationException(
                    $"Initial missing video PTS did not use the 40-500ms bounded wait: result={firstResult}, elapsed={firstWait.Elapsed.TotalMilliseconds:0.0}ms.");
            }

            var fallbackWait = Stopwatch.StartNew();
            var fallbackResult = AwaitVideoPtsWithDeadline(
                waitForPts,
                context,
                1L,
                "disabled-wait fallback");
            fallbackWait.Stop();
            if (!double.IsNaN(fallbackResult) || fallbackWait.Elapsed > TimeSpan.FromMilliseconds(30))
            {
                throw new InvalidOperationException(
                    $"Missing video PTS fallback blocked subsequent frames for {fallbackWait.Elapsed.TotalMilliseconds:0.0}ms.");
            }

            var storePts = Method(
                sessionType,
                "StoreVideoPts",
                BindingFlags.Static | BindingFlags.NonPublic);
            for (var ordinal = 2L; ordinal < 1002; ordinal++)
            {
                storePts.Invoke(null, [context, ordinal, ordinal / 30.0]);
            }

            var bufferedPts = contextType.GetProperty("VideoPtsByOrdinal")!.GetValue(context)!;
            var bufferedCount = Convert.ToInt32(bufferedPts.GetType().GetProperty("Count")!.GetValue(bufferedPts));
            var wakeSignal = contextType.GetProperty("VideoPtsAvailable")!.GetValue(context)!;
            var wakeCount = Convert.ToInt32(wakeSignal.GetType().GetProperty("CurrentCount")!.GetValue(wakeSignal));
            if (bufferedCount > 129 || wakeCount > 1)
            {
                throw new InvalidOperationException(
                    $"Video PTS fallback buffers were unbounded: entries={bufferedCount}, wakeSignals={wakeCount}.");
            }

            var recoveredResult = AwaitVideoPtsWithDeadline(
                waitForPts,
                context,
                1001L,
                "PTS recovery");
            AssertNear(recoveredResult, 1001 / 30.0, "video PTS fallback recovery");
            var extrapolatedResult = AwaitVideoPtsWithDeadline(
                waitForPts,
                context,
                1002L,
                "bounded PTS extrapolation");
            AssertNear(extrapolatedResult, 1002 / 30.0, "bounded video PTS extrapolation");

            var unsupportedContext = Activator.CreateInstance(
                contextType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [cts.Token, false, false],
                culture: null) ?? throw new InvalidOperationException("Failed to create a no-PTS FFmpeg output pipe context.");
            try
            {
                var unsupportedWait = Stopwatch.StartNew();
                var unsupportedResult = AwaitVideoPtsWithDeadline(
                    waitForPts,
                    unsupportedContext,
                    0L,
                    "unsupported PTS path");
                unsupportedWait.Stop();
                if (!double.IsNaN(unsupportedResult) || unsupportedWait.Elapsed > TimeSpan.FromMilliseconds(30))
                {
                    throw new InvalidOperationException(
                        $"Unsupported FFmpeg PTS fallback blocked video for {unsupportedWait.Elapsed.TotalMilliseconds:0.0}ms.");
                }
            }
            finally
            {
                DisposeReflective(unsupportedContext);
            }
        }
        finally
        {
            cts.Cancel();
            DisposeReflective(context);
        }
    }

    private static double AwaitVideoPtsWithDeadline(
        MethodInfo waitForPts,
        object context,
        long ordinal,
        string phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var task = (System.Threading.Tasks.Task<double>)waitForPts.Invoke(
                null,
                [context, ordinal, timeout.Token])!;
            return task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            throw new InvalidOperationException($"Video PTS contract timed out during {phase}.", ex);
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
        ((IDictionary)Field(window, "_issueSince").GetValue(window)!).Clear();
        var config = (LocalConfig)Field(window, "_config").GetValue(window)!;
        foreach (var alert in config.AlertHistory.Where(alert => string.Equals(alert.StreamId, stream.Id, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            config.AlertHistory.Remove(alert);
        }
        stream.AnalysisEnabled = false;

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
            var lastFramePresentedTicks = Field(session, "_lastFramePresentedTicks");
            ffmpegSessions[stream.Id] = session;
            lastFramePresentedTicks.SetValue(session, DateTime.UtcNow.Ticks);
            playbackSnapshots[stream.Id] = Method(sessionType, "CurrentPlaybackSnapshot").Invoke(session, []);
            Method(typeof(MainWindow), "UpdateTileChrome", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [stream, null]);

            Method(sessionType, "QueueBitmapFrame").Invoke(session, [frame, DateTime.UtcNow.Ticks]);
            Pump(window, 150);
            lastFramePresentedTicks.SetValue(session, DateTime.UtcNow.Ticks);
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
            lastFramePresentedTicks.SetValue(session, DateTime.UtcNow.Ticks);
            playbackSnapshots[stream.Id] = Method(sessionType, "CurrentPlaybackSnapshot").Invoke(session, []);
            Method(typeof(MainWindow), "UpdateTileChrome", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, [stream, null]);
            var chromeKeyAfter = (string?)chromeKeys[stream.Id];
            var statusKeyAfter = (string?)statusKeys[stream.Id];
            if (!string.Equals(chromeKeyBefore, chromeKeyAfter, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Video freshness text changes should not repaint tile chrome: before={chromeKeyBefore}, after={chromeKeyAfter}.");
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

    private static (object? SessionA, object? SessionB) AssertTwoFfmpegSessionsCanRun(string workDir)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("ffmpeg_session_smoke_skipped: ffmpeg not found");
            return (null, null);
        }

        Directory.CreateDirectory(workDir);
        var fileA = Path.Combine(workDir, "stream-a.mp4");
        var fileB = Path.Combine(workDir, "stream-b.mp4");
        RunFfmpeg(ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", fileA);
        RunFfmpeg(ffmpeg, "-y", "-f", "lavfi", "-i", "smptebars=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "3", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", fileB);

        var sessionType = DesktopType("FfmpegPlaybackSession");
        AssertShowInfoVideoPtsFallbackCanRun(sessionType, ffmpeg, fileA);
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

        AssertAudioFollowsPresentedVideoPts(sessionType, sessionA, "ffmpeg_session_a");
        AssertAudioFollowsPresentedVideoPts(sessionType, sessionB, "ffmpeg_session_b");

        AssertTwoSrtLoopbackSessionsCanRun(sessionType, ffmpeg);
        return (sessionA, sessionB);
    }

    private static void AssertShowInfoVideoPtsFallbackCanRun(Type sessionType, string ffmpeg, string inputFile)
    {
        var cache = sessionType.GetField("VideoPtsStatsSupport", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) ?? throw new InvalidOperationException("FFmpeg PTS capability cache was not available.");
        var clearCache = cache.GetType().GetMethod("Clear")!;
        var tryAdd = cache.GetType().GetMethod("TryAdd")!;
        clearCache.Invoke(cache, []);
        _ = tryAdd.Invoke(cache, [ffmpeg, new Lazy<bool>(() => false)]);

        var session = CreateFfmpegSession(sessionType, ffmpeg, inputFile);
        try
        {
            var startElapsed = Stopwatch.StartNew();
            Method(sessionType, "Start").Invoke(session, []);
            startElapsed.Stop();
            if (startElapsed.Elapsed > TimeSpan.FromMilliseconds(100))
            {
                throw new InvalidOperationException(
                    $"FFmpeg capability detection blocked Start for {startElapsed.Elapsed.TotalMilliseconds:0.0}ms.");
            }

            var hasFrame = sessionType.GetProperty("HasPresentedFrame")!;
            var tryAudio = Method(sessionType, "TryGetAudioSnapshot");
            var gotFrame = false;
            var gotAudio = false;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                Pump(null, 100);
                gotFrame = (bool)hasFrame.GetValue(session)!;
                gotAudio = (bool)tryAudio.Invoke(session, [null])!;
                if (gotFrame && gotAudio)
                {
                    break;
                }
            }

            if (!gotFrame || !gotAudio)
            {
                throw new InvalidOperationException(
                    $"Fallback FFmpeg showinfo path did not preserve video/audio monitoring: frame={gotFrame}, audio={gotAudio}.");
            }

            AssertAudioFollowsPresentedVideoPts(sessionType, session, "ffmpeg_showinfo_fallback");
        }
        finally
        {
            DisposeReflective(session);
            clearCache.Invoke(cache, []);
        }
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

            senderA = StartFfmpegProcess(ffmpeg, "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=540:sample_rate=48000", "-t", "12", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", "-f", "mpegts", $"srt://127.0.0.1:{portA}?mode=caller&pkt_size=1316");
            senderB = StartFfmpegProcess(ffmpeg, "-re", "-f", "lavfi", "-i", "smptebars=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=960:sample_rate=48000", "-t", "12", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", "-f", "mpegts", $"srt://127.0.0.1:{portB}?mode=caller&pkt_size=1316");

            var hasFrame = sessionType.GetProperty("HasFrame")!;
            var hasPresentedFrame = sessionType.GetProperty("HasPresentedFrame")!;
            var bitmapFramesPresented = sessionType.GetProperty("BitmapFramesPresented")!;
            var observedPresentedFps = sessionType.GetProperty("ObservedPresentedFps")!;
            var state = sessionType.GetProperty("State")!;
            var currentPlaybackSnapshot = Method(sessionType, "CurrentPlaybackSnapshot");
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

            if (!gotFrameA || !gotFrameB || !gotPresentedFrameA || !gotPresentedFrameB || !gotAudioA || !gotAudioB)
            {
                Console.WriteLine($"srt_loopback_a state={state.GetValue(sessionA)} frame={gotFrameA} presented={gotPresentedFrameA} audio={gotAudioA}");
                Console.WriteLine($"srt_loopback_b state={state.GetValue(sessionB)} frame={gotFrameB} presented={gotPresentedFrameB} audio={gotAudioB}");
                throw new InvalidOperationException("Two SRT loopback playback/analysis sessions did not run concurrently.");
            }

            var presentedBeforeA = (long)bitmapFramesPresented.GetValue(sessionA)!;
            var presentedBeforeB = (long)bitmapFramesPresented.GetValue(sessionB)!;
            var measurementStart = DateTime.UtcNow;
            var measurementDeadline = measurementStart.AddMilliseconds(SmoothFpsMeasurementMs);
            while (DateTime.UtcNow < measurementDeadline)
            {
                Pump(null, 100);
            }

            var elapsedSeconds = Math.Max(0.001, (DateTime.UtcNow - measurementStart).TotalSeconds);
            var measuredPresentedFpsA = ((long)bitmapFramesPresented.GetValue(sessionA)! - presentedBeforeA) / elapsedSeconds;
            var measuredPresentedFpsB = ((long)bitmapFramesPresented.GetValue(sessionB)! - presentedBeforeB) / elapsedSeconds;
            var observedPresentedFpsA = (double)observedPresentedFps.GetValue(sessionA)!;
            var observedPresentedFpsB = (double)observedPresentedFps.GetValue(sessionB)!;
            var snapshotFpsA = SnapshotFps(currentPlaybackSnapshot.Invoke(sessionA, [])!);
            var snapshotFpsB = SnapshotFps(currentPlaybackSnapshot.Invoke(sessionB, [])!);
            Console.WriteLine($"srt_loopback_a state={state.GetValue(sessionA)} frame={gotFrameA} presented={gotPresentedFrameA} audio={gotAudioA} measuredFps={measuredPresentedFpsA:0.0} observedFps={observedPresentedFpsA:0.0} snapshotFps={snapshotFpsA:0.0}");
            Console.WriteLine($"srt_loopback_b state={state.GetValue(sessionB)} frame={gotFrameB} presented={gotPresentedFrameB} audio={gotAudioB} measuredFps={measuredPresentedFpsB:0.0} observedFps={observedPresentedFpsB:0.0} snapshotFps={snapshotFpsB:0.0}");
            if (measuredPresentedFpsA < MinimumSmoothPresentedFps ||
                measuredPresentedFpsB < MinimumSmoothPresentedFps ||
                observedPresentedFpsA < MinimumSmoothPresentedFps ||
                observedPresentedFpsB < MinimumSmoothPresentedFps ||
                snapshotFpsA < MinimumSmoothPresentedFps ||
                snapshotFpsB < MinimumSmoothPresentedFps)
            {
                throw new InvalidOperationException($"SRT loopback presented FPS must stay above {MinimumSmoothPresentedFps:0.0} fps.");
            }

            _ = tryAudio.Invoke(sessionA, [null]);
            _ = tryAudio.Invoke(sessionB, [null]);
            AssertAudioFollowsPresentedVideoPts(sessionType, sessionA, "srt_loopback_a");
            AssertAudioFollowsPresentedVideoPts(sessionType, sessionB, "srt_loopback_b");
        }
        finally
        {
            DisposeReflective(sessionA);
            DisposeReflective(sessionB);
            StopProcess(senderA);
            StopProcess(senderB);
        }
    }

    private static void AssertAudioFollowsPresentedVideoPts(Type sessionType, object session, string label)
    {
        var videoPts = double.NaN;
        var audioPts = double.NaN;
        var offsetMs = double.NaN;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        do
        {
            videoPts = (double)sessionType.GetProperty("LastPresentedVideoPtsSeconds")!.GetValue(session)!;
            audioPts = (double)sessionType.GetProperty("LastPresentedAudioPtsSeconds")!.GetValue(session)!;
            offsetMs = (double)sessionType.GetProperty("LastAudioVideoSyncOffsetMs")!.GetValue(session)!;
            if (double.IsFinite(videoPts) &&
                double.IsFinite(audioPts) &&
                double.IsFinite(offsetMs) &&
                offsetMs >= -30 &&
                offsetMs <= 150)
            {
                break;
            }

            Pump(null, 50);
        }
        while (DateTime.UtcNow < deadline);

        Console.WriteLine($"{label}_sync videoPts={videoPts:0.000} audioPts={audioPts:0.000} offsetMs={offsetMs:0.0}");
        if (!double.IsFinite(videoPts) ||
            !double.IsFinite(audioPts) ||
            !double.IsFinite(offsetMs) ||
            offsetMs < -30 ||
            offsetMs > 150)
        {
            throw new InvalidOperationException(
                $"{label} audio meter must follow the presented video PTS within -30..150ms; offset={offsetMs:0.0}ms.");
        }
    }

    private static double SnapshotFps(object snapshot)
    {
        return Convert.ToDouble(snapshot.GetType().GetProperty("Fps")!.GetValue(snapshot), CultureInfo.InvariantCulture);
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

    private sealed record PlaybackStreamFixtureState(
        bool Enabled,
        string InputUrl,
        string InputProtocol,
        bool AnalysisEnabled,
        bool AudioMonitorEnabled);

    private sealed record DictionaryEntryFixtureState(bool Exists, object? Value);

    private sealed class WpfAppScope : IDisposable
    {
        public WpfAppScope()
        {
            var application = Application.Current ?? new Application();
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/IPMultiViewer;component/DirectMatrixTheme.xaml",
                    UriKind.Absolute)
            });
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
