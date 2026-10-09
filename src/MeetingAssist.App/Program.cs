using Velopack;

namespace MeetingAssist.App;

/// <summary>
/// The entry point, ahead of WPF's generated one (StartupObject in the project file). Velopack
/// must run first: the installer, the uninstaller and the updater start this exe with their own
/// arguments, and Run handles those and exits before any window exists.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
