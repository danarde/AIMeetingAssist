using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MeetingAssist.CaptureProbe;

public partial class ProbeWindow : Window
{
    private bool _excluded = true;

    public ProbeWindow()
    {
        InitializeComponent();

        // Affinity is a property of the HWND, which does not exist until the source is
        // created. Applying it in the constructor silently does nothing.
        SourceInitialized += (_, _) => Apply();

        KeyDown += OnKeyDown;
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _excluded = !_excluded;
                Apply();
                break;
            case Key.Escape:
                Close();
                break;
        }
    }

    private void Apply()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var affinity = _excluded ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE;
        var (ok, detail) = NativeMethods.TrySetAffinity(hwnd, affinity);

        if (!ok)
        {
            // A failure here is the finding, not an error to hide. Say so in the largest text
            // on screen so it cannot be mistaken for a working exclusion.
            StateText.Text = "AFFINITY CALL FAILED";
            InstructionText.Text =
                "This window could not be excluded from capture. Note the detail line below — "
                + "it decides whether the overlay approach is viable at all.";
            DetailText.Text = detail;
            return;
        }

        StateText.Text = _excluded ? "EXCLUDED FROM CAPTURE" : "VISIBLE TO CAPTURE";
        InstructionText.Text = _excluded
            ? "Share your screen now. The far side should NOT see this window, while you still do. "
              + "Check Teams, Zoom and Meet — they use different capture paths and can differ."
            : "Control case. The far side SHOULD see this window. If they cannot, your screen share "
              + "is not showing this monitor and the excluded result above proves nothing.";
        DetailText.Text = $"WDA_{(_excluded ? "EXCLUDEFROMCAPTURE" : "NONE")} · {detail} · hwnd 0x{hwnd:X}";
    }
}
