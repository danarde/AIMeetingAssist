using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MeetingAssist.Core.Session;
using Serilog;

namespace MeetingAssist.App.Tray;

/// <summary>
/// The tray icon: the app's only always-visible control surface, and the way to reach it when
/// the overlay is hidden (spec FR-1.1).
///
/// <b>Explicit trade-off:</b> the tray icon is <i>not</i> hidden from screen sharing. The shell
/// owns that pixel area, so display affinity cannot apply to it, and a full-desktop share shows
/// it like any other icon. It is a small, unlabelled dot; the overlay — which carries the
/// actual content — remains excluded.
///
/// Icons are drawn in code rather than shipped as .ico resources: they are a coloured dot whose
/// colour is the session state, and generating them keeps the state vocabulary in one place
/// (FR-7.6) instead of split between code and a resource folder.
///
/// <b>No toast notifications.</b> FR-1.3 forbids them while a session is active, because a toast
/// <i>is</i> captured by screen sharing. Nothing in this class calls <c>ShowBalloonTip</c>, and
/// nothing should start.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _sessionItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Dictionary<SessionState, Icon> _icons = [];

    public TrayIcon()
    {
        _sessionItem = new ToolStripMenuItem("Start meeting", null, (_, _) => ToggleSession?.Invoke());
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause?.Invoke()) { Enabled = false };

        // The main window is what a click on a tray app is expected to open, so it leads the
        // menu in bold, the shell's convention for the default action.
        var open = new ToolStripMenuItem("Open MeetingAssist", null, (_, _) => Open?.Invoke());
        open.Font = new Font(open.Font, FontStyle.Bold);

        var menu = new ContextMenuStrip();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_sessionItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Show/hide overlay", null, (_, _) => ToggleOverlay?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = IconFor(SessionState.Idle),
            ContextMenuStrip = menu,
            Visible = true,
            Text = "MeetingAssist"
        };

        // Double-click is the conventional way to reach the main surface of a tray app.
        _icon.DoubleClick += (_, _) => Open?.Invoke();
    }

    public event Action? ToggleSession;
    public event Action? TogglePause;
    public event Action? Open;
    public event Action? ToggleOverlay;
    public event Action? Quit;

    public void ShowState(SessionState state, bool running, bool paused)
    {
        _sessionItem.Text = running ? "Stop meeting" : "Start meeting";
        _pauseItem.Text = paused ? "Resume" : "Pause";
        _pauseItem.Enabled = running;
        _icon.Icon = IconFor(state);

        // The tooltip is capped at 63 characters by the shell; anything longer is dropped
        // silently, tooltip and all.
        var text = $"MeetingAssist — {state}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Colour is the whole vocabulary; the dot is generated once per state and cached.</summary>
    private Icon IconFor(SessionState state)
    {
        if (_icons.TryGetValue(state, out var cached)) return cached;

        var colour = state switch
        {
            SessionState.Recording => Color.FromArgb(0x3F, 0xC1, 0x6A),
            SessionState.Paused => Color.FromArgb(0xA7, 0x8B, 0xFA),
            SessionState.Thinking => Color.FromArgb(0x5B, 0x9D, 0xF0),
            SessionState.Degraded => Color.FromArgb(0xE0, 0xA0, 0x30),
            SessionState.Error => Color.FromArgb(0xE0, 0x50, 0x50),
            _ => Color.FromArgb(0x90, 0x96, 0xA0)
        };

        var icon = Draw(colour);
        _icons[state] = icon;
        return icon;
    }

    private static Icon Draw(Color colour)
    {
        // 32px so the icon stays clean at 150% and 200% display scaling, which is the normal
        // configuration on the laptops this runs on.
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(colour);
            g.FillEllipse(fill, 5, 5, 22, 22);

            // A dark ring keeps the dot legible against a light taskbar.
            using var ring = new Pen(Color.FromArgb(0x60, 0x00, 0x00, 0x00), 2f);
            g.DrawEllipse(ring, 5, 5, 22, 22);
        }

        // GetHicon hands out an unmanaged handle; Icon.FromHandle does not own it, so it is
        // cloned and the original destroyed rather than leaked for the life of the process.
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        try
        {
            // Hidden explicitly: a NotifyIcon that is only disposed can leave a ghost in the
            // tray until the user moves the mouse over it.
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();

            foreach (var icon in _icons.Values) icon.Dispose();
            _icons.Clear();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Tray icon did not dispose cleanly");
        }
    }
}
