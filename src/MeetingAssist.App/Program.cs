using Velopack;

namespace MeetingAssist.App;

/// <summary>
/// The entry point, ahead of WPF's generated one (StartupObject in the project file). Velopack
/// must run first: the installer, the uninstaller and the updater start this exe with their own
/// arguments, and Run handles those and exits before any window exists. Then only one copy may
/// run (<see cref="SingleInstance"/>).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            instance.ShowFirst();
            return;
        }

        var app = new App();
        app.InitializeComponent();
        instance.Listen(app.BringToFront);
        app.Run();
    }
}
