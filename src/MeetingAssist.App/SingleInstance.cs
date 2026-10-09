namespace MeetingAssist.App;

/// <summary>
/// One copy of the app per Windows session. A second copy could not register the hotkeys the
/// first one holds, and both would write to the same transcript database, so a second launch
/// asks the first to show its main window, as the tray icon does, and exits.
///
/// A named mutex says a copy is running; a named event is how a later launch knows to ask.
/// Both are per session (Local\), so another user signed in to the same PC is unaffected, and a
/// build from the source tree and the installed app count as the same app.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _running;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _listening;

    public SingleInstance(string name = @"Local\MeetingAssist")
    {
        _running = new Mutex(true, $"{name}.Running", out var created);
        IsFirst = created;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $"{name}.Show");
    }

    /// <summary>No other copy was running when this one started.</summary>
    public bool IsFirst { get; }

    /// <summary>Asks the copy that is running to show itself.</summary>
    public void ShowFirst() => _show.Set();

    /// <summary>Calls <paramref name="show"/>, on a pool thread, each time a later launch asks.</summary>
    public void Listen(Action show) =>
        _listening = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, false);

    public void Dispose()
    {
        _listening?.Unregister(null);
        _show.Dispose();
        if (IsFirst) _running.ReleaseMutex();
        _running.Dispose();
    }
}
