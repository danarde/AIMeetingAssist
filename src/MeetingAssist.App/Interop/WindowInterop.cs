using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MeetingAssist.App.Interop;

/// <summary>
/// The Win32 surface the overlay depends on (spec FR-7.2 to FR-7.4). None of this can be
/// covered by an automated test, which is why it is isolated here and kept as small as
/// possible: everything else lives in Core where it can be tested.
/// </summary>
internal static partial class WindowInterop
{
    private const int GWL_EXSTYLE = -20;

    /// <summary>Window never becomes the foreground window when clicked or shown.</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Keeps the window out of Alt+Tab and the taskbar.</summary>
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Clicks pass through to whatever is underneath.</summary>
    private const int WS_EX_TRANSPARENT = 0x00000020;

    public const uint WDA_NONE = 0x00000000;

    /// <summary>Requires Windows 10 version 2004 (build 19041) or later.</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint hWnd, out uint pdwAffinity);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    public static nint HandleOf(Window window) => new WindowInteropHelper(window).Handle;

    /// <summary>
    /// Applies the capture-exclusion affinity and verifies it stuck.
    ///
    /// The read-back is the point. A call that reports success without taking effect would give
    /// a false sense of safety at exactly the moment it matters — mid-presentation, to a
    /// customer. FR-7.3 requires the failure to be surfaced, so this returns a reason rather
    /// than a bare bool.
    /// </summary>
    public static (bool Ok, string Detail) TryExcludeFromCapture(nint hwnd, bool exclude = true)
    {
        if (hwnd == 0) return (false, "window handle not created yet");

        var affinity = exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;

        if (!SetWindowDisplayAffinity(hwnd, affinity))
            return (false, Describe("SetWindowDisplayAffinity"));

        if (!GetWindowDisplayAffinity(hwnd, out var actual))
            return (false, $"set succeeded but {Describe("GetWindowDisplayAffinity")}");

        return actual == affinity
            ? (true, $"0x{actual:X2} confirmed by read-back")
            : (false, $"read-back mismatch: asked 0x{affinity:X2}, got 0x{actual:X2}");
    }

    /// <summary>
    /// Applies FR-7.4's extended styles. <paramref name="clickThrough"/> adds
    /// <c>WS_EX_TRANSPARENT</c>; it is separate because a click-through window cannot be
    /// dragged, and dragging is how the user positions the overlay before a meeting.
    /// </summary>
    public static void ApplyOverlayStyles(nint hwnd, bool clickThrough)
    {
        if (hwnd == 0) return;

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        style = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style);
    }

    private static string Describe(string call)
    {
        var error = Marshal.GetLastWin32Error();
        return $"{call} failed: {new Win32Exception(error).Message} (0x{error:X})";
    }
}
