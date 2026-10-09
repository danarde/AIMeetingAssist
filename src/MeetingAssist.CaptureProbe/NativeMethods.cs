using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeetingAssist.CaptureProbe;

/// <summary>
/// The whole point of the probe. Display affinity is the mechanism the entire product premise
/// rests on (spec FR-7.x), and it is the surface where competing Windows products visibly fail,
/// so it is exercised here in isolation before any real code depends on it.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>Window renders normally and appears in captures.</summary>
    public const uint WDA_NONE = 0x00000000;

    /// <summary>
    /// Window is excluded from capture: the compositor renders it to the screen but omits it
    /// from anything that captures the desktop. Requires Windows 10 version 2004 (build 19041).
    /// On older builds the call fails rather than silently doing nothing, which is why the
    /// return value is checked.
    /// </summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint hWnd, out uint pdwAffinity);

    /// <summary>
    /// Applies an affinity and reports what actually happened. Returns the Win32 error message
    /// on failure rather than throwing: a probe that crashes tells us less than one that says
    /// which call failed and why.
    /// </summary>
    public static (bool Ok, string Detail) TrySetAffinity(nint hwnd, uint affinity)
    {
        if (hwnd == 0) return (false, "window handle is not available yet");

        if (!SetWindowDisplayAffinity(hwnd, affinity))
        {
            var error = Marshal.GetLastWin32Error();
            return (false, $"SetWindowDisplayAffinity failed: {new Win32Exception(error).Message} (0x{error:X})");
        }

        // Read it back. A call that returns success but does not stick would be the worst
        // possible outcome — a false sense of safety while presenting to a customer.
        if (!GetWindowDisplayAffinity(hwnd, out var actual))
        {
            var error = Marshal.GetLastWin32Error();
            return (false, $"set succeeded but read-back failed: {new Win32Exception(error).Message} (0x{error:X})");
        }

        return actual != affinity
            ? (false, $"read-back mismatch: asked for 0x{affinity:X2}, window reports 0x{actual:X2}")
            : (true, $"confirmed by read-back (0x{actual:X2})");
    }
}
