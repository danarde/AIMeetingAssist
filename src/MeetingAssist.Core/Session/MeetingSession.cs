using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Persistence;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Transcription;
using Serilog;

namespace MeetingAssist.Core.Session;

public enum SessionState { Idle, Recording, Paused, Thinking, Degraded, Error }

/// <summary>
/// Everything a live meeting needs, with no dependency on WPF.
///
/// The orchestration lives here rather than in the window so it can be exercised from fixtures
/// and unit tests — the overlay is the one part of the app that cannot be tested automatically,
/// so it gets as little logic as possible.
/// </summary>
public sealed class MeetingSession : IDisposable
{
    private readonly ContextProfile _profile;
    private readonly ITranscriber _transcriber;
    private readonly IAssistant? _assistant;
    private readonly TranscriptionOptions _sttOptions;
    private readonly SegmenterOptions _segmenterOptions;
    private readonly Func<AudioChannelKind, TimeBase, IAudioSource> _sourceFactory;

    /// <summary>
    /// Captured once at construction, never re-read per Ask. FR-6.5/D14 require the system block
    /// to be byte-identical across the requests of one session for implicit caching to work, so
    /// this must not be able to change underneath a running meeting.
    /// </summary>
    private readonly string? _styleTemplate;

    private readonly List<ChannelPipeline> _pipelines = [];
    private readonly Lock _pipelineGate = new();
    private readonly HashSet<AudioChannelKind> _unhealthyChannels = [];

    /// <summary>Between <see cref="Start"/> and <see cref="StopAsync"/>, paused or not.</summary>
    private bool _active;
    private bool _paused;

    /// <summary>Kept across a pause, so segments after it sort after the ones before it.</summary>
    private TimeBase? _timeBase;

    /// <summary>The pause in progress, so a resume or stop pressed during it waits for it.</summary>
    private Task _pausing = Task.CompletedTask;

    /// <summary>Non-null while capturing; cancelled by a pause or a stop.</summary>
    private CancellationTokenSource? _capture;

    /// <summary>
    /// Cancelled only once the backlog has had <see cref="DrainTimeout"/> to clear after a pause
    /// or stop, so what was heard before it is still transcribed.
    /// </summary>
    private CancellationTokenSource? _transcription;

    /// <summary>Watches how far behind the transcript is, for the life of the meeting.</summary>
    private CancellationTokenSource? _monitor;

    /// <summary>The transcript is further behind than <see cref="LagThreshold"/>.</summary>
    private bool _behind;
    private Task[] _running = [];
    private SessionState _state = SessionState.Idle;
    private int _sttFailures;
    private bool _thinking;

    public MeetingSession(
        ContextProfile profile,
        ITranscriber transcriber,
        IAssistant? assistant,
        TranscriptionOptions sttOptions,
        SegmenterOptions segmenterOptions,
        Func<AudioChannelKind, TimeBase, IAudioSource>? sourceFactory = null)
    {
        _profile = profile;
        _transcriber = transcriber;
        _assistant = assistant;
        _sttOptions = sttOptions;
        _segmenterOptions = segmenterOptions;

        // Blank means the built-in default, so an empty box in the settings window is a valid
        // and recoverable state rather than an empty prompt.
        _styleTemplate = string.IsNullOrWhiteSpace(profile.AnswerStyle) ? null : profile.AnswerStyle;

        // Injectable so tests and the WAV playback mode (FR-12.x) drive the same code path
        // the live app does.
        _sourceFactory = sourceFactory
            ?? ((channel, timeBase) => new WasapiAudioSource(channel, timeBase));

        // Subscribed once, here rather than per-Start, so repeated sessions cannot end up with
        // a segment written twice. The handler reads the *current* session id at call time.
        Transcript.SegmentAdded += PersistSegment;
        _transcriber.AttemptCompleted += OnTranscriptionAttempt;
    }

    public TranscriptStore Transcript { get; } = new();

    /// <summary>The profile this session was built with; fixed for its life (see <see cref="_styleTemplate"/>).</summary>
    public ContextProfile Profile => _profile;

    /// <summary>
    /// Durable transcript storage (FR-5.4), or null to keep the meeting in memory only.
    /// The session never owns it: one database outlives many sessions, so disposal is the
    /// caller's.
    /// </summary>
    public SessionRepository? Repository { get; init; }

    /// <summary>
    /// What this run is, stored with it so History can tell a rehearsal from a real meeting.
    /// Set by whoever wires the audio, since only they know where it comes from.
    /// </summary>
    public MeetingKind Kind { get; set; } = MeetingKind.Meeting;

    /// <summary>
    /// Identifies the current meeting. <see cref="Guid.Empty"/> until the first
    /// <see cref="Start"/>, and a fresh value on every one after that.
    /// </summary>
    public Guid SessionId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Flush timeout for the Ask path. Measured STT p95 is 587 ms (findings, §5.1).</summary>
    public TimeSpan FlushTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long <see cref="StopAsync"/> waits for queued transcript writes to land.</summary>
    public TimeSpan StorageFlushTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many times a failed capture channel is rebuilt before it is given up on (FR-2.6).
    /// A device that comes back — a headset replugged, a driver restarted — must not require
    /// the user to notice, stop the meeting and start it again.
    /// </summary>
    public int DeviceRetryAttempts { get; init; } = 6;

    /// <summary>First backoff delay; each attempt doubles it, capped at 15 s.</summary>
    public TimeSpan DeviceRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a rebuilt channel must run without failing before it counts as recovered.
    /// Device acquisition fails almost immediately when it fails at all, so this only has to
    /// outlast that.
    /// </summary>
    public TimeSpan DeviceSettleTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Consecutive provider failures before the session reports itself degraded. Above one, so
    /// a single dropped chunk on a flaky connection does not flicker the indicator; low enough
    /// that a real outage shows up within seconds at 2 s chunks on two channels.
    /// </summary>
    public int TranscriptionFailureThreshold { get; init; } = 4;

    /// <summary>
    /// How far behind the transcript may fall before the session reports itself degraded and
    /// an Ask says so. Normally a segment is transcribed within a second of being spoken; ten
    /// is well clear of that, and well short of the minutes a silent backlog reached in a real
    /// meeting (backlog §5).
    /// </summary>
    public TimeSpan LagThreshold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the lag is checked, and the indicator's figure for it updated.</summary>
    public TimeSpan LagCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a pause or stop waits for what was already heard to be transcribed. Normally
    /// that is under a second; this bounds the wait when the provider is slow or throttled.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Segments the last pause or stop gave up on after <see cref="DrainTimeout"/>.</summary>
    public int LastAbandoned { get; private set; }

    /// <summary>
    /// The loudest sound on each channel since the last call (RMS, 0–1), for the overlay's
    /// level meter. Zero for a channel that is not capturing.
    /// </summary>
    public (float Mic, float Loopback) TakeLevels()
    {
        float mic = 0, loopback = 0;
        foreach (var pipeline in Pipelines())
        {
            var peak = pipeline.TakePeak();
            if (pipeline.Channel == AudioChannelKind.Mic) mic = peak;
            else loopback = peak;
        }
        return (mic, loopback);
    }

    /// <summary>What is waiting to be transcribed, across both channels.</summary>
    public (int Count, TimeSpan OldestAge) Backlog
    {
        get
        {
            var count = 0;
            var oldest = TimeSpan.Zero;
            foreach (var pipeline in Pipelines())
            {
                var (n, age) = pipeline.Backlog;
                count += n;
                if (age > oldest) oldest = age;
            }
            return (count, oldest);
        }
    }

    /// <summary>
    /// Why the session is degraded, when it is because the transcript is behind: e.g.
    /// "transcript 2 min behind". Null otherwise.
    /// </summary>
    public string? LagDetail => _behind && Backlog is { Count: > 0 } backlog
        ? $"transcript {Describe(backlog.OldestAge)} behind"
        : null;

    /// <summary>
    /// Raised every <see cref="LagCheckInterval"/> while the transcript is behind, and once when
    /// it catches up, so the indicator's figure stays current.
    /// </summary>
    public event Action? LagChanged;

    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// True when both capture channels are running and the transcription provider is answering.
    /// This is what the overlay's indicator means; anything else is <see cref="SessionState.Degraded"/>.
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            lock (_pipelineGate)
                return _unhealthyChannels.Count == 0
                       && Volatile.Read(ref _sttFailures) < TranscriptionFailureThreshold
                       && !_behind;
        }
    }

    /// <summary>
    /// The single place the state is decided, so health changes and Ask can never fight over it.
    /// Ask sets a flag rather than saving and restoring a previous value — that dance is what
    /// used to leave the indicator pinned on Thinking after a failed Ask.
    /// </summary>
    private void RefreshState()
    {
        if (!IsRunning) { State = SessionState.Idle; return; }
        if (_thinking) { State = SessionState.Thinking; return; }
        if (_paused) { State = SessionState.Paused; return; }

        State = IsHealthy ? SessionState.Recording : SessionState.Degraded;
    }

    private void OnTranscriptionAttempt(bool ok)
    {
        var before = Volatile.Read(ref _sttFailures) >= TranscriptionFailureThreshold;

        if (ok) Interlocked.Exchange(ref _sttFailures, 0);
        else Interlocked.Increment(ref _sttFailures);

        var after = Volatile.Read(ref _sttFailures) >= TranscriptionFailureThreshold;

        if (before == after) return;

        if (after)
            Log.Error("Transcription has failed {Count} times in a row; the session is degraded",
                Volatile.Read(ref _sttFailures));
        else
            Log.Information("Transcription recovered");

        RefreshState();
    }

    /// <summary>A meeting is in progress: started and not stopped, whether capturing or paused.</summary>
    public bool IsRunning => _active;

    /// <summary>In progress but not listening; the transcript is kept for when it resumes.</summary>
    public bool IsPaused => _paused;

    public event Action<SessionState>? StateChanged;
    public event Action<TranscriptSegment>? SegmentAdded
    {
        add => Transcript.SegmentAdded += value;
        remove => Transcript.SegmentAdded -= value;
    }

    public void Start()
    {
        if (IsRunning) return;

        // A new meeting starts from nothing. Without this, a second session in the same app run
        // inherits the first one's transcript, and the previous customer's conversation is sent
        // to the model as context for this one.
        Transcript.Clear();
        SessionId = Guid.NewGuid();
        StartedAt = DateTimeOffset.UtcNow;
        Repository?.StartSession(SessionId, _profile.Name, StartedAt, Kind);

        _active = true;
        _paused = false;
        _thinking = false;
        _behind = false;
        _timeBase = new TimeBase();
        StartCapture();

        _monitor = new CancellationTokenSource();
        _ = MonitorLagAsync(_monitor.Token);

        RefreshState();
        Log.Information("Session {SessionId} started", SessionId);
    }

    /// <summary>
    /// Stops listening without ending the meeting: the transcript, the session id and the
    /// stored record carry on when it resumes. Anything audible while the user is away — the
    /// room, a notification, a video — would otherwise be transcribed into the context.
    ///
    /// What was still being said is flushed first, as an Ask does, so the sentence before the
    /// pause is not lost to the cancellation.
    /// </summary>
    public Task PauseAsync()
    {
        if (!_active || _paused) return _pausing;
        _paused = true;
        RefreshState();
        return _pausing = PauseCoreAsync();
    }

    private async Task PauseCoreAsync()
    {
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Flush before pausing failed; the last words may be missing");
        }

        await StopCaptureAsync().ConfigureAwait(false);
        RefreshState();
        Log.Information("Session {SessionId} paused after {Segments} segments", SessionId, Transcript.Count);
    }

    /// <summary>Listens again, adding to the same transcript.</summary>
    public async Task ResumeAsync()
    {
        await _pausing.ConfigureAwait(false);
        if (!_active || !_paused) return;
        _paused = false;

        StartCapture();
        RefreshState();
        Log.Information("Session {SessionId} resumed", SessionId);
    }

    private void StartCapture()
    {
        _capture = new CancellationTokenSource();
        _transcription = new CancellationTokenSource();

        lock (_pipelineGate)
        {
            _pipelines.Clear();
            _unhealthyChannels.Clear();
        }

        Interlocked.Exchange(ref _sttFailures, 0);

        var channels = new[] { AudioChannelKind.Mic, AudioChannelKind.Loopback };
        _running = [.. channels.Select(c => WatchAsync(c, _timeBase!, _capture.Token, _transcription.Token))];
    }

    /// <summary>
    /// Stops listening, then gives what was already heard <see cref="DrainTimeout"/> to be
    /// transcribed. Cancelling it all at once lost the last 15-20 minutes of a real meeting's
    /// saved transcript: everything still queued behind the free plan's rate limit.
    /// </summary>
    private async Task StopCaptureAsync()
    {
        var capture = _capture;
        var transcription = _transcription;
        if (capture is null) return;
        _capture = null;
        _transcription = null;
        LastAbandoned = 0;

        await capture.CancelAsync().ConfigureAwait(false);
        var running = Task.WhenAll(_running);
        try
        {
            try
            {
                await running.WaitAsync(DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LastAbandoned = Backlog.Count;
                Log.Warning("{Count} segment(s) were still waiting to be transcribed after {Timeout}; "
                            + "they are missing from the transcript", LastAbandoned, DrainTimeout);
                await transcription!.CancelAsync().ConfigureAwait(false);
                await running.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Pipelines faulted during stop");
        }
        finally
        {
            capture.Dispose();
            transcription?.Dispose();
            _running = [];

            // A stopped pipeline has nothing to flush, so an Ask while paused must not wait on it.
            lock (_pipelineGate) _pipelines.Clear();
        }
    }

    /// <summary>Snapshot, so a rebuild mid-Ask cannot mutate the list being iterated.</summary>
    private ChannelPipeline[] Pipelines()
    {
        lock (_pipelineGate) return [.. _pipelines];
    }

    private ChannelPipeline SwapIn(AudioChannelKind channel, TimeBase timeBase)
    {
        var pipeline = new ChannelPipeline(
            _sourceFactory(channel, timeBase),
            new Segmenter(channel, _segmenterOptions.Clone()),
            _transcriber, _sttOptions, Transcript);

        lock (_pipelineGate)
        {
            _pipelines.RemoveAll(p => p.Channel == channel);
            _pipelines.Add(pipeline);
        }

        return pipeline;
    }

    private void MarkChannel(AudioChannelKind channel, bool healthy)
    {
        bool changed;
        lock (_pipelineGate)
            changed = healthy ? _unhealthyChannels.Remove(channel) : _unhealthyChannels.Add(channel);

        if (changed) RefreshState();
    }

    /// <summary>
    /// Writes each segment as it finalizes (FR-5.4), so a crash mid-meeting costs nothing.
    /// The call only queues — the repository owns the disk I/O and keeps it off this path.
    /// </summary>
    private void PersistSegment(TranscriptSegment segment)
    {
        if (SessionId != Guid.Empty) Repository?.RecordSegment(SessionId, segment);
    }

    /// <summary>
    /// Runs one capture channel for the life of the session, rebuilding it when it fails
    /// (FR-2.6).
    ///
    /// A channel dying must not take the session with it: one microphone failing while the far
    /// end still transcribes is degraded, not broken, and the user is mid-meeting. Equally, a
    /// device that comes back — a headset replugged, a driver restarted, Windows switching the
    /// default — must not require the user to notice and restart the session, which is exactly
    /// what a pinned device (FR-2.5) makes more likely.
    ///
    /// The failure is reported immediately and the retrying happens behind it, so the indicator
    /// never claims health the session does not have.
    /// </summary>
    private async Task WatchAsync(
        AudioChannelKind channel, TimeBase timeBase, CancellationToken ct, CancellationToken transcription)
    {
        var delay = DeviceRetryDelay;

        for (var attempt = 0; attempt <= DeviceRetryAttempts && !ct.IsCancellationRequested; attempt++)
        {
            ChannelPipeline pipeline;
            try
            {
                pipeline = SwapIn(channel, timeBase);
            }
            catch (Exception ex)
            {
                // Constructing the source itself failed, which counts as a failed attempt.
                Log.Error(ex, "Channel {Channel} could not be created", channel);
                MarkChannel(channel, healthy: false);

                var next = await WaitBeforeRetryAsync(channel, attempt, delay, ct).ConfigureAwait(false);
                if (next is null) return;
                delay = next.Value;
                continue;
            }

            try
            {
                var run = pipeline.RunAsync(ct, transcription);

                // A capture device that is going to fail fails almost at once, so surviving the
                // settle window is what distinguishes a recovery from a retry loop.
                if (await Task.WhenAny(run, Task.Delay(DeviceSettleTime, ct)).ConfigureAwait(false) != run)
                {
                    if (attempt > 0) Log.Information("Channel {Channel} recovered", channel);
                    MarkChannel(channel, healthy: true);
                    delay = DeviceRetryDelay;
                }

                await run.ConfigureAwait(false);

                // The source ran to completion — a WAV fixture finishing, not a failure.
                MarkChannel(channel, healthy: true);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // Expected on stop.
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Channel {Channel} failed (attempt {Attempt})", channel, attempt + 1);
                MarkChannel(channel, healthy: false);

                var next = await WaitBeforeRetryAsync(channel, attempt, delay, ct).ConfigureAwait(false);
                if (next is null) return;
                delay = next.Value;
            }
        }
    }

    /// <summary>
    /// Waits out the backoff and returns the next delay to use, or null when the channel should
    /// be given up on. A return value rather than a ref parameter because async methods cannot
    /// take one.
    /// </summary>
    private async Task<TimeSpan?> WaitBeforeRetryAsync(
        AudioChannelKind channel, int attempt, TimeSpan delay, CancellationToken ct)
    {
        if (attempt >= DeviceRetryAttempts)
        {
            Log.Error("Channel {Channel} gave up after {Attempts} attempts; it will not recover "
                      + "without restarting the session", channel, DeviceRetryAttempts + 1);
            return null;
        }

        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var doubled = delay * 2;
        return doubled > MaxDeviceRetryDelay ? MaxDeviceRetryDelay : doubled;
    }

    private static readonly TimeSpan MaxDeviceRetryDelay = TimeSpan.FromSeconds(15);

    public async Task StopAsync()
    {
        if (!_active) return;
        _active = false;
        _paused = false;

        try
        {
            await _pausing.ConfigureAwait(false);
            await StopCaptureAsync().ConfigureAwait(false);
        }
        finally
        {
            _monitor?.Cancel();
            _monitor?.Dispose();
            _monitor = null;
            _behind = false;
            _thinking = false;
            RefreshState();

            if (Repository is { } repository && SessionId != Guid.Empty)
            {
                repository.EndSession(SessionId, DateTimeOffset.UtcNow);

                // Bounded: the transcript is already queued, so a slow disk must delay the stop
                // rather than block it. Anything still queued is written by the writer anyway.
                try
                {
                    await repository.FlushAsync().WaitAsync(StorageFlushTimeout).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Transcript storage did not settle within {Timeout}", StorageFlushTimeout);
                }
            }

            Log.Information("Session {SessionId} stopped after {Segments} segments",
                SessionId, Transcript.Count);
        }
    }

    /// <summary>
    /// The Ask path. Flushes both segmenters first so the sentence that was still being spoken
    /// when the hotkey was pressed is in the transcript — verified in live conversation, and the
    /// single most load-bearing behaviour in the design (D7).
    /// </summary>
    public async IAsyncEnumerable<string> AskAsync(
        TimeSpan window,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_assistant is null)
        {
            yield return "- Assistant is not configured — check the Gemini API key";
            yield break;
        }

        var timer = new StageTimer("ask");
        _thinking = true;
        RefreshState();

        IReadOnlyList<TranscriptSegment> history = [];
        IReadOnlyList<TranscriptSegment> focus = [];
        var prepared = false;
        try
        {
            await FlushAsync(ct).ConfigureAwait(false);
            timer.Mark("flushDrained");

            history = Transcript.All();
            focus = Transcript.GetWindow(window);
            timer.Mark("promptBuilt");
            prepared = true;
        }
        catch (Exception ex)
        {
            // C# forbids yielding from a catch, so the failure is recorded and reported below.
            Log.Error(ex, "Ask failed before reaching the assistant");
            _thinking = false;
            State = SessionState.Error;
        }

        if (!prepared)
        {
            yield return "- Could not prepare the transcript — press Ask again";
            yield break;
        }

        // Answering on old text without saying so is how a real meeting got twenty minutes of
        // notes about a topic long finished (backlog §5). The notes still come, as the newest
        // speech is transcribed first, but the user knows what they may be missing.
        var backlog = Backlog;
        var lagNote = backlog.Count > 0 && backlog.OldestAge >= LagThreshold
            ? $"- Note: {backlog.Count} line(s) from the last {Describe(backlog.OldestAge)} "
              + "are not transcribed yet; these notes may miss them\n"
            : null;

        if (focus.Count == 0)
        {
            _thinking = false;
            RefreshState();
            yield return _paused
                ? "- Nothing was transcribed before the pause"
                : "- Nothing transcribed yet — is the session recording?";
            yield break;
        }

        // try/finally, not a trailing assignment: a superseded Ask is cancelled mid-stream and
        // a network failure throws, and either would otherwise leave the status indicator stuck
        // on Thinking for the rest of the meeting — the exact untrustworthiness FR-7.6 exists
        // to prevent. RefreshState then reports whatever the session's actual health is, which
        // may have changed to Degraded while the Ask was in flight.
        if (lagNote is not null)
        {
            Log.Warning("Ask with {Count} segment(s) untranscribed, oldest {Age}", backlog.Count, backlog.OldestAge);
            yield return lagNote;
        }

        try
        {
            var first = true;
            await foreach (var chunk in _assistant
                .AskAsync(new AssistRequest(_profile, history, focus, _styleTemplate), ct)
                .ConfigureAwait(false))
            {
                if (first) { timer.Mark("firstToken"); first = false; }
                yield return chunk;
            }

            timer.Mark("lastToken");
            Log.Information("{Timing}", timer.Format());
        }
        finally
        {
            _thinking = false;
            RefreshState();
        }
    }

    /// <summary>
    /// The whole transcript, after flushing whatever is still being spoken into it — the same
    /// first step as an Ask, for anything else that has to react to the latest words (the mock
    /// counterpart, backlog §3).
    /// </summary>
    public async Task<IReadOnlyList<TranscriptSegment>> SettleTranscriptAsync(CancellationToken ct)
    {
        await FlushAsync(ct).ConfigureAwait(false);
        return Transcript.All();
    }

    private Task FlushAsync(CancellationToken ct) =>
        Task.WhenAll(Pipelines().Select(p => p.FlushAndDrainAsync(FlushTimeout, ct)));

    /// <summary>
    /// Checks the backlog every <see cref="LagCheckInterval"/>. A transcript that falls behind
    /// does so silently otherwise: the indicator stayed on Recording for a whole meeting while
    /// the transcript slipped twenty minutes behind it.
    /// </summary>
    private async Task MonitorLagAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(LagCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var backlog = Backlog;
                var behind = backlog.Count > 0 && backlog.OldestAge >= LagThreshold;

                if (behind != _behind)
                {
                    _behind = behind;
                    if (behind)
                        Log.Warning("Transcript is behind: {Count} segment(s) waiting, oldest {Age}",
                            backlog.Count, backlog.OldestAge);
                    else
                        Log.Information("Transcript caught up");
                    RefreshState();
                    LagChanged?.Invoke();
                }
                else if (behind)
                {
                    LagChanged?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) { /* the meeting ended */ }
        catch (Exception ex)
        {
            Log.Error(ex, "Lag monitor failed; a transcript falling behind will not be reported");
        }
    }

    /// <summary>
    /// "8 s", "2 min": how a lag reads in the indicator and in an Ask's note. Rounded down, so
    /// it never claims more lag than there is.
    /// </summary>
    public static string Describe(TimeSpan age) =>
        age < TimeSpan.FromSeconds(90)
            ? $"{Math.Max(1, (int)age.TotalSeconds)} s"
            : $"{(int)age.TotalMinutes} min";

    /// <summary>
    /// Pays the TLS handshake and connection setup before the user needs them. Cold start costs
    /// ~1.5 s and would otherwise land on the first Ask of a meeting — the worst moment
    /// (findings; spec FR-6.12).
    /// </summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        if (_assistant is null) return;

        try
        {
            var probe = new TranscriptSegment(
                Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(1),
                "Hello.", SegmentCutReason.Silence);

            await foreach (var _ in _assistant
                // Warms with the same system block a real Ask will send, so the cached prefix
                // is the one that gets reused.
                .AskAsync(new AssistRequest(_profile, [probe], [probe], _styleTemplate), ct)
                .ConfigureAwait(false))
            {
                break; // One token is enough to establish the connection.
            }
            Log.Debug("Assistant warmed");
        }
        catch (Exception ex)
        {
            // Warming is an optimisation. Failing it must never prevent a session starting.
            Log.Debug(ex, "Warm-up failed; first Ask will pay the cold start");
        }
    }

    public void Dispose()
    {
        Transcript.SegmentAdded -= PersistSegment;
        _transcriber.AttemptCompleted -= OnTranscriptionAttempt;
        _monitor?.Cancel();
        _monitor?.Dispose();
        _monitor = null;
        _capture?.Cancel();
        _capture?.Dispose();
        _capture = null;
        _transcription?.Cancel();
        _transcription?.Dispose();
        _transcription = null;
        (_transcriber as IDisposable)?.Dispose();
        (_assistant as IDisposable)?.Dispose();
    }
}
