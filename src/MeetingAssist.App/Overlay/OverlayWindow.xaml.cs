using System.Windows;
using System.Windows.Media;
using MeetingAssist.App.Interop;
using MeetingAssist.Core.Session;
using Serilog;

namespace MeetingAssist.App.Overlay;

/// <summary>
/// The overlay. Deliberately thin: it renders state and text, and owns the Win32 window
/// properties that cannot live anywhere else. Every decision about *what* to show is made in
/// <see cref="MeetingSession"/>, which is testable.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlaySettings _settings;
    private string _lastAnswer = "";
    private bool _panicked;

    public OverlayWindow(OverlaySettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Width = settings.Width;
        AnswerScroll.MaxHeight = settings.MaxHeight;
        AnswerText.FontSize = settings.FontSize;
        AnswerText.LineHeight = settings.FontSize * 1.55;

        // The HWND does not exist until now, and both display affinity and the extended
        // styles are properties of the HWND — applying them earlier silently does nothing.
        SourceInitialized += (_, _) =>
        {
            var hwnd = WindowInterop.HandleOf(this);

            // Not click-through: the user drags the overlay to place it. Everything else in
            // FR-7.4 applies, so it never steals focus and never appears in Alt+Tab.
            WindowInterop.ApplyOverlayStyles(hwnd, clickThrough: false);

            var (ok, detail) = WindowInterop.TryExcludeFromCapture(hwnd);
            if (ok)
            {
                Log.Information("Capture exclusion active: {Detail}", detail);
            }
            else
            {
                // FR-7.3. The product's core promise is broken; the user must know before
                // they share their screen, not after.
                Log.Error("Capture exclusion FAILED: {Detail}", detail);
                NotHiddenReason = detail;
                ShowWarning($"NOT HIDDEN FROM SCREEN SHARING — {detail}");
            }

            ApplyPreset(_settings.Current);
        };

        MouseLeftButtonDown += (_, _) => { if (IsVisible) DragMove(); };

        // Only a drag counts as the user choosing a position. Assigning Left/Top also raises
        // LocationChanged, so without this guard the flag would be set by our own repositioning
        // and the overlay would never re-anchor again.
        LocationChanged += (_, _) => { if (!_positioning) _userMoved = true; };

        // SizeToContent grows the window as tokens stream in. A bottom-anchored overlay grows
        // downward off the screen unless it is re-anchored on every size change.
        SizeChanged += (_, _) => { if (!_userMoved) Reanchor(); };
    }

    private bool _userMoved;
    private bool _positioning;

    /// <summary>
    /// Why capture exclusion failed, or null when it holds. The overlay is hidden between
    /// meetings, so its own banner is not seen in time; the main window warns as well.
    /// </summary>
    public string? NotHiddenReason { get; private set; }

    /// <param name="detail">Why, when it needs saying: "transcript 2 min behind".</param>
    public void ShowState(SessionState state, string? detail = null)
    {
        StateText.Text = detail is null ? state.ToString() : $"{state} · {detail}";
        StateDot.Fill = (Brush)FindResource(state.ToString());
    }

    public void ShowHint(string hint) => HintText.Text = hint;

    /// <summary>Each tick keeps this much of the last reading: a fall-off over about a quarter second.</summary>
    private const double MeterDecay = 0.8;

    private double _micShown, _loopbackShown;

    /// <summary>The loudest sound on each channel since the last call, as RMS (0–1).</summary>
    public void ShowLevels(float mic, float loopback)
    {
        // Rise at once, fall gradually: a meter that drops to nothing between syllables flickers.
        _micShown = Math.Max(Meter(mic), _micShown * MeterDecay);
        _loopbackShown = Math.Max(Meter(loopback), _loopbackShown * MeterDecay);
        MicScale.ScaleX = _micShown;
        LoopbackScale.ScaleX = _loopbackShown;
    }

    /// <summary>
    /// RMS to bar length, on a decibel scale: -60 dBFS (near silence) is empty, -15 dBFS (loud
    /// speech) is full. A linear scale would leave ordinary speech as a sliver.
    /// </summary>
    public static double Meter(double rms) =>
        rms <= 0 ? 0 : Math.Clamp((20 * Math.Log10(rms) + 60) / 45, 0, 1);

    /// <summary>A button was clicked: the same actions the hotkeys raise.</summary>
    public event Action<HotkeyAction>? ActionRequested;

    public event Action? SettingsRequested;

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<HotkeyAction>(tag, out var action))
            ActionRequested?.Invoke(action);
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    // Segoe Fluent Icons code points, rendered and checked: solid play, stop and pause.
    private const string PlayGlyph = "";
    private const string StopGlyph = "";
    private const string PauseGlyph = "";

    private static readonly Brush Go = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush Halt = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush Hold = new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD));

    /// <summary>Labels and availability follow the session, so each button says what it will do.</summary>
    public void ShowControls(bool running, bool paused, bool canPause, bool mock)
    {
        Meters.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        SessionIcon.Text = running ? StopGlyph : PlayGlyph;
        SessionIcon.Foreground = running ? Halt : Go;
        SessionLabel.Text = running ? "Stop" : "Start";

        PauseIcon.Text = paused ? PlayGlyph : PauseGlyph;
        PauseIcon.Foreground = paused ? Go : Hold;
        PauseLabel.Text = paused ? "Resume" : "Pause";
        PauseButton.IsEnabled = running && canPause;

        MockButton.Visibility = mock ? Visibility.Visible : Visibility.Collapsed;
        MockButton.IsEnabled = running && !paused;
    }

    /// <summary>Each button's tooltip: what it does, and the hotkey bound to it if any.</summary>
    public void ShowBindings(IReadOnlyDictionary<HotkeyAction, string> bindings)
    {
        foreach (var button in Buttons(this))
        {
            if (button.Tag is not string tag || !Enum.TryParse<HotkeyAction>(tag, out var action)) continue;
            var what = Describe(action);
            button.ToolTip = bindings.TryGetValue(action, out var combo) ? $"{what}  ·  {combo}" : what;
        }
    }

    private static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleSession => "Start or stop the session",
        HotkeyAction.TogglePause => "Pause or resume listening; the conversation is kept",
        HotkeyAction.AskNarrow => "Cue notes on the last minute",
        HotkeyAction.AskWide => "Cue notes on the last three minutes",
        HotkeyAction.MockNext => "Mock: the other party's next line",
        HotkeyAction.ToggleOverlay => "Hide the overlay",
        _ => action.ToString()
    };

    private static IEnumerable<System.Windows.Controls.Button> Buttons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is System.Windows.Controls.Button button) yield return button;
            foreach (var nested in Buttons(child)) yield return nested;
        }
    }

    public void ShowWarning(string message)
    {
        WarningText.Text = message;
        WarningBanner.Visibility = Visibility.Visible;
    }

    /// <summary>Replaces the answer wholesale — used for status lines and the start of an Ask.</summary>
    public void SetAnswer(string text)
    {
        _lastAnswer = text;
        AnswerText.Text = text;
        AnswerScroll.ScrollToTop();
    }

    /// <summary>Appends a streamed token. Keeps the newest text in view as it arrives.</summary>
    public void AppendAnswer(string chunk)
    {
        _lastAnswer += chunk;
        AnswerText.Text = _lastAnswer;
        AnswerScroll.ScrollToBottom();
    }

    public void ApplyPreset(OverlayPreset preset)
    {
        Opacity = preset.Opacity;

        // A preset is an explicit instruction to reposition, so it overrides a manual drag.
        _userMoved = false;
        Reanchor();
    }

    /// <summary>
    /// Places the window in the current preset's working-area corner. Working area rather than
    /// screen bounds, so the overlay never lands under the taskbar.
    ///
    /// Bottom corners depend on the window's height, which <c>SizeToContent</c> changes as the
    /// answer streams in and which is still zero before the first layout pass. The measurement
    /// is therefore taken from the desired size when the actual size is not yet known, and this
    /// runs again on every SizeChanged.
    /// </summary>
    private void Reanchor()
    {
        var corner = _settings.Current.Corner;
        var area = SystemParameters.WorkArea;
        const double margin = 24;

        var height = ActualHeight > 0 ? ActualHeight
            : DesiredSize.Height > 0 ? DesiredSize.Height
            : _settings.MaxHeight;

        _positioning = true;
        try
        {
            Left = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
                ? area.Left + margin
                : area.Right - Width - margin;

            Top = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
                ? area.Top + margin
                : area.Bottom - height - margin;
        }
        finally
        {
            _positioning = false;
        }
    }

    /// <summary>
    /// FR-8.4: strictly one-way. Once panicked the overlay stays hidden for the life of the
    /// process, so the panic key can never be the thing that reveals it again.
    /// </summary>
    public void Panic()
    {
        _panicked = true;
        Hide();
        Log.Information("Panic hide engaged; overlay will not reappear this session");
    }

    public void ToggleVisible()
    {
        if (_panicked) return;
        if (IsVisible) Hide(); else ShowWithoutActivating();
    }

    public void ShowWithoutActivating()
    {
        if (_panicked) return;
        Show();

        // Re-anchor after a show: SizeToContent means the height changed while hidden.
        if (!_userMoved) Reanchor();
    }
}
