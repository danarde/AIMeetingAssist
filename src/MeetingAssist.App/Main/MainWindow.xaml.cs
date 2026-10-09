using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeetingAssist.App.Interop;
using MeetingAssist.App.Overlay;
using MeetingAssist.App.Settings;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Configuration;
using MeetingAssist.Core.Mock;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using Serilog;

namespace MeetingAssist.App.Main;

/// <summary>
/// One editable hotkey row. <see cref="Kind"/> is the enum saving writes back to
/// <c>hotkeys.json</c>; <see cref="Action"/> is only the display label the grid binds to, so
/// relabeling an action can never break the save path the way a reverse string lookup would.
/// </summary>
public sealed class HotkeyRow(HotkeyAction kind, string action, string combo, string note)
{
    public HotkeyAction Kind { get; } = kind;
    public string Action { get; } = action;
    public string Combo { get; set; } = combo;
    public string Note { get; } = note;
}

/// <summary>
/// The app's one window: Home, where meetings are started and past ones listed, and every
/// setting, in sections beside it. It lives for the life of the app; closing it hides it to the
/// tray, and a meeting hides it while the overlay is up.
///
/// Two behaviours are not obvious from the XAML and matter:
///
/// * The window is <b>capture-excluded like the overlay</b>. Left open during a screen share it
///   would leak the profile, the meeting notes and a masked key — considerably more than the
///   overlay ever shows — so it gets the same treatment, and panic hide hides it.
/// * <b>Nothing is written as you type.</b> Save applies everything at once, so a stray
///   keystroke cannot reconfigure a meeting that is in progress. Choosing a profile is the
///   exception: it is a choice of what to run next, not an edit.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IMainHost _host;
    private readonly ObservableCollection<HotkeyRow> _hotkeys = [];
    private readonly Dictionary<Section, (RadioButton Nav, ScrollViewer Page)> _sections;

    private ContextProfile _editing;
    private List<AudioDeviceInfo> _micDevices = [];
    private List<AudioDeviceInfo> _loopbackDevices = [];
    private bool _loading;

    public MainWindow(IMainHost host)
    {
        _host = host;
        InitializeComponent();

        _editing = host.Profiles.Load(host.Settings.ProfileName);

        _sections = new()
        {
            [Section.Home] = (NavHome, HomePage),
            [Section.History] = (NavHistory, HistoryPage),
            [Section.GetStarted] = (NavGetStarted, GetStartedPage),
            [Section.Profile] = (NavProfile, ProfilePage),
            [Section.Devices] = (NavDevices, DevicesPage),
            [Section.Keys] = (NavKeys, KeysPage),
            [Section.Hotkeys] = (NavHotkeys, HotkeysPage),
            [Section.Overlay] = (NavOverlay, OverlayPage),
            [Section.Rehearsal] = (NavRehearsal, RehearsalPage),
            [Section.Playback] = (NavPlayback, PlaybackPage),
            [Section.General] = (NavGeneral, GeneralPage)
        };

        foreach (var (section, (nav, _)) in _sections)
            nav.Checked += (_, _) => ShowSection(section);

        SourceInitialized += (_, _) =>
        {
            var hwnd = WindowInterop.HandleOf(this);
            var (ok, detail) = WindowInterop.TryExcludeFromCapture(hwnd);

            if (ok)
            {
                Log.Information("Main window capture exclusion active: {Detail}", detail);
            }
            else
            {
                NotHiddenReason = detail;
                Log.Error("Main window is NOT hidden from screen sharing: {Detail}", detail);
            }
        };

        // Home is current whenever it is looked at: a meeting may have ended, or a hotkey been
        // taken by another app, since it was last shown.
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshHome(); };

        HotkeyList.ItemsSource = _hotkeys;
        LoadEverything();
        ShowStep(0);

        var config = AppConfig.From(host.Secrets);
        Navigate(Walkthrough.OpensAtStart(host.Settings.WalkthroughShown,
            !string.IsNullOrWhiteSpace(config.GroqApiKey), !string.IsNullOrWhiteSpace(config.GeminiApiKey))
            ? Section.GetStarted
            : Section.Home);
    }

    /// <summary>Why capture exclusion failed for this window, or null when it holds.</summary>
    public string? NotHiddenReason { get; private set; }

    // ------------------------------------------------------------------ sections

    public void Navigate(Section section) => _sections[section].Nav.IsChecked = true;

    private void ShowSection(Section section)
    {
        foreach (var (key, (_, page)) in _sections)
            page.Visibility = key == section ? Visibility.Visible : Visibility.Collapsed;

        // Home, History and Get started are not settings: nothing on them waits for Save.
        SaveBar.Visibility = section is Section.Home or Section.History or Section.GetStarted
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (section == Section.GetStarted) RefreshGetStarted();
    }

    private void OnEditProfile(object sender, RoutedEventArgs e) => Navigate(Section.Profile);

    private void OnFixWarning(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Section section }) Navigate(section);
    }

    // ------------------------------------------------------------------ get started

    private int _step;

    private StackPanel[] StepPanels => [Step1, Step2, Step3, Step4];

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, Walkthrough.Steps - 1);
        var panels = StepPanels;
        for (var i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;

        StepCount.Text = $"GET STARTED · STEP {_step + 1} OF {Walkthrough.Steps}";
        StepBack.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        StepNext.Content = _step == Walkthrough.Steps - 1 ? "Go to Home" : "Next";
        GetStartedPage.ScrollToTop();
    }

    private void OnStepBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnStepNext(object sender, RoutedEventArgs e)
    {
        if (_step == Walkthrough.Steps - 1) Navigate(Section.Home);
        else ShowStep(_step + 1);
    }

    /// <summary>
    /// The statuses on each step, read afresh: keys or a profile may have been saved on their
    /// own pages since the walkthrough was last shown.
    /// </summary>
    private void RefreshGetStarted()
    {
        if (!_host.Settings.WalkthroughShown)
        {
            _host.Settings.WalkthroughShown = true;
            _host.Settings.Save();
        }

        var config = AppConfig.From(_host.Secrets);
        ShowDone(GroqStep, Walkthrough.KeyStatus("Groq", !string.IsNullOrWhiteSpace(config.GroqApiKey)));
        ShowDone(GeminiStep, Walkthrough.KeyStatus("Gemini", !string.IsNullOrWhiteSpace(config.GeminiApiKey)));
        ShowDone(ProfileStep, Walkthrough.ProfileStatus(_host.Profiles.Load(_host.Settings.ProfileName)));

        FirstMeetingList.ItemsSource = Walkthrough.FirstMeeting(_host.Hotkeys.Bindings, _host.FailedHotkeys);
    }

    private void ShowDone(TextBlock target, (string Text, bool Done) status)
    {
        target.Text = (status.Done ? "✓  " : "") + status.Text;
        target.Foreground = (Brush)FindResource(status.Done ? "Ok" : "Muted");
    }

    /// <summary>Opens a web page in the default browser; the page's address rides in Tag.</summary>
    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open {Url}", url);
        }
    }

    // ------------------------------------------------------------------ home

    /// <summary>Warnings, past meetings and the start hint, read afresh from the host.</summary>
    public void RefreshHome()
    {
        var warnings = _host.Warnings();
        WarningList.ItemsSource = warnings;
        WarningsCard.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var now = DateTime.Now;
        var meetings = _host.Meetings().Select(m => MeetingRow.From(m, now)).ToList();
        MeetingList.ItemsSource = meetings.Take(RecentMeetings).ToList();
        HistoryList.ItemsSource = meetings;
        NoMeetings.Visibility = NoHistory.Visibility =
            meetings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SeeAll.Content = $"See all {meetings.Count}";
        SeeAll.Visibility = meetings.Count > RecentMeetings ? Visibility.Visible : Visibility.Collapsed;
        HomeStorage.Text = _host.StorageDescription;
        ExportFolderText.Text = $"Exports are saved to {_host.TranscriptFolder}.";

        ShowSessionState();
    }

    /// <summary>The session's state, wherever it shows: the sidebar, and Home's buttons.</summary>
    public void ShowSessionState()
    {
        var state = _host.State;
        var running = _host.SessionRunning;
        var rehearsing = _host.Rehearsing;

        StateDot.Fill = StateColour(state);
        StateText.Text = rehearsing ? $"{state} · rehearsal" : state.ToString();

        var noun = rehearsing ? "rehearsal" : "meeting";
        HomeTitle.Text = running ? $"{Capitalised(noun)} in progress" : "Ready for your next meeting";
        HomeLead.Text = running
            ? "The overlay is showing the cue notes. Stop here, from the overlay, or with the hotkey."
            : "Pick the profile for this meeting and start. The overlay appears and this window hides; it comes back when the meeting ends.";

        StartButton.Style = (Style)FindResource(running ? "Danger" : "Primary");
        StartGlyph.Text = running ? "\uE71A" : "\uE768";
        StartLabel.Text = running ? $"Stop {noun}" : "Start meeting";
        RehearseButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        HomeProfilePicker.IsEnabled = !running;
        CanExport = !running;

        var combo = Binding(HotkeyAction.ToggleSession);
        StartHint.Text = combo is null
            ? ""
            : $"{combo} starts and stops a meeting from anywhere, without this window.";
        StartHint.Visibility = combo is null ? Visibility.Collapsed : Visibility.Visible;

        static string Capitalised(string word) => char.ToUpperInvariant(word[0]) + word[1..];
    }

    /// <summary>A line under the start buttons: how the last start or stop went.</summary>
    public void ShowOutcome(string message, bool problem)
    {
        OutcomeText.Text = message;
        OutcomeText.Foreground = problem ? Palette("Warn") : Palette("Muted");
        OutcomeText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The bound combination, or null when there is none or another app owns it.</summary>
    private string? Binding(HotkeyAction action) =>
        _host.Hotkeys.Bindings.TryGetValue(action.ToString(), out var combo)
        && combo.Length > 0
        && !_host.FailedHotkeys.Contains(combo, StringComparer.OrdinalIgnoreCase)
            ? combo
            : null;

    /// <summary>The tray icon's colours, so the dot means the same thing in both places.</summary>
    private static Brush StateColour(SessionState state) => new SolidColorBrush(state switch
    {
        SessionState.Recording => Color.FromRgb(0x3F, 0xC1, 0x6A),
        SessionState.Paused => Color.FromRgb(0xA7, 0x8B, 0xFA),
        SessionState.Thinking => Color.FromRgb(0x5B, 0x9D, 0xF0),
        SessionState.Degraded => Color.FromRgb(0xE0, 0xA0, 0x30),
        SessionState.Error => Color.FromRgb(0xE0, 0x50, 0x50),
        _ => Color.FromRgb(0x90, 0x96, 0xA0)
    });

    // ------------------------------------------------------------------ history

    /// <summary>How many meetings Home shows; History has them all.</summary>
    private const int RecentMeetings = 5;

    public static readonly DependencyProperty CanExportProperty = DependencyProperty.Register(
        nameof(CanExport), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    /// <summary>
    /// False during a meeting, for Export and Delete alike. Export opens the folder in Explorer,
    /// an ordinary window that a screen share would show, and this app puts nothing on screen
    /// that a share can see; the meeting being held is itself one of the rows Delete could hit.
    /// </summary>
    public bool CanExport
    {
        get => (bool)GetValue(CanExportProperty);
        private set => SetValue(CanExportProperty, value);
    }

    private void OnSeeAll(object sender, RoutedEventArgs e) => Navigate(Section.History);

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;

        var (path, problem) = _host.ExportTranscript(id);
        ShowExportProblem(problem);

        // Shown selected in its folder: from there it can be opened, or dragged into a chat.
        if (path is not null) Explore($"/select,\"{path}\"");
    }

    private async void OnDeleteMeeting(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        if (HistoryList.ItemsSource is not IEnumerable<MeetingRow> rows) return;
        var row = rows.FirstOrDefault(m => m.Id == id);
        if (row is null) return;

        var what = row.Type.Length > 0 ? row.Type.ToLowerInvariant() : "meeting";
        if (!Confirm(
                $"Delete the {what} of {row.When} ({row.Profile}, {row.Lines} lines)? Its transcript "
                + "is removed from History and cannot be recovered. A file you already exported stays.",
                "Delete meeting"))
            return;

        try
        {
            var problem = await _host.DeleteMeetingAsync(id);
            ShowExportProblem(problem);
            if (problem is null) Status($"Deleted the {what} of {row.When}.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not delete meeting {Meeting}", id);
            ShowExportProblem($"The meeting could not be deleted: {ex.Message}");
        }

        RefreshHome();
    }

    private void OnOpenExportFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_host.TranscriptFolder);
            Explore($"\"{_host.TranscriptFolder}\"");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open the export folder");
            ShowExportProblem($"Could not open {_host.TranscriptFolder}: {ex.Message}");
        }
    }

    private void Explore(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open Explorer");
            ShowExportProblem($"The file was written, but Explorer could not be opened: {ex.Message}");
        }
    }

    private void ShowExportProblem(string? problem)
    {
        foreach (var line in new[] { HomeExportProblem, HistoryExportProblem })
        {
            line.Text = problem ?? "";
            line.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnStartMeeting(object sender, RoutedEventArgs e) => _ = StartOrStopAsync(rehearse: false);

    private void OnRehearse(object sender, RoutedEventArgs e) => _ = StartOrStopAsync(rehearse: true);

    private async Task StartOrStopAsync(bool rehearse)
    {
        if (_host.SessionRunning)
        {
            StartButton.IsEnabled = false;
            try
            {
                await _host.StopMeetingAsync();
            }
            finally
            {
                StartButton.IsEnabled = true;
            }
            return;
        }

        // The meeting runs on what is saved, so profile edits still in the boxes would be
        // silently left out of it.
        if (HasUnsavedProfileEdits())
        {
            if (!Confirm($"“{_editing.Name}” has unsaved changes. Save them and start?", "Unsaved changes"))
                return;
            if (!Save()) return;
        }

        ShowOutcome("", problem: false);
        if (_host.StartMeeting(rehearse) is { } problem) ShowOutcome(problem, problem: true);
    }

    // ------------------------------------------------------------------ loading

    private void LoadEverything()
    {
        _loading = true;
        try
        {
            LoadProfileList();
            LoadProfileFields();
            LoadDevices();
            LoadKeys();
            LoadHotkeys();
            LoadOverlay();
            LoadPlayback();
            LoadMock();
            LoadGeneral();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadGeneral()
    {
        NarrowSeconds.Text = _host.Settings.NarrowAskSeconds.ToString(CultureInfo.InvariantCulture);
        WideSeconds.Text = _host.Settings.WideAskSeconds.ToString(CultureInfo.InvariantCulture);
        StorageText.Text = _host.StorageDescription;
    }

    /// <summary>Both pickers, Home's and the Profile section's: they are one choice shown twice.</summary>
    private void LoadProfileList()
    {
        var names = _host.Profiles.Names().ToList();
        if (names.Count == 0) names.Add(_editing.Name);

        var selected = names.Contains(_editing.Name) ? _editing.Name : names[0];
        foreach (var picker in new[] { ProfilePicker, HomeProfilePicker })
        {
            picker.ItemsSource = names;
            picker.SelectedItem = selected;
        }
    }

    private void LoadProfileFields()
    {
        MeetingText.Text = _editing.Meeting;
        AboutMe.Text = _editing.AboutMe;
        Questions.Text = _editing.Questions;
        Vocabulary.Text = _editing.Vocabulary;
        LanguageCode.Text = _editing.Language;
        MicLabel.Text = _editing.MicLabel;
        LoopbackLabel.Text = _editing.LoopbackLabel;
        AnswerStyle.Text = _editing.AnswerStyle;
        MockBrief.Text = _editing.MockBrief;
        UpdateVocabularyCount();
    }

    private void LoadDevices()
    {
        _micDevices = [.. AudioDevices.Capture()];
        _loopbackDevices = [.. AudioDevices.Render()];

        Fill(MicDevice, _micDevices, _host.Settings.MicDeviceId);
        Fill(LoopbackDevice, _loopbackDevices, _host.Settings.LoopbackDeviceId);

        static void Fill(ComboBox box, List<AudioDeviceInfo> devices, string? selectedId)
        {
            var items = new List<string> { "Windows default (recommended)" };
            items.AddRange(devices.Select(d => d.Display));
            box.ItemsSource = items;

            var index = selectedId is null ? -1 : devices.FindIndex(d => d.Id == selectedId);

            // A pinned device that is no longer connected must not silently become a different
            // device: fall back to the default entry and let the user notice.
            box.SelectedIndex = index >= 0 ? index + 1 : 0;
        }
    }

    private void LoadKeys()
    {
        GroqCurrent.Text = $"Stored: {SecretStore.Mask(_host.Secrets.Get(SecretStore.GroqApiKey))}";
        GroqPlanPicker.SelectedIndex = _host.Settings.GroqPlan == GroqPlan.Developer ? 1 : 0;
        GeminiCurrent.Text = $"Stored: {SecretStore.Mask(_host.Secrets.Get(SecretStore.GeminiApiKey))}";

        GeminiModel.Text = _host.Secrets.Get(SecretStore.GeminiModel) ?? AppConfig.DefaultGeminiModel;
        SttModel.Text = _host.Secrets.Get(SecretStore.SttModel) ?? "whisper-large-v3-turbo";

        KeyNotice.Text = _host.Secrets.Migration switch
        {
            SecretMigration.Completed =>
                "Keys were imported from the old plaintext file, which has been deleted.",
            SecretMigration.Incomplete =>
                $"A plaintext copy of the keys is still at {AppConfig.SecretsPath} — the import "
                + "did not complete. Delete it once the keys here are working.",
            _ => ""
        };

        foreach (var (name, box) in new[]
                 {
                     ("GROQ_API_KEY", GroqStatus),
                     ("GEMINI_API_KEY", GeminiStatus)
                 })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 })
                box.Text = $"{name} is set and overrides this";
        }
    }

    private void LoadHotkeys()
    {
        _hotkeys.Clear();

        var failed = _host.FailedHotkeys;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var combo = _host.Hotkeys.Bindings.TryGetValue(action.ToString(), out var value) ? value : "";
            var note = failed.Contains(combo, StringComparer.OrdinalIgnoreCase)
                ? "in use by another app"
                : "";
            _hotkeys.Add(new HotkeyRow(action, Describe(action), combo, note));
        }

        HotkeyNotice.Text = failed.Count > 0
            ? $"Not registered — another application already owns: {string.Join(", ", failed)}"
            : "All hotkeys are registered.";
    }

    private static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.AskNarrow => "Ask (narrow)",
        HotkeyAction.AskWide => "Ask (wide)",
        HotkeyAction.ToggleOverlay => "Show/hide overlay",
        HotkeyAction.PanicHide => "Panic hide",
        HotkeyAction.CyclePreset => "Cycle position",
        HotkeyAction.ToggleSession => "Start/stop session",
        HotkeyAction.TogglePause => "Pause/resume session",
        HotkeyAction.MockNext => "Mock: next line",
        HotkeyAction.Quit => "Quit",
        _ => action.ToString()
    };

    private void LoadOverlay()
    {
        OverlayPreset.ItemsSource = OverlaySettings.Presets.Select(p => p.Display).ToList();
        OverlayPreset.SelectedIndex =
            Math.Clamp(_host.Overlay.PresetIndex, 0, OverlaySettings.Presets.Length - 1);

        OverlayFontSize.Text = _host.Overlay.FontSize.ToString(CultureInfo.InvariantCulture);
        OverlayWidth.Text = _host.Overlay.Width.ToString(CultureInfo.InvariantCulture);
        OverlayMaxHeight.Text = _host.Overlay.MaxHeight.ToString(CultureInfo.InvariantCulture);
    }

    private void LoadPlayback()
    {
        PlaybackMode.IsChecked = _host.Settings.PlaybackMode;
        MicWav.Text = _host.Settings.MicWavPath ?? "";
        LoopbackWav.Text = _host.Settings.LoopbackWavPath ?? "";
        PlaybackRealTime.IsChecked = _host.Settings.PlaybackRealTime;
    }

    /// <summary>One entry of the voice box: an engine and a voice of it, null for its default.</summary>
    private sealed record VoiceChoice(VoiceEngine Engine, string? Name, string Label)
    {
        public override string ToString() => Label;
    }

    private void LoadMock()
    {
        var choices = GeminiVoice.Voices
            .Select(name => new VoiceChoice(VoiceEngine.Gemini, name,
                name == GeminiVoice.DefaultVoice ? $"Gemini: {name} (default)" : $"Gemini: {name}"))
            .Append(new VoiceChoice(VoiceEngine.Windows, null, "Windows: automatic (matches the profile language)"))
            .Concat(WindowsVoice.Installed().Select(name => new VoiceChoice(VoiceEngine.Windows, name, $"Windows: {name}")))
            .ToList();

        var engine = _host.Settings.MockVoiceEngine;
        var saved = _host.Settings.MockVoiceName;
        if (engine == VoiceEngine.Gemini) saved ??= GeminiVoice.DefaultVoice;

        // A saved voice that is no longer offered stays listed, so saving without touching
        // this box does not quietly change it; the session falls back on its own.
        var selected = choices.FirstOrDefault(c => c.Engine == engine && c.Name == saved);
        if (selected is null)
        {
            selected = new VoiceChoice(engine, saved, $"{engine}: {saved}");
            choices.Add(selected);
        }

        MockVoice.ItemsSource = choices;
        MockVoice.SelectedItem = selected;
    }

    private VoiceChoice SelectedVoice() =>
        MockVoice.SelectedItem as VoiceChoice ?? new VoiceChoice(VoiceEngine.Gemini, null, "");

    private async void OnTestVoice(object sender, RoutedEventArgs e)
    {
        try
        {
            var language = string.IsNullOrWhiteSpace(LanguageCode.Text) ? "en" : LanguageCode.Text.Trim();
            var key = GeminiKey.Password is { Length: > 0 } typed ? typed : _host.Secrets.Get(SecretStore.GeminiApiKey);
            var choice = SelectedVoice();

            using var voice = Voices.Create(choice.Engine, choice.Name, language, key);
            string? fellBack = null;
            if (voice is GeminiVoice gemini) gemini.FellBack += reason => fellBack = reason;

            Status($"Speaking with {voice.Description}…");
            await voice.SpeakAsync(
                "Hi, thanks for joining. Can you hear me clearly? Let's get started.", CancellationToken.None);

            if (fellBack is null) Status($"{voice.Description} works.");
            else Warn($"{voice.Description} could not speak ({fellBack}), so the Windows voice did.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Voice test failed");
            Warn($"The voice could not play: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ profile

    /// <summary>
    /// Choosing a profile, in either picker, makes it the one the next meeting uses at once —
    /// there is no Save on Home to press — and opens it for editing.
    /// </summary>
    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ((ComboBox)sender).SelectedItem is not string name || name == _editing.Name) return;

        // Switching away without saving would lose the edits silently, which is the one thing a
        // window must never do.
        if (HasUnsavedProfileEdits() && !Confirm(
                $"Discard the unsaved changes to “{_editing.Name}”?", "Unsaved changes"))
        {
            _loading = true;
            ProfilePicker.SelectedItem = _editing.Name;
            HomeProfilePicker.SelectedItem = _editing.Name;
            _loading = false;
            return;
        }

        _editing = _host.Profiles.Load(name);
        _loading = true;
        ProfilePicker.SelectedItem = name;
        HomeProfilePicker.SelectedItem = name;
        LoadProfileFields();
        _loading = false;

        MakeActive(_editing.Name);
    }

    /// <summary>The profile the next meeting uses: whichever was last chosen, created or saved.</summary>
    private void MakeActive(string name)
    {
        _host.Settings.ProfileName = name;
        _host.Settings.Save();
        _host.ReloadConfiguration();
    }

    private bool HasUnsavedProfileEdits() =>
        MeetingText.Text != _editing.Meeting
        || AboutMe.Text != _editing.AboutMe
        || Questions.Text != _editing.Questions
        || Vocabulary.Text != _editing.Vocabulary
        || LanguageCode.Text != _editing.Language
        || MicLabel.Text != _editing.MicLabel
        || LoopbackLabel.Text != _editing.LoopbackLabel
        || AnswerStyle.Text != _editing.AnswerStyle
        || MockBrief.Text != _editing.MockBrief;

    private void OnNewProfile(object sender, RoutedEventArgs e)
    {
        if (Prompt("Name for the new profile:", "New profile") is not { } name) return;

        if (_host.Profiles.Exists(name))
        {
            Warn($"A profile called “{name}” already exists.");
            return;
        }

        var profile = ContextProfile.Sample();
        profile.Name = ProfileLibrary.Sanitize(name);

        if (!_host.Profiles.Save(profile))
        {
            Warn("Could not write the new profile.");
            return;
        }

        _editing = profile;
        _loading = true;
        LoadProfileList();
        LoadProfileFields();
        _loading = false;
        MakeActive(profile.Name);
        Status($"Created “{profile.Name}”.");
    }

    private void OnDuplicateProfile(object sender, RoutedEventArgs e)
    {
        if (Prompt($"Copy “{_editing.Name}” to:", "Duplicate profile") is not { } name) return;

        var copy = _host.Profiles.Duplicate(_editing.Name, name);
        if (copy is null)
        {
            Warn($"Could not create “{name}” — the name may already be taken.");
            return;
        }

        _editing = copy;
        _loading = true;
        LoadProfileList();
        LoadProfileFields();
        _loading = false;
        MakeActive(copy.Name);
        Status($"Duplicated to “{copy.Name}”.");
    }

    /// <summary>
    /// Makes a new profile from the JSON an AI chat wrote (see skills/meeting-profile). It never
    /// replaces an existing profile: a name that is taken is asked for again, so a paste can
    /// only add.
    /// </summary>
    private void OnPasteProfile(object sender, RoutedEventArgs e)
    {
        ContextProfile pasted;
        try
        {
            pasted = ContextProfile.FromJson(Clipboard.ContainsText() ? Clipboard.GetText() : "");
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Runtime.InteropServices.ExternalException)
        {
            Warn($"The clipboard does not hold a profile ({ex.Message}). Copy the whole JSON the AI wrote, then paste again.");
            return;
        }

        if (HasUnsavedProfileEdits() && !Confirm(
                $"Discard the unsaved changes to “{_editing.Name}”?", "Unsaved changes"))
            return;

        var name = pasted.Name;
        while (string.IsNullOrWhiteSpace(name) || name == ProfileLibrary.DefaultName || _host.Profiles.Exists(name))
        {
            var why = _host.Profiles.Exists(name) ? $"“{name}” already exists. " : "";
            if (Prompt($"{why}Name for the pasted profile:", "Paste profile") is not { } typed) return;
            name = typed;
        }

        pasted.Name = ProfileLibrary.Sanitize(name);
        if (!_host.Profiles.Save(pasted))
        {
            Warn("Could not write the pasted profile.");
            return;
        }

        _editing = pasted;
        _loading = true;
        LoadProfileList();
        LoadProfileFields();
        _loading = false;
        MakeActive(pasted.Name);
        Status($"Created “{pasted.Name}” from the pasted profile. Read it through before the meeting.");
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (!Confirm($"Delete the profile “{_editing.Name}”? This cannot be undone.", "Delete profile"))
            return;

        if (!_host.Profiles.Delete(_editing.Name))
        {
            Warn("Could not delete it. The last remaining profile is always kept.");
            return;
        }

        _editing = _host.Profiles.Load(_host.Profiles.Names().FirstOrDefault());
        _loading = true;
        LoadProfileList();
        LoadProfileFields();
        _loading = false;
        MakeActive(_editing.Name);
        Status("Deleted.");
    }

    private void OnVocabularyChanged(object sender, TextChangedEventArgs e) => UpdateVocabularyCount();

    private void UpdateVocabularyCount()
    {
        // Whisper truncates the prompt at ~224 tokens without saying so, and the symptom is
        // silently worse recognition of exactly the words that were added last.
        var chars = Vocabulary.Text.Length;
        var estimatedTokens = chars / 4;

        VocabularyCount.Text = estimatedTokens > 224
            ? $"{chars} characters, roughly {estimatedTokens} tokens — over the ~224-token cap; "
              + "everything past it is dropped without warning. Trim it."
            : $"{chars} characters, roughly {estimatedTokens} of ~224 tokens. "
              + "Proper nouns, product names and attendee names help most.";

        VocabularyCount.Foreground = estimatedTokens > 224 ? Palette("Warn") : Palette("Muted");
    }

    private void OnLoadDefaultStyle(object sender, RoutedEventArgs e) =>
        AnswerStyle.Text = PromptBuilder.DefaultStyle;

    private void OnClearStyle(object sender, RoutedEventArgs e) => AnswerStyle.Text = "";

    // ------------------------------------------------------------------ actions

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        LoadDevices();
        Status($"{_micDevices.Count} microphone(s), {_loopbackDevices.Count} playback device(s).");
    }

    private async void OnTestGroq(object sender, RoutedEventArgs e)
    {
        var key = GroqKey.Password is { Length: > 0 } typed ? typed : _host.Secrets.Get(SecretStore.GroqApiKey);
        await RunCheck(GroqStatus, () => ProviderCheck.GroqAsync(key));
    }

    private async void OnTestGemini(object sender, RoutedEventArgs e)
    {
        var key = GeminiKey.Password is { Length: > 0 } typed ? typed : _host.Secrets.Get(SecretStore.GeminiApiKey);
        var model = GeminiModel.Text.Trim();
        await RunCheck(GeminiStatus, () => ProviderCheck.GeminiAsync(key, model));
    }

    private static async Task RunCheck(TextBlock status, Func<Task<ProviderStatus>> check)
    {
        status.Text = "Checking…";
        status.Foreground = Palette("Accent");

        var result = await check();

        status.Text = result.Detail;
        status.Foreground = result.Ok ? Palette("Ok") : Palette("Bad");
    }

    private void OnBrowseMicWav(object sender, RoutedEventArgs e) => Browse(MicWav);

    private void OnBrowseLoopbackWav(object sender, RoutedEventArgs e) => Browse(LoopbackWav);

    private static void Browse(TextBox target)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    // ------------------------------------------------------------------ saving

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    /// <summary>Writes every section, and reports whether it all went through.</summary>
    private bool Save()
    {
        try
        {
            SaveProfile();
            SaveSettings();
            SaveKeys();
            SaveOverlay();
            SaveHotkeys();

            // Reloading rebuilds the transcriber, the assistant and the active profile from what
            // is now on disk. It is refused while a session runs: swapping the profile mid-Ask
            // would change the prompt prefix and quietly break caching, and swapping the
            // transcriber would drop in-flight audio.
            _host.ReloadConfiguration();
            if (_host.SessionRunning)
                Status("Saved. The meeting in progress keeps its current profile, devices and keys until it ends.");
            else
                Status("Saved.");

            _loading = true;
            LoadKeys();
            LoadHotkeys();
            _loading = false;

            GroqKey.Clear();
            GeminiKey.Clear();
            RefreshHome();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save settings");
            Warn($"Could not save: {ex.Message}");
            return false;
        }
    }

    private void SaveProfile()
    {
        _editing.Meeting = MeetingText.Text;
        _editing.AboutMe = AboutMe.Text;
        _editing.Questions = Questions.Text;
        _editing.Vocabulary = Vocabulary.Text;
        _editing.Language = string.IsNullOrWhiteSpace(LanguageCode.Text) ? "en" : LanguageCode.Text.Trim();
        _editing.MicLabel = Blank(MicLabel.Text, "You");
        _editing.LoopbackLabel = Blank(LoopbackLabel.Text, "Other side");
        _editing.AnswerStyle = AnswerStyle.Text;
        _editing.MockBrief = MockBrief.Text;

        if (!_host.Profiles.Save(_editing)) Warn("Could not write the profile.");

        static string Blank(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private void SaveSettings()
    {
        var settings = _host.Settings;

        settings.ProfileName = _editing.Name;
        settings.NarrowAskSeconds = ParseInt(NarrowSeconds.Text, settings.NarrowAskSeconds);
        settings.WideAskSeconds = ParseInt(WideSeconds.Text, settings.WideAskSeconds);

        settings.MicDeviceId = SelectedDeviceId(MicDevice, _micDevices);
        settings.LoopbackDeviceId = SelectedDeviceId(LoopbackDevice, _loopbackDevices);

        settings.PlaybackMode = PlaybackMode.IsChecked == true;
        settings.MicWavPath = Trimmed(MicWav.Text);
        settings.LoopbackWavPath = Trimmed(LoopbackWav.Text);
        settings.PlaybackRealTime = PlaybackRealTime.IsChecked == true;

        settings.GroqPlan = GroqPlanPicker.SelectedIndex == 1 ? GroqPlan.Developer : GroqPlan.Free;

        var voice = SelectedVoice();
        settings.MockVoiceEngine = voice.Engine;
        if (voice.Engine == VoiceEngine.Gemini) settings.MockGeminiVoice = voice.Name;
        else settings.MockVoice = voice.Name;

        if (settings.PlaybackMode && !settings.PlaybackReady)
            Warn("Playback is on, but one of the WAV files is missing. The session will use live audio.");

        settings.Save();

        static string? Trimmed(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        // Index 0 is the "Windows default" entry, so the device list is offset by one.
        static string? SelectedDeviceId(ComboBox box, List<AudioDeviceInfo> devices) =>
            box.SelectedIndex > 0 && box.SelectedIndex - 1 < devices.Count
                ? devices[box.SelectedIndex - 1].Id
                : null;
    }

    private void SaveKeys()
    {
        // An empty box means "keep what is stored", not "delete the key" — otherwise opening the
        // window and pressing Save would wipe both keys.
        if (GroqKey.Password is { Length: > 0 } groq) _host.Secrets.Set(SecretStore.GroqApiKey, groq);
        if (GeminiKey.Password is { Length: > 0 } gemini) _host.Secrets.Set(SecretStore.GeminiApiKey, gemini);

        _host.Secrets.Set(SecretStore.GeminiModel, GeminiModel.Text);
        _host.Secrets.Set(SecretStore.SttModel, SttModel.Text);

        if (!_host.Secrets.Save()) Warn("Could not write the encrypted key store.");
    }

    private void SaveOverlay()
    {
        var overlay = _host.Overlay;

        overlay.FontSize = ParseDouble(OverlayFontSize.Text, overlay.FontSize, 10, 48);
        overlay.Width = ParseDouble(OverlayWidth.Text, overlay.Width, 240, 1400);
        overlay.MaxHeight = ParseDouble(OverlayMaxHeight.Text, overlay.MaxHeight, 120, 2000);

        if (OverlayPreset.SelectedIndex >= 0 && OverlayPreset.SelectedIndex != overlay.PresetIndex)
            _host.ApplyOverlayPreset(OverlayPreset.SelectedIndex);

        overlay.Save();
    }

    private void SaveHotkeys()
    {
        var rejected = new List<string>();

        foreach (var row in _hotkeys)
        {
            var combo = row.Combo.Trim();

            // A rejected combination leaves the old one in place. Accepting it would unbind the
            // action, and an action that silently stops working mid-meeting is the failure
            // FR-8.1 exists to prevent.
            if (!HotkeyCombo.TryParse(combo, out _, out _))
            {
                rejected.Add($"{row.Action} (“{combo}”)");
                continue;
            }

            _host.Hotkeys.Bindings[row.Kind.ToString()] = combo;
        }

        _host.Hotkeys.Save();
        _host.ApplyHotkeys();

        if (rejected.Count > 0)
            Warn("These are not valid combinations and were left unchanged:\n\n"
                 + string.Join("\n", rejected)
                 + "\n\nEvery combination needs at least one modifier, for example Ctrl+Alt+G.");
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double ParseDouble(string text, double fallback, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    // ------------------------------------------------------------------ chrome

    private bool _closing;

    /// <summary>The dialog on screen, if any, so panic hide can take it down too.</summary>
    private Window? _dialog;

    /// <summary>Lets the window actually close: only quitting the app does.</summary>
    public void AllowClose() => _closing = true;

    /// <summary>
    /// Panic hide (FR-8.4): instantaneous and unconditional. An open dialog is cancelled rather
    /// than left on screen waiting for an answer. Nothing typed is lost; the window is hidden,
    /// not closed.
    /// </summary>
    public void PanicHide()
    {
        _dialog?.Close();
        Hide();
    }

    /// <summary>The close button hides to the tray: the app keeps running, and so do its hotkeys.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Palette lookup. Application-scoped rather than window-scoped because the dialogs this
    /// window builds in code have their own logical tree and would find nothing here.
    /// </summary>
    private static Brush Palette(string key) => (Brush)Application.Current.FindResource(key);

    private void Status(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = Palette("Muted");
    }

    /// <summary>
    /// A problem the user needs to see, shown in the status bar rather than a message box.
    ///
    /// <c>MessageBox</c> would open a top-level window of its own, and a top-level window is
    /// exactly what cannot be capture-excluded after the fact — a dialog reading "MeetingAssist"
    /// would appear in a screen share, which is the one thing this app promises will not happen.
    /// A modal box would also swallow panic hide until it was dismissed.
    /// </summary>
    private void Warn(string message)
    {
        // The status bar is one line; a multi-line warning would simply be clipped.
        StatusText.Text = string.Join(" ", message.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        StatusText.Foreground = Palette("Warn");
        Log.Warning("Main window: {Message}", message);
    }

    private bool Confirm(string message, string title) => Ask(message, title, input: null);

    /// <summary>A one-line text prompt, or null if it was cancelled or left empty.</summary>
    private string? Prompt(string message, string title)
    {
        var box = new TextBox { Margin = new Thickness(0, 10, 0, 14), MinWidth = 340 };
        return Ask(message, title, box) ? Trimmed(box.Text) : null;

        static string? Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// The one modal this window uses, for both confirmations and the name prompt. Hand-built
    /// rather than <c>MessageBox</c> for a single reason: it gets the same capture exclusion as
    /// its parent, so nothing this app puts on screen is ever visible to a screen share.
    ///
    /// Returns true when accepted, false when cancelled.
    /// </summary>
    private bool Ask(string message, string title, TextBox? input)
    {
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Background,
            MaxWidth = 520,
            // A code-created window does not inherit the owner's theme, so without this its
            // buttons and text box render in light chrome on the dark background above.
            // ThemeMode is still marked experimental; the XAML form of it is already relied on
            // by MainWindow.xaml and ProbeWindow.xaml, so the API is load-bearing either way.
#pragma warning disable WPF0001
            ThemeMode = ThemeMode.Dark
#pragma warning restore WPF0001
        };

        dialog.SourceInitialized += (_, _) =>
        {
            var (excluded, detail) = WindowInterop.TryExcludeFromCapture(WindowInterop.HandleOf(dialog));
            if (!excluded) Log.Error("Dialog is NOT hidden from screen sharing: {Detail}", detail);
        };

        ok.Click += (_, _) => dialog.DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, input is null ? 16 : 0),
            Foreground = Palette("Ink")
        });

        if (input is not null) panel.Children.Add(input);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        // Before the window is shown there is nothing to focus; Loaded is the first moment the
        // caret can actually land in the box.
        if (input is not null) dialog.Loaded += (_, _) => input.Focus();

        _dialog = dialog;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally
        {
            _dialog = null;
        }
    }
}
