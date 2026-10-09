using System.Runtime.CompilerServices;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using static MeetingAssist.Tests.Doubles;

namespace MeetingAssist.Tests;

/// <summary>
/// A transcript that falls behind (backlog §5). In a real meeting on Groq's free plan it fell
/// twenty minutes behind with the indicator on Recording throughout, every Ask answered on
/// what had been said long before, and stopping threw the backlog away.
/// </summary>
public class TranscriptLagTests
{
    private static MeetingSession Build(ITranscriber transcriber, IAssistant? assistant = null, TimeSpan? drain = null) =>
        new(ContextProfile.Sample(),
            transcriber,
            assistant,
            new TranscriptionOptions(),
            new SegmenterOptions(),
            (channel, _) => channel == AudioChannelKind.Loopback
                ? new SpeechThenNothingSource(channel)
                : new EmptySource(channel))
        {
            FlushTimeout = TimeSpan.FromMilliseconds(100),
            LagThreshold = TimeSpan.FromMilliseconds(150),
            LagCheckInterval = TimeSpan.FromMilliseconds(25),
            DrainTimeout = drain ?? TimeSpan.FromSeconds(5)
        };

    [Fact]
    public async Task A_transcript_that_falls_behind_degrades_the_session_and_says_by_how_much()
    {
        var transcriber = new HeldTranscriber();
        using var session = Build(transcriber);
        session.Start();

        // The flush hands the spoken chunk to a provider that does not answer.
        await Task.Delay(200);
        await session.SettleTranscriptAsync(default);
        await Until(() => session.State == SessionState.Degraded);

        Assert.Equal(1, session.Backlog.Count);
        Assert.Matches(@"^transcript \d+ s behind$", session.LagDetail);

        transcriber.Answer();
        await Until(() => session.State == SessionState.Recording);

        Assert.Null(session.LagDetail);
        Assert.Single(session.Transcript.All());
        await session.StopAsync();
    }

    [Fact]
    public async Task An_Ask_while_behind_says_what_it_may_be_missing()
    {
        var transcriber = new HeldTranscriber();
        using var session = Build(transcriber, new StubAssistant("- answer"));
        session.Start();
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "Tell me about components.", SegmentCutReason.Silence));

        await Task.Delay(200);
        await session.SettleTranscriptAsync(default);
        await Until(() => session.State == SessionState.Degraded);

        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));

        Assert.StartsWith("- Note: 1 line(s) from the last ", text);
        Assert.Contains("- answer", text);

        transcriber.Answer();
        await session.StopAsync();
    }

    [Fact]
    public async Task An_Ask_that_is_not_behind_carries_no_note()
    {
        using var session = Build(new FakeTranscriber(TimeSpan.FromMilliseconds(10)), new StubAssistant("- answer"));
        session.Start();
        await Task.Delay(200);

        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));

        Assert.Equal("- answer", text);
        await session.StopAsync();
    }

    [Fact]
    public async Task Stopping_finishes_transcribing_what_was_already_heard()
    {
        // Slower than the flush timeout: before the fix, stopping cancelled it mid-request.
        using var session = Build(new FakeTranscriber(TimeSpan.FromMilliseconds(400)));
        session.Start();
        await Task.Delay(200);

        await session.StopAsync();

        Assert.Single(session.Transcript.All());
        Assert.Equal(0, session.LastAbandoned);
    }

    [Fact]
    public async Task Stopping_gives_up_after_the_drain_timeout_and_counts_what_it_lost()
    {
        using var session = Build(new HeldTranscriber(), drain: TimeSpan.FromMilliseconds(300));
        session.Start();
        await Task.Delay(200);

        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, session.LastAbandoned);
        Assert.Empty(session.Transcript.All());
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Pausing_also_finishes_what_was_already_heard()
    {
        using var session = Build(new FakeTranscriber(TimeSpan.FromMilliseconds(400)));
        session.Start();
        await Task.Delay(200);

        await session.PauseAsync();

        Assert.Single(session.Transcript.All());
        Assert.Equal(SessionState.Paused, session.State);
        await session.StopAsync();
    }

    [Fact]
    public async Task A_freed_slot_goes_to_the_newest_waiter()
    {
        var gate = new NewestFirstGate(1);
        await gate.WaitAsync(default);

        var order = new List<string>();
        var older = gate.WaitAsync(default).ContinueWith(_ => { lock (order) order.Add("older"); gate.Release(); });
        var newer = gate.WaitAsync(default).ContinueWith(_ => { lock (order) order.Add("newer"); gate.Release(); });

        gate.Release();
        await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["newer", "older"], order);
    }

    [Fact]
    public async Task A_cancelled_waiter_gives_up_its_place()
    {
        var gate = new NewestFirstGate(1);
        await gate.WaitAsync(default);

        using var cts = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(cts.Token);
        var waiting = gate.WaitAsync(default);
        var newest = gate.WaitAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newest);

        gate.Release();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(GroqPlan.Free, 18)]
    [InlineData(GroqPlan.Developer, 360)]
    public void Each_Groq_plan_is_paced_under_its_cap(GroqPlan plan, int perMinute) =>
        Assert.Equal(perMinute, GroqTranscriber.RequestsPerMinute(plan));

    [Theory]
    [InlineData(8, "8 s")]
    [InlineData(89, "89 s")]
    [InlineData(150, "2 min")]
    [InlineData(1200, "20 min")]
    public void A_lag_reads_in_seconds_then_minutes(int seconds, string expected) =>
        Assert.Equal(expected, MeetingSession.Describe(TimeSpan.FromSeconds(seconds)));

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met within 5 s");
            await Task.Delay(20);
        }
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> chunks)
    {
        var text = "";
        await foreach (var chunk in chunks) text += chunk;
        return text;
    }

    /// <summary>A provider that does not answer until told to: a throttled or stalled Groq.</summary>
    private sealed class HeldTranscriber : ITranscriber
    {
        private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "held";

        public event Action<bool>? AttemptCompleted;

        public void Answer() => _answer.TrySetResult();

        public async Task<TranscriptionResult?> TranscribeAsync(
            AudioSegment segment, TranscriptionOptions options, CancellationToken ct)
        {
            await _answer.Task.WaitAsync(ct);
            AttemptCompleted?.Invoke(true);
            return new TranscriptionResult("Words.", segment.Duration.TotalSeconds, 1);
        }
    }

    /// <summary>A second of speech, then no frames: the chunk is emitted by a flush or a stop.</summary>
    private sealed class SpeechThenNothingSource(AudioChannelKind channel) : IAudioSource
    {
        public AudioChannelKind Channel => channel;
        public string Description => $"{channel} (speech, then nothing)";

        public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var frame = TimeSpan.FromMilliseconds(50);
            var pcm = new byte[AudioFormat.BytesFor(frame)];
            for (var i = 0; i < pcm.Length; i += 2)
                BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)((i / 2 % 20 < 10) ? 12_000 : -12_000));

            for (var at = TimeSpan.Zero; at < TimeSpan.FromSeconds(1); at += frame)
            {
                yield return new AudioFrame(channel, at, pcm, false);
                await Task.Yield();
            }

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
    }
}
