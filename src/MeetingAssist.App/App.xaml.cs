using System.IO;
using System.Windows;
using MeetingAssist.Core.Configuration;
using Serilog;
using Serilog.Formatting.Compact;

namespace MeetingAssist.App;

public partial class App : Application
{
    private AppHost? _host;
    private readonly Updates _updates = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No console to log to, so the file sink is the only record of what happened during a
        // meeting. Compact JSON keeps the latency marks post-processable.
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MeetingAssist", "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(logDir, "app-.jsonl"),
                rollingInterval: RollingInterval.Day, shared: true)
            .CreateLogger();

        // Nothing below may take down a live session silently.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
        };

        try
        {
            _host = new AppHost(AppConfig.FromEnvironment());
            _host.Start();
            _updates.DownloadInBackground();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show($"MeetingAssist could not start:\n\n{ex.Message}",
                "MeetingAssist", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>A later launch asked this copy to show itself. Called from any thread.</summary>
    public void BringToFront() => Dispatcher.BeginInvoke(() => _host?.BringToFront());

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _updates.ApplyOnExit();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
