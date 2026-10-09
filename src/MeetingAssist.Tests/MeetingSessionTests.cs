using System.Runtime.CompilerServices;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Persistence;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using static MeetingAssist.Tests.Doubles;

namespace MeetingAssist.Tests;

/// <summary>
/// The overlay cannot be tested automatically, so the session it drives carries the coverage.
/// Everything here runs on synthetic audio with no devices, no keys and no network.
/// </summary>
public class MeetingSessionTests
{
    private static MeetingSession Build(
        IAssistant? assistant = null,
        Func<AudioChannelKind, TimeBase, IAudioSource>? sources = null,
        ITranscriber? transcriber = null,
        SessionRepository? repository = null) =>
        new(ContextProfile.Sample(),
            transcriber ?? new FakeTranscriber(),
            assistant,
            new TranscriptionOptions(),
            new SegmenterOptions(),
            sources ?? ((channel, _) => new SilentSource(channel)))
        {
            FlushTimeout = TimeSpan.FromMilliseconds(200),
            Repository = repository
        };

    private static TranscriptSegment Spoken(string text, AudioChannelKind channel = AudioChannelKind.Loopback) =>
        new(Guid.NewGuid(), channel, TimeSpan.Zero, TimeSpan.FromSeconds(3), text, SegmentCutReason.Silence);

    [Fact]
    public async Task Starting_and_stopping_moves_through_the_expected_states()
    {
        using var session = Build();
        var seen = new List<SessionState>();
        session.StateChanged += seen.Add;

        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.IsRunning);

        session.Start();
        Assert.True(session.IsRunning);
        Assert.Equal(SessionState.Recording, session.State);

        await session.StopAsync();
        Assert.False(session.IsRunning);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal([SessionState.Recording, SessionState.Idle], seen);
    }

    [Fact]
    public async Task Starting_a_running_session_again_is_a_no_op()
    {
        // Start() guards on IsRunning, so a second call must change nothing observable — in
        // particular it must not hand out a fresh SessionId or reset the transcript underneath
        // a meeting that is already in progress.
        using var session = Build();
        session.Start();
        var id = session.SessionId;
        var startedAt = session.StartedAt;
        session.Transcript.Append(Spoken("already recorded"));

        session.Start();

        Assert.Equal(id, session.SessionId);
        Assert.Equal(startedAt, session.StartedAt);
        Assert.Equal(1, session.Transcript.Count);

        await session.StopAsync();
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Stopping_a_session_that_never_started_is_harmless()
    {
        using var session = Build();
        await session.StopAsync();
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Ask_without_an_assistant_says_so_rather_than_throwing()
    {
        // A blank overlay mid-meeting is the failure that actually hurts (spec FR-6.6).
        using var session = Build(assistant: null);
        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));

        Assert.Contains("not configured", text);
    }

    [Fact]
    public async Task Ask_with_an_empty_transcript_explains_itself()
    {
        using var session = Build(new StubAssistant("- should not be reached"));
        session.Start();

        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));

        Assert.Contains("Nothing transcribed", text);
        await session.StopAsync();
    }

    [Fact]
    public async Task Ask_streams_the_assistant_response_and_returns_to_recording()
    {
        var assistant = new StubAssistant("- first\n", "- second");
        using var session = Build(assistant);
        session.Start();

        // Seed the transcript directly: transcription itself is covered elsewhere, and this
        // isolates the Ask path from audio timing.
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(3),
            "What does the licence cost?", SegmentCutReason.Silence));

        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));

        Assert.Equal("- first\n- second", text);
        Assert.Equal(SessionState.Recording, session.State);
        await session.StopAsync();
    }

    [Fact]
    public async Task A_failing_assistant_leaves_the_session_usable()
    {
        using var session = Build(new ThrowingAssistant());
        session.Start();
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Mic, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "anything", SegmentCutReason.Silence));

        await Assert.ThrowsAnyAsync<Exception>(
            () => Collect(session.AskAsync(TimeSpan.FromSeconds(60), default)));

        // The session must still be running: one bad answer is not a reason to stop recording.
        Assert.True(session.IsRunning);
        await session.StopAsync();
    }

    [Fact]
    public async Task A_failing_Ask_does_not_leave_the_state_stuck_on_Thinking()
    {
        // Regression: State was set to Thinking up front and only restored on the success
        // path, so one network error pinned the status indicator for the rest of the meeting.
        using var session = Build(new ThrowingAssistant());
        session.Start();
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Mic, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "anything", SegmentCutReason.Silence));

        await Assert.ThrowsAnyAsync<Exception>(
            () => Collect(session.AskAsync(TimeSpan.FromSeconds(60), default)));

        Assert.Equal(SessionState.Recording, session.State);
        await session.StopAsync();
    }

    [Fact]
    public async Task A_cancelled_Ask_does_not_leave_the_state_stuck_on_Thinking()
    {
        // Regression: pressing Ask twice in quick succession cancels the first mid-stream.
        using var session = Build(new SlowAssistant());
        session.Start();
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "What is the price?", SegmentCutReason.Silence));

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.AskAsync(TimeSpan.FromSeconds(60), cts.Token))
                await cts.CancelAsync();
        });

        Assert.Equal(SessionState.Recording, session.State);
        await session.StopAsync();
    }

    [Fact]
    public async Task Warming_swallows_assistant_failures()
    {
        // Warm-up is an optimisation; failing it must never block a session from starting.
        using var session = Build(new ThrowingAssistant());
        await session.WarmAsync(default);
    }

    [Fact]
    public async Task A_channel_that_throws_degrades_the_session_instead_of_killing_it()
    {
        using var session = Build(sources: (channel, _) =>
            channel == AudioChannelKind.Mic ? new ExplodingSource(channel) : new SilentSource(channel));

        session.Start();

        // The failure surfaces asynchronously once the pipeline drains.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.State != SessionState.Degraded && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(SessionState.Degraded, session.State);
        await session.StopAsync();
    }

    // ------------------------------------------------------------------ pause

    [Fact]
    public async Task Pausing_keeps_the_meeting_and_resuming_continues_it()
    {
        using var session = Build();
        var seen = new List<SessionState>();
        session.StateChanged += seen.Add;

        session.Start();
        var id = session.SessionId;
        session.Transcript.Append(Spoken("Before the break."));

        await session.PauseAsync();
        Assert.True(session.IsRunning);
        Assert.True(session.IsPaused);
        Assert.Equal(SessionState.Paused, session.State);

        await session.ResumeAsync();
        Assert.False(session.IsPaused);
        Assert.Equal(SessionState.Recording, session.State);
        Assert.Equal(id, session.SessionId);
        Assert.Equal(["Before the break."], session.Transcript.All().Select(s => s.Text));
        Assert.Equal([SessionState.Recording, SessionState.Paused, SessionState.Recording], seen);

        await session.StopAsync();
    }

    [Fact]
    public async Task The_sentence_being_said_when_pausing_is_kept()
    {
        // Pausing cancels capture, and cancellation alone drops the open chunk and anything in
        // flight. Flushing first, as an Ask does, is what keeps it.
        using var session = Build(sources: (channel, _) => channel == AudioChannelKind.Loopback
            ? new SpeechThenNothingSource(channel, TimeSpan.FromSeconds(1))
            : new EmptySource(channel));

        session.Start();
        await Task.Delay(300);
        Assert.Equal(0, session.Transcript.Count);

        await session.PauseAsync();

        Assert.Equal(AudioChannelKind.Loopback, Assert.Single(session.Transcript.All()).Channel);
        await session.StopAsync();
    }

    [Fact]
    public async Task Ask_while_paused_answers_from_what_was_said_before()
    {
        using var session = Build(new StubAssistant("- answer"));
        session.Start();
        session.Transcript.Append(Spoken("What is your notice period?"));
        await session.PauseAsync();

        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));

        Assert.Contains("answer", text);
        Assert.Equal(SessionState.Paused, session.State);
        await session.StopAsync();
    }

    [Fact]
    public async Task Stopping_a_paused_session_ends_it()
    {
        using var session = Build();
        session.Start();
        await session.PauseAsync();

        await session.StopAsync();

        Assert.False(session.IsRunning);
        Assert.False(session.IsPaused);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Resuming_while_the_pause_is_still_settling_waits_for_it()
    {
        // A double press: the resume must not start capture that the finishing pause then cancels.
        using var session = Build();
        session.Start();

        var pausing = session.PauseAsync();
        await session.ResumeAsync();
        await pausing;

        Assert.False(session.IsPaused);
        Assert.Equal(SessionState.Recording, session.State);
        await session.StopAsync();
    }

    /// <summary>Loud audio for a while, then no frames at all: the chunk stays open until something flushes it.</summary>
    private sealed class SpeechThenNothingSource(AudioChannelKind channel, TimeSpan speech) : IAudioSource
    {
        public AudioChannelKind Channel => channel;
        public string Description => $"{channel} (speech, then nothing)";

        public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var frame = TimeSpan.FromMilliseconds(50);
            var pcm = new byte[AudioFormat.BytesFor(frame)];
            for (var i = 0; i < pcm.Length; i += 2)
                BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)((i / 2 % 20 < 10) ? 12_000 : -12_000));

            for (var at = TimeSpan.Zero; at < speech; at += frame)
            {
                yield return new AudioFrame(channel, at, pcm, false);
                await Task.Yield();
            }

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task A_second_session_cannot_see_the_first_sessions_transcript()
    {
        // Regression, and a confidentiality one: the transcript store was created once per app
        // run and never reset, so a second meeting inherited the first meeting's conversation
        // and sent the previous customer's words to the model as context for this one.
        using var session = Build(new StubAssistant("- leaked answer"));

        session.Start();
        session.Transcript.Append(Spoken("Customer A settled at forty thousand."));
        await session.StopAsync();

        Assert.Equal(1, session.Transcript.Count);

        session.Start();

        Assert.Equal(0, session.Transcript.Count);
        Assert.Empty(session.Transcript.All());
        Assert.Empty(session.Transcript.GetWindow(TimeSpan.FromHours(1)));

        // The assertion that matters is at the prompt boundary: Ask must find nothing to send.
        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));
        Assert.Contains("Nothing transcribed", text);
        Assert.DoesNotContain("leaked", text);

        await session.StopAsync();
    }

    [Fact]
    public async Task Each_session_gets_a_fresh_identity()
    {
        using var session = Build();
        Assert.Equal(Guid.Empty, session.SessionId);

        session.Start();
        var first = session.SessionId;
        var startedAt = session.StartedAt;
        Assert.NotEqual(Guid.Empty, first);
        await session.StopAsync();

        session.Start();
        Assert.NotEqual(first, session.SessionId);
        Assert.True(session.StartedAt >= startedAt);
        await session.StopAsync();
    }

    [Fact]
    public async Task Segments_are_persisted_against_the_session_that_produced_them()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MeetingAssistTests", Guid.NewGuid().ToString("N"));

        try
        {
            await using var repository = SessionRepository.Open(Path.Combine(folder, "sessions.db"));
            using var session = Build(repository: repository);

            session.Start();
            var first = session.SessionId;
            session.Transcript.Append(Spoken("first meeting"));
            await session.StopAsync();

            session.Start();
            var second = session.SessionId;
            session.Transcript.Append(Spoken("second meeting"));
            await session.StopAsync();

            await repository.FlushAsync();

            Assert.Equal("first meeting", Assert.Single(repository.ReadSegments(first)).Text);
            Assert.Equal("second meeting", Assert.Single(repository.ReadSegments(second)).Text);

            // Both meetings closed cleanly, so both carry an end time.
            Assert.Equal(2, repository.ReadSessions().Count);
            Assert.All(repository.ReadSessions(), s => Assert.NotNull(s.EndedUtc));
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { /* a leftover temp file is not worth failing the run over */ }
        }
    }

    [Fact]
    public async Task A_session_without_storage_still_runs()
    {
        // Storage is optional by design: an unwritable database costs the transcript, not the
        // meeting. Nothing on the session path may assume a repository exists.
        using var session = Build(repository: null);

        session.Start();
        session.Transcript.Append(Spoken("no database here"));
        await session.StopAsync();

        Assert.Equal(SessionState.Idle, session.State);
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> chunks)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var c in chunks) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Streams slowly enough that a caller can cancel partway through.</summary>
    private sealed class SlowAssistant : IAssistant
    {
        public string Name => "slow";
        public string ModelId => "slow-1";
        public AssistUsage? LastUsage => null;

        public async IAsyncEnumerable<string> AskAsync(
            AssistRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < 20; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(10, ct);
                yield return $"- bullet {i} ";
            }
        }
    }

    private sealed class ThrowingAssistant : IAssistant
    {
        public string Name => "throwing";
        public string ModelId => "throwing-1";
        public AssistUsage? LastUsage => null;

        public async IAsyncEnumerable<string> AskAsync(
            AssistRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new HttpRequestException("network is down");
#pragma warning disable CS0162 // Required to make this an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }
}
