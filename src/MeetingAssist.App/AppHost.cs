using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MeetingAssist.App.Interop;
using MeetingAssist.App.Main;
using MeetingAssist.App.Overlay;
using MeetingAssist.App.Settings;
using MeetingAssist.App.Tray;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Configuration;
using MeetingAssist.Core.Mock;
using MeetingAssist.Core.Persistence;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using Serilog;

namespace MeetingAssist.App;

/// <summary>
/// Wires the main window, the overlay, the tray icon, the hotkeys and the session together.
/// Holds no meeting logic of its own — that lives in <see cref="MeetingSession"/> where it can
/// be tested.
///
/// Between meetings the main window is up and the overlay hidden; during one it is the other
/// way round.
/// </summary>
public sealed class AppHost : IMainHost, IDisposable
{
    private readonly OverlayWindow _overlay;
    private readonly OverlaySettings _overlaySettings;
    private readonly HotkeySettings _hotkeySettings = HotkeySettings.Load();
    private readonly SessionRepository _repository;
    private readonly TrayIcon _tray;
    private readonly MainWindow _main;

    private MeetingSession _session;

    /// <summary>The AI playing the other party, or null outside a rehearsal or when it is unavailable.</summary>
    private MockCall? _mock;

    /// <summary>The session is built for a rehearsal: chosen per meeting, on Home.</summary>
    private bool _rehearse;

    /// <summary>Why the last rehearsal could not get its other party, if it could not.</summary>
    private string? _mockProblem;

    /// <summary>Settings changed during a meeting, to be applied once it ends.</summary>
    private bool _reloadPending;

    /// <summary>
    /// Panic hide was pressed. The main window then stays down until it is opened on purpose,
    /// rather than reappearing by itself when the meeting ends.
    /// </summary>
    private bool _panicked;

    private HotkeyManager? _hotkeys;
    private CancellationTokenSource? _ask;
    private string[] _failedHotkeys = [];

    /// <summary>
    /// The session reads WAV files. Pause is refused then: a file source starts again from the
    /// beginning, so resuming would replay the meeting into the same transcript.
    /// </summary>
    private bool _playback;

    /// <summary>The hotkey hint, kept so a mock status line can be replaced by it again.</summary>
    private string _hint = "";

    /// <summary>Set by <see cref="BuildSession"/>, reported by <see cref="ReportProblems"/>.</summary>
    private List<string> _problems = [];

    public AppHost(AppConfig config)
    {
        Settings = AppSettings.Load();
        Profiles = new ProfileLibrary();
        Secrets = config.Secrets ?? SecretStore.Load();

        // The hand-written profile.json from before profiles were named is the one the user
        // actually tuned; adopt it rather than starting them on a sample.
        Profiles.ImportLegacy(config.ProfilePath);

        _overlaySettings = OverlaySettings.Load();
        _overlay = new OverlayWindow(_overlaySettings);

        // Opening never throws — an unwritable database costs the transcript, not the meeting.
        _repository = SessionRepository.Open();

        _tray = new TrayIcon();
        _tray.ToggleSession += () => _ = ToggleSessionAsync();
        _tray.TogglePause += () => _ = TogglePauseAsync();
        _tray.Open += OpenMain;
        _tray.ToggleOverlay += () => _overlay.ToggleVisible();
        _tray.Quit += () => _ = QuitAsync();

        _overlay.ActionRequested += OnHotkey;
        _overlay.SettingsRequested += OpenMain;

        _session = BuildSession();
        ReportProblems();

        _main = new MainWindow(this);
    }

    public AppSettings Settings { get; }
    public OverlaySettings Overlay => _overlaySettings;
    public HotkeySettings Hotkeys => _hotkeySettings;
    public SecretStore Secrets { get; }
    public ProfileLibrary Profiles { get; }

    public bool SessionRunning => _session.IsRunning;
    public SessionState State => _session.State;
    public bool Rehearsing => _session.IsRunning && _mock is not null;
    public IReadOnlyList<string> FailedHotkeys => _failedHotkeys;

    public IReadOnlyList<MeetingSummary> Meetings() => _repository.ReadMeetings();

    public string TranscriptFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetingAssist", "Transcripts");

    public async Task<string?> DeleteMeetingAsync(Guid meetingId)
    {
        // The window is hidden during a meeting, so this is a guard, not a feature.
        if (_session.IsRunning) return "A meeting is running. Delete it after it ends.";
        if (!_repository.Available) return $"The transcript database could not be opened ({_repository.Path}).";

        var failedBefore = _repository.FailedWrites;
        _repository.DeleteSession(meetingId);
        await _repository.FlushAsync();

        if (_repository.FailedWrites > failedBefore)
            return "The meeting could not be deleted. The log has the reason.";

        Log.Information("Deleted meeting {Meeting} from History", meetingId);
        return null;
    }

    public (string? Path, string? Problem) ExportTranscript(Guid meetingId)
    {
        var meeting = _repository.ReadMeetings().FirstOrDefault(m => m.Id == meetingId);
        if (meeting is null) return (null, "That meeting is no longer in the transcript database.");

        // The speakers are named as the profile names them now; the database keeps only which
        // channel each line came from.
        var profile = Profiles.Exists(meeting.Profile) ? Profiles.Load(meeting.Profile) : null;
        var text = TranscriptExport.Render(
            meeting, _repository.ReadSegments(meetingId),
            profile?.MicLabel ?? "You", profile?.LoopbackLabel ?? "Other side", TimeZoneInfo.Local);

        var path = Path.Combine(TranscriptFolder, TranscriptExport.FileName(meeting, TimeZoneInfo.Local));
        try
        {
            Directory.CreateDirectory(TranscriptFolder);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Log.Information("Exported the transcript of {Meeting} ({Lines} lines) to {Path}",
                meetingId, meeting.Lines, path);
            return (path, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not export the transcript of {Meeting} to {Path}", meetingId, path);
            return (null, $"Could not write {path}: {ex.Message}");
        }
    }

    public IReadOnlyList<HomeWarning> Warnings()
    {
        var config = AppConfig.From(Secrets);
        var notHidden = new List<string>();
        if (_overlay.NotHiddenReason is { } overlay) notHidden.Add($"The overlay ({overlay})");
        if (_main?.NotHiddenReason is { } main) notHidden.Add($"This window ({main})");

        return HomeWarnings.From(new WarningFacts
        {
            GroqKey = !string.IsNullOrWhiteSpace(config.GroqApiKey),
            GeminiKey = !string.IsNullOrWhiteSpace(config.GeminiApiKey),
            GroqPlan = Settings.GroqPlan,
            FailedHotkeys = _failedHotkeys,
            StorageAvailable = _repository.Available,
            StoragePath = _repository.Path,
            FailedWrites = _repository.FailedWrites,
            PlaintextKeysAt = Secrets.Migration is SecretMigration.Incomplete ? AppConfig.SecretsPath : null,
            PlaybackMode = Settings.PlaybackMode,
            PlaybackReady = Settings.PlaybackReady,
            NotHidden = notHidden
        });
    }

    public string StorageDescription => _repository.Available
        ? $"Transcripts are saved to {_repository.Path}."
          + (_repository.FailedWrites > 0
              ? $" {_repository.FailedWrites} write(s) failed — the saved transcript is incomplete."
              : "")
        : $"Transcripts are NOT being saved — could not open {_repository.Path}.";

    /// <summary>
    /// Builds a session from what is currently on disk. Called at startup and again whenever
    /// settings change while idle, so a profile, device or key change needs no restart.
    /// </summary>
    private MeetingSession BuildSession()
    {
        var profile = Profiles.Load(Settings.ProfileName);
        var problems = new List<string>();

        // Rebuilt from the store each time, so environment variables keep overriding it and the
        // precedence rules live in exactly one place rather than being restated here.
        var config = AppConfig.From(Secrets);

        var sttOptions = new TranscriptionOptions
        {
            ModelId = config.SttModel,
            Language = profile.Language,
            Vocabulary = profile.Vocabulary
        };

        // A missing key must not prevent the app starting: the user needs to see *why* the
        // overlay is degraded, and an app that refuses to launch cannot tell them.
        ITranscriber transcriber;
        IAssistant? assistant = null;

        try
        {
            transcriber = new GroqTranscriber(
                config.RequireGroqKey(), requestsPerMinute: GroqTranscriber.RequestsPerMinute(Settings.GroqPlan));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Transcription unavailable");
            problems.Add("no transcription (Groq key missing)");
            transcriber = new FakeTranscriber();
        }

        try
        {
            assistant = new GeminiAssistant(config.RequireGeminiKey(), config.GeminiModel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Assistant unavailable");
            problems.Add("no answers (Gemini key missing)");
        }

        _problems = problems;

        var session = new MeetingSession(
            profile, transcriber, assistant, sttOptions, new SegmenterOptions(), SourceFactory())
        {
            Repository = _repository
        };

        session.StateChanged += OnStateChanged;
        session.LagChanged += OnLagChanged;
        _playback = Settings.PlaybackReady;
        _mock = BuildMock(profile, config, session);
        session.Kind = _mock is not null ? MeetingKind.Rehearsal
            : _playback ? MeetingKind.Playback
            : MeetingKind.Meeting;
        return session;
    }

    /// <summary>
    /// A rehearsal (backlog §3): a second Gemini client plays the other party, heard through a
    /// Gemini or Windows voice on the default output — where the loopback channel picks it up
    /// like any caller. Its own client, so its thinking-level discovery and temperature never
    /// touch the cue-note path.
    /// </summary>
    private MockCall? BuildMock(ContextProfile profile, AppConfig config, MeetingSession session)
    {
        _mockProblem = null;
        if (!_rehearse) return null;

        if (Settings.PlaybackReady)
        {
            // The loopback channel is reading a WAV file, so the counterpart's voice would never
            // reach the transcript.
            _mockProblem = "the other party needs live audio, and playback mode is on";
            return null;
        }

        GeminiAssistant? completion = null;
        try
        {
            completion = new GeminiAssistant(config.RequireGeminiKey(), config.GeminiModel);
            var voice = Voices.Create(
                Settings.MockVoiceEngine, Settings.MockVoiceName, profile.Language, config.GeminiApiKey);

            if (voice is GeminiVoice gemini) gemini.FellBack += OnMockVoiceFellBack;

            var mock = new MockCall(profile, completion, voice, session.SettleTranscriptAsync);
            mock.StageChanged += OnMockStageChanged;
            Log.Information("Mock mode on: {Them} is played by {Model}, voice {Voice}",
                profile.LoopbackLabel, config.GeminiModel, voice.Description);
            return mock;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mock mode unavailable");
            completion?.Dispose();
            _mockProblem = "no one to play the other party (it needs the Gemini key and a voice)";
            return null;
        }
    }

    private void OnMockVoiceFellBack(string reason) => _overlay.Dispatcher.BeginInvoke(() =>
        _overlay.ShowHint($"Mock: Gemini voice unavailable ({reason}), Windows voice for this line"));

    private void OnMockStageChanged(MockStage stage) => _overlay.Dispatcher.BeginInvoke(() =>
        _overlay.ShowHint(stage switch
        {
            MockStage.Thinking => $"Mock: {_session.Profile.LoopbackLabel} is thinking…",
            MockStage.Speaking => $"Mock: {_session.Profile.LoopbackLabel} is speaking",
            _ => _hint
        }));

    /// <summary>
    /// Live capture, or WAV fixtures when playback mode is on and both files exist (FR-12.1).
    /// Returning null keeps <see cref="MeetingSession"/>'s own live default.
    /// </summary>
    private Func<AudioChannelKind, TimeBase, IAudioSource>? SourceFactory()
    {
        if (Settings.PlaybackReady)
        {
            var mic = Settings.MicWavPath!;
            var loopback = Settings.LoopbackWavPath!;
            var realTime = Settings.PlaybackRealTime;

            Log.Information("Playback mode: mic={Mic} loopback={Loopback} realTime={RealTime}",
                mic, loopback, realTime);

            return (channel, _) => new WavFileAudioSource(
                channel, channel == AudioChannelKind.Mic ? mic : loopback, realTime);
        }

        var micId = Settings.MicDeviceId;
        var loopbackId = Settings.LoopbackDeviceId;

        if (micId is null && loopbackId is null) return null;

        return (channel, timeBase) => new WasapiAudioSource(
            channel, timeBase, deviceId: channel == AudioChannelKind.Mic ? micId : loopbackId);
    }

    private void OnStateChanged(SessionState state) => _overlay.Dispatcher.Invoke(() =>
    {
        _overlay.ShowState(state, _session.LagDetail);
        RefreshControls();
    });

    /// <summary>The transcript is behind, or caught up: the status row says by how much.</summary>
    private void OnLagChanged() => _overlay.Dispatcher.BeginInvoke(() =>
        _overlay.ShowState(_session.State, _session.LagDetail));

    /// <summary>Everything that shows what the session is doing, or offers what it can do next.</summary>
    private void RefreshControls()
    {
        _overlay.ShowControls(_session.IsRunning, _session.IsPaused, canPause: !_playback, mock: _mock is not null);
        _tray.ShowState(_session.State, _session.IsRunning, _session.IsPaused);
        _main.ShowSessionState();
    }

    private void ReportProblems()
    {
        // The banner is one line and every problem has to fit in it, so the fix hint is attached
        // only to the problems it actually solves — a storage failure must not send the user to
        // the secrets file.
        var problems = new List<string>(_problems);
        var missingKeys = problems.Count > 0;

        if (!_repository.Available)
            problems.Add($"transcripts are not being saved (could not open {_repository.Path})");

        // The keys are safe either way, but a plaintext copy the user believes was removed is
        // exactly the kind of thing they must not find out about later.
        if (Secrets.Migration is SecretMigration.Incomplete)
            problems.Add($"keys are still in plaintext at {AppConfig.SecretsPath}");

        if (problems.Count > 0)
            _overlay.ShowWarning($"Degraded: {string.Join("; ", problems)}"
                + (missingKeys ? ". Add them in Settings" : ""));
    }

    public void Start()
    {
        // The overlay stays hidden until a meeting starts, but the hotkeys are delivered to its
        // window, so the window is created now without being shown.
        _hotkeys = new HotkeyManager(new WindowInteropHelper(_overlay).EnsureHandle());
        _hotkeys.Pressed += OnHotkey;

        ApplyHotkeys();

        _overlay.ShowState(_session.State);
        RefreshControls();

        // Polled rather than pushed: frames arrive every few milliseconds per channel, and the
        // eye needs only twenty updates a second.
        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _meterTimer.Tick += (_, _) =>
        {
            var (mic, loopback) = _session.TakeLevels();
            _overlay.ShowLevels(mic, loopback);
        };
        _meterTimer.Start();

        OpenMain();
    }

    private DispatcherTimer? _meterTimer;

    /// <summary>Re-registers every binding. Safe to call repeatedly; the manager clears first.</summary>
    public void ApplyHotkeys()
    {
        if (_hotkeys is null) return;

        _hotkeys.UnregisterAll();

        // The mock key is only claimed for a rehearsal, so it does not take a combination away
        // from other applications for a feature that is not in use.
        var bindings = _hotkeySettings.Resolve()
            .Where(b => b.Action != HotkeyAction.MockNext || _mock is not null)
            .ToArray();
        foreach (var binding in bindings) _hotkeys.Register(binding);

        _failedHotkeys = [.. _hotkeys.Failed.Select(f => f.Display)];

        if (_failedHotkeys.Length > 0)
        {
            // FR-8.1: a hotkey discovered dead during a live meeting is unacceptable, so the
            // failure is stated at startup with the exact combinations that did not bind, and
            // with where to change them — otherwise the user can see the problem but not fix it.
            _overlay.ShowWarning(
                $"Already used by another app: {string.Join(", ", _failedHotkeys)}. Rebind in Settings");
        }

        _hint = Hint(bindings);
        _overlay.ShowHint(_hint);
        _overlay.ShowBindings(bindings
            .Except(_hotkeys.Failed)
            .ToDictionary(b => b.Action, b => b.Display));

        _main.RefreshHome();
    }

    /// <summary>
    /// The keys worth stating on screen, using whatever they are actually bound to. A modifier
    /// prefix they all share is written once — "Ctrl+Alt + R start · S ask" — which is what
    /// lets the line fit the overlay's width.
    /// </summary>
    public static string Hint(IReadOnlyCollection<HotkeyBinding> bindings)
    {
        var shown = new[]
            {
                (HotkeyAction.ToggleSession, "start"),
                (HotkeyAction.AskNarrow, "ask"),
                (HotkeyAction.AskWide, "wide"),
                (HotkeyAction.TogglePause, "pause"),
                (HotkeyAction.MockNext, "next line"),
                (HotkeyAction.Quit, "quit")
            }
            .Select(s => (Binding: bindings.FirstOrDefault(b => b.Action == s.Item1), Label: s.Item2))
            .Where(s => s.Binding is not null)
            .Select(s => (Keys: s.Binding!.Display.Split('+', StringSplitOptions.TrimEntries), s.Label))
            .ToList();

        if (shown.Count == 0) return "";

        // The longest run of leading modifiers every binding shares, never the key itself.
        var prefix = shown.Select(s => s.Keys[..^1]).Aggregate((a, b) =>
            a.Zip(b).TakeWhile(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.First).ToArray());

        var items = shown.Select(s => $"{string.Join("+", s.Keys[prefix.Length..])} {s.Label}");
        var line = string.Join(" · ", items);
        return prefix.Length > 0 ? $"{string.Join("+", prefix)} + {line}" : line;
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleSession: _ = ToggleSessionAsync(); break;
            case HotkeyAction.TogglePause: _ = TogglePauseAsync(); break;
            case HotkeyAction.AskNarrow: _ = AskAsync(Settings.NarrowAsk); break;
            case HotkeyAction.AskWide: _ = AskAsync(Settings.WideAsk); break;
            case HotkeyAction.MockNext: _ = MockNextAsync(); break;
            case HotkeyAction.ToggleOverlay: _overlay.ToggleVisible(); break;
            case HotkeyAction.PanicHide: PanicHide(); break;
            case HotkeyAction.Quit: _ = QuitAsync(); break;
            case HotkeyAction.CyclePreset:
                var preset = _overlaySettings.Advance();
                _overlay.ApplyPreset(preset);
                _overlay.ShowHint(preset.Display);
                _overlaySettings.Save();
                break;
        }
    }

    /// <summary>
    /// FR-8.4. The main window shows the profile, the meeting notes and the answer style —
    /// more than the overlay ever does — so panic hide has to hide it too, not just the overlay.
    /// </summary>
    private void PanicHide()
    {
        _panicked = true;
        _main.PanicHide();
        _overlay.Panic();
    }

    /// <summary>The main window, asked for: from the tray, or the overlay's settings button.</summary>
    private void OpenMain()
    {
        _panicked = false;
        ShowMain();
    }

    private void ShowMain()
    {
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ApplyOverlayPreset(int index)
    {
        _overlaySettings.PresetIndex = index;
        _overlay.ApplyPreset(_overlaySettings.Current);
    }

    /// <summary>
    /// Rebuilds the session from what is now on disk. Deferred while one is running: swapping
    /// the profile mid-meeting would change the prompt prefix and break caching, and swapping
    /// the transcriber would drop audio already in flight.
    /// </summary>
    public void ReloadConfiguration()
    {
        if (_session.IsRunning)
        {
            _reloadPending = true;
            Log.Information("Configuration reload deferred until the session ends");
            return;
        }

        _reloadPending = false;

        var previous = _session;
        previous.StateChanged -= OnStateChanged;
        previous.LagChanged -= OnLagChanged;

        var previousMock = _mock;
        if (previousMock is not null) previousMock.StageChanged -= OnMockStageChanged;

        _session = BuildSession();
        previousMock?.Dispose();
        previous.Dispose();

        ReportProblems();
        _overlay.ShowState(_session.State);

        // A rehearsal claims the mock key and a meeting releases it.
        ApplyHotkeys();
        RefreshControls();
        Log.Information("Configuration reloaded; profile is {Profile}, rehearsal {Rehearsal}",
            Settings.ProfileName, _rehearse);
    }

    /// <summary>
    /// Stops the session before tearing down, so capture devices are released and the last
    /// segments are flushed rather than abandoned.
    /// </summary>
    private async Task QuitAsync()
    {
        try
        {
            if (_session.IsRunning) await _session.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Session did not stop cleanly during quit");
        }
        finally
        {
            _main.AllowClose();
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// The start/stop hotkey, the tray and the overlay's button. Starting from there is always
    /// a real meeting: a rehearsal is chosen on Home, where it is spelt out.
    /// </summary>
    private async Task ToggleSessionAsync()
    {
        if (_session.IsRunning)
        {
            await StopMeetingAsync();
            return;
        }

        if (StartMeeting(rehearse: false) is { } problem)
        {
            _main.ShowOutcome(problem, problem: true);
            if (!_panicked) ShowMain();
        }
    }

    public string? StartMeeting(bool rehearse)
    {
        if (_session.IsRunning) return null;

        try
        {
            if (rehearse != _rehearse || _reloadPending)
            {
                _rehearse = rehearse;
                ReloadConfiguration();
            }

            if (rehearse && _mock is null)
                return $"The rehearsal could not start: {_mockProblem ?? "no one to play the other party"}.";

            _session.Start();
            _main.Hide();
            _overlay.ShowWithoutActivating();
            _overlay.SetAnswer(_mock is null
                ? "Listening. Press Ask when you want cue notes."
                : $"Mock call. Press {Binding(HotkeyAction.MockNext)} for {_session.Profile.LoopbackLabel}'s next line, "
                  + "and Ask when you want cue notes.");

            // Absorb the ~1.5 s cold start now rather than on the user's first Ask.
            _ = _session.WarmAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start the session");
            return $"Could not start: {ex.Message}";
        }
        finally
        {
            RefreshControls();
        }
    }

    public async Task StopMeetingAsync()
    {
        if (!_session.IsRunning) return;

        try
        {
            _mock?.Cancel();
            _overlay.SetAnswer("Stopping: finishing the transcript…");
            _main.ShowOutcome("Stopping: finishing the transcript…", problem: false);
            await _session.StopAsync();

            // The list of past meetings reads the file, which does not yet hold queued writes.
            await _repository.FlushAsync();

            var abandoned = _session.LastAbandoned;
            _overlay.Hide();
            if (_reloadPending) ReloadConfiguration();

            _main.ShowOutcome("Meeting ended." + Abandoned(abandoned), problem: abandoned > 0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not stop the session");
            _main.ShowOutcome($"The meeting did not stop cleanly: {ex.Message}", problem: true);
        }
        finally
        {
            RefreshControls();
            if (!_panicked) ShowMain();
            _main.RefreshHome();
        }
    }

    /// <summary>What the last pause or stop could not transcribe in time, if anything.</summary>
    private string Abandoned() => Abandoned(_session.LastAbandoned);

    private static string Abandoned(int lines) => lines switch
    {
        0 => "",
        var n => $" {n} line(s) could not be transcribed in time and are missing from the transcript."
    };

    /// <summary>
    /// Stops listening while the user is away, keeping the conversation for when they are back.
    /// Ask still works while paused, on what was said before it.
    /// </summary>
    private async Task TogglePauseAsync()
    {
        if (!_session.IsRunning)
        {
            _overlay.ShowHint($"No session to pause. Start one first ({Binding(HotkeyAction.ToggleSession)})");
            return;
        }

        if (_playback)
        {
            _overlay.ShowHint("Pause is not available in playback mode");
            return;
        }

        try
        {
            if (_session.IsPaused)
            {
                await _session.ResumeAsync();
                _overlay.SetAnswer("Listening again. Everything said before the pause is still in context.");
            }
            else
            {
                _mock?.Cancel();
                _overlay.SetAnswer("Pausing: finishing the transcript…");
                await _session.PauseAsync();
                _overlay.SetAnswer(
                    $"Paused: not listening. The conversation so far is kept. Press Resume or "
                    + $"{Binding(HotkeyAction.TogglePause)} to carry on. Ask still works on what was said."
                    + Abandoned());
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not pause or resume the session");
            _overlay.ShowHint($"Could not pause or resume: {ex.Message}");
        }
        finally
        {
            RefreshControls();
        }
    }

    /// <summary>
    /// The counterpart's next line. Never automatic: a pause to think must not be talked over.
    /// A press during a line supersedes it, inside <see cref="MockCall"/>.
    /// </summary>
    private async Task MockNextAsync()
    {
        if (_mock is null)
        {
            _overlay.ShowHint("Not a rehearsal. Start one with Rehearse with mock call, on Home");
            return;
        }

        if (!_session.IsRunning)
        {
            _overlay.ShowHint($"Start the session first ({Binding(HotkeyAction.ToggleSession)})");
            return;
        }

        if (_session.IsPaused)
        {
            // Its voice would not be heard: nothing is listening.
            _overlay.ShowHint($"The session is paused. Resume first ({Binding(HotkeyAction.TogglePause)})");
            return;
        }

        try
        {
            if (await _mock.TakeTurnAsync(CancellationToken.None) is null)
                _overlay.ShowHint("Mock: no reply. Press again");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer press, or the session stopped.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mock turn failed");
            _overlay.ShowHint($"Mock: could not reply ({ex.Message}). Press again");
        }
    }

    private string Binding(HotkeyAction action) =>
        _hotkeySettings.Bindings.TryGetValue(action.ToString(), out var combo) ? combo : action.ToString();

    private async Task AskAsync(TimeSpan window)
    {
        // A second Ask supersedes the first: mid-meeting the newest question is the one that
        // matters, and two answers interleaving into one panel would be unreadable.
        //
        // The outgoing Ask disposes its own source in its finally, so this can race with it.
        // Losing that cancellation is harmless — the outgoing Ask is finishing anyway — but
        // letting the exception escape would swallow the new Ask entirely.
        var outgoing = _ask;
        if (outgoing is not null)
        {
            try { await outgoing.CancelAsync(); }
            catch (ObjectDisposedException) { /* already completed and cleaned up */ }
        }

        var cts = new CancellationTokenSource();
        _ask = cts;

        _overlay.ShowWithoutActivating();
        _overlay.SetAnswer("");

        try
        {
            await foreach (var chunk in _session.AskAsync(window, cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                _overlay.AppendAnswer(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Ask.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ask failed");
            _overlay.SetAnswer($"- Ask failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_ask, cts)) _ask = null;
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        _meterTimer?.Stop();
        _hotkeys?.Dispose();
        _ask?.Cancel();
        _ask?.Dispose();
        _mock?.Dispose();
        _session.Dispose();
        _tray.Dispose();

        // Blocking here is deliberate: the process is exiting, and abandoning the writer would
        // discard whatever is still queued. Nothing in the writer captures a synchronization
        // context, so this cannot deadlock on the UI thread.
        try
        {
            _repository.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Transcript storage did not close cleanly");
        }

        Settings.Save();
        _overlaySettings.Save();
    }
}
