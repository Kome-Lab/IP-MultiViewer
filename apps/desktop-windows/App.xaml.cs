using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace IPMultiViewer.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\IPMultiViewer.Desktop.SingleInstance", out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            Shutdown(0);
            Environment.Exit(0);
            return;
        }

        DispatcherUnhandledException += (_, args) => LogUnhandledException("dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogUnhandledException("app-domain", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => LogUnhandledException("task", args.Exception);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void LogUnhandledException(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LocalConfigStore.LogDirectory);
            var path = Path.Combine(LocalConfigStore.LogDirectory, "desktop.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} Unhandled exception ({source}): {exception}{Environment.NewLine}");
        }
        catch
        {
            // Last-resort logging must not create another startup/render failure.
        }
    }
}
