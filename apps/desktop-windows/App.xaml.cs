using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace IPMultiViewer.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
