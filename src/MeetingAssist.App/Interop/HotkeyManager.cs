using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace MeetingAssist.App.Interop;

/// <summary>Actions the app binds to global hotkeys (spec §4.8).</summary>
public enum HotkeyAction
{
    AskNarrow,
    AskWide,
    ToggleOverlay,
    PanicHide,
    CyclePreset,
    ToggleSession,

    /// <summary>Stops listening without ending the session, and resumes it; the transcript is kept.</summary>
    TogglePause,

    /// <summary>Mock mode: the AI playing the other party says its next line (backlog §3).</summary>
    MockNext,

    /// <summary>
    /// Not in the spec's table of six. Without it the app can only be ended from Task Manager:
    /// the overlay never takes focus, has no chrome, and panic hide is deliberately one-way.
    /// </summary>
    Quit
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8,
    /// <summary>Suppresses auto-repeat while the key is held (Windows 7+).</summary>
    NoRepeat = 0x4000
}

public sealed record HotkeyBinding(HotkeyAction Action, HotkeyModifiers Modifiers, uint VirtualKey, string Display);

/// <summary>
/// Global hotkeys via <c>RegisterHotKey</c>, which is what makes them work while PowerPoint or
/// Teams holds focus (FR-8.3).
///
/// Every registration is checked. A combination already claimed by another app fails silently
/// at the OS level, and discovering a dead hotkey mid-meeting is exactly the failure this app
/// exists to avoid (FR-8.1).
/// </summary>
public sealed partial class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private int _nextId = 1;
    private bool _disposed;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    public HotkeyManager(nint hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd)
                  ?? throw new InvalidOperationException("No HwndSource for the given window handle.");
        _source.AddHook(WndProc);
    }

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Bindings that could not be registered, for surfacing in the UI (FR-8.1).</summary>
    public List<HotkeyBinding> Failed { get; } = [];

    public bool Register(HotkeyBinding binding)
    {
        var id = _nextId++;

        // NoRepeat matters for Ask: holding the key would otherwise fire a burst of requests
        // straight into the provider's rate limit.
        var modifiers = (uint)(binding.Modifiers | HotkeyModifiers.NoRepeat);

        if (!RegisterHotKey(_source.Handle, id, modifiers, binding.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            Log.Warning("Hotkey {Display} for {Action} could not be registered (0x{Error:X}) — likely claimed by another application",
                binding.Display, binding.Action, error);
            Failed.Add(binding);
            return false;
        }

        _registered[id] = binding.Action;
        Log.Information("Hotkey {Display} bound to {Action}", binding.Display, binding.Action);
        return true;
    }

    /// <summary>
    /// Releases every binding so they can be registered again from edited settings. Without
    /// this, rebinding would leave the old combination claimed — the app would answer to a key
    /// the user had just removed, and the new one would collide with itself.
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        Failed.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || !_registered.TryGetValue((int)wParam, out var action)) return 0;

        handled = true;
        Pressed?.Invoke(action);
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _source.RemoveHook(WndProc);
    }
}
