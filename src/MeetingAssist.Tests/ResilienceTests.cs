using System.Runtime.CompilerServices;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using static MeetingAssist.Tests.Doubles;

namespace MeetingAssist.Tests;

/// <summary>
/// Acceptance criterion 8: a failure must be visible while it lasts and must clear itself when
/// it passes. Both halves matter — an indicator that never goes amber is useless, and one that
/// stays amber after recovery is worse, because it trains the user to ignore it.
///
/// Timings here are compressed to milliseconds. The production defaults are seconds; what is
/// under test is the transition logic, not the delays.
/// </summary>
public class ResilienceTests
{
    private static MeetingSession Build(
        Func<AudioChannelKind, TimeBase, IAudioSource> sources,
        ITranscriber? transcriber = null,
        int retryAttempts = 3) =>
        new(ContextProfile.Sample(),
            transcriber ?? new FakeTranscriber(),
            new StubAssistant("- noted"),
            new TranscriptionOptions(),
            new SegmenterOptions(),
            sources)
        {
            FlushTimeout = TimeSpan.FromMilliseconds(100),
            DeviceRetryAttempts = retryAttempts,
            DeviceRetryDelay = TimeSpan.FromMilliseconds(30),
            DeviceSettleTime = TimeSpan.FromMilliseconds(80),
            TranscriptionFailureThreshold = 3
        };

    private static async Task<bool> Reaches(MeetingSession session, SessionState state, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (session.State == state) return true;
            await Task.Delay(10);
        }
        return session.State == state;
    }

    [Fact]
    public async Task A_capture_device_that_comes_back_returns_the_session_to_recording()
    {
        // The case that matters after FR-2.5 device pinning: a pinned headset unplugged and
        // plugged back in must not require the user to notice and restart the meeting.
        var micAttempts = 0;

        using var session = Build((channel, _) =>
        {
            if (channel != AudioChannelKind.Mic) return new SilentSource(channel);
            return Interlocked.Increment(ref micAttempts) == 1
                ? new ExplodingSource(channel)
                : new SilentSource(channel);
        });

        session.Start();

        Assert.True(await Reaches(session, SessionState.Degraded), "never reported the failure");
        Assert.True(await Reaches(session, SessionState.Recording), "never recovered");
        Assert.True(session.IsHealthy);
        Assert.True(micAttempts >= 2);

        await session.StopAsync();
    }

    [Fact]
    public async Task A_device_that_never_comes_back_is_given_up_on_and_stays_degraded()
    {
        using var session = Build(
            (channel, _) => channel == AudioChannelKind.Mic
                ? new ExplodingSource(channel)
                : new SilentSource(channel),
            retryAttempts: 1);

        session.Start();
        Assert.True(await Reaches(session, SessionState.Degraded));

        // Still degraded well after the retries are exhausted — it must not quietly go green.
        await Task.Delay(300);
        Assert.Equal(SessionState.Degraded, session.State);
        Assert.False(session.IsHealthy);

        await session.StopAsync();
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task The_other_channel_keeps_working_while_one_is_broken()
    {
        // Degraded, not broken: the far end still being transcribed is most of the value.
        using var session = Build(
            (channel, _) => channel == AudioChannelKind.Mic
                ? new ExplodingSource(channel)
                : new SilentSource(channel),
            retryAttempts: 0);

        session.Start();
        Assert.True(await Reaches(session, SessionState.Degraded));

        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "the customer is still audible", SegmentCutReason.Silence));

        var text = await Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));
        Assert.Contains("noted", text);

        // And an Ask must not launder the session back to healthy on its way out.
        Assert.Equal(SessionState.Degraded, session.State);

        await session.StopAsync();
    }

    [Fact]
    public async Task A_transcription_outage_degrades_the_session_and_recovery_clears_it()
    {
        // Without this the overlay shows a healthy green dot over an empty transcript, which
        // looks exactly like a quiet room — the misleading state FR-7.6 exists to prevent.
        var transcriber = new ControllableTranscriber();

        using var session = Build((channel, _) => new SilentSource(channel), transcriber);
        session.Start();
        Assert.Equal(SessionState.Recording, session.State);

        // Below the threshold: a dropped chunk on a flaky connection must not flicker it.
        transcriber.Report(false);
        transcriber.Report(false);
        Assert.Equal(SessionState.Recording, session.State);

        transcriber.Report(false);
        Assert.Equal(SessionState.Degraded, session.State);
        Assert.False(session.IsHealthy);

        // One good response is enough: the provider is demonstrably answering again.
        transcriber.Report(true);
        Assert.Equal(SessionState.Recording, session.State);
        Assert.True(session.IsHealthy);

        await session.StopAsync();
    }

    [Fact]
    public async Task A_recovered_provider_resets_the_streak_rather_than_decrementing_it()
    {
        var transcriber = new ControllableTranscriber();

        using var session = Build((channel, _) => new SilentSource(channel), transcriber);
        session.Start();

        transcriber.Report(false);
        transcriber.Report(false);
        transcriber.Report(true);

        // If the counter merely decremented, two more failures would trip the threshold.
        transcriber.Report(false);
        transcriber.Report(false);
        Assert.Equal(SessionState.Recording, session.State);

        await session.StopAsync();
    }

    [Fact]
    public async Task Health_reported_during_an_Ask_is_applied_when_the_Ask_finishes()
    {
        // The Ask sets Thinking; a failure arriving underneath it must not be lost when the
        // answer completes and the indicator goes back to its resting state.
        var transcriber = new ControllableTranscriber();
        var assistant = new GatedAssistant();

        using var session = new MeetingSession(
            ContextProfile.Sample(), transcriber, assistant,
            new TranscriptionOptions(), new SegmenterOptions(),
            (channel, _) => new SilentSource(channel))
        {
            FlushTimeout = TimeSpan.FromMilliseconds(100),
            TranscriptionFailureThreshold = 2
        };

        session.Start();
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Mic, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "anything", SegmentCutReason.Silence));

        var ask = Collect(session.AskAsync(TimeSpan.FromSeconds(60), default));

        await assistant.Started.Task;
        Assert.Equal(SessionState.Thinking, session.State);

        transcriber.Report(false);
        transcriber.Report(false);

        // Still Thinking: the Ask owns the indicator while it is streaming.
        Assert.Equal(SessionState.Thinking, session.State);

        assistant.Release();
        await ask;

        Assert.Equal(SessionState.Degraded, session.State);
        await session.StopAsync();
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> chunks)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var c in chunks) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Raises the health signal on demand, with no network anywhere near it.</summary>
    private sealed class ControllableTranscriber : ITranscriber
    {
        public string Name => "controllable";
        public event Action<bool>? AttemptCompleted;

        public void Report(bool ok) => AttemptCompleted?.Invoke(ok);

        public Task<TranscriptionResult?> TranscribeAsync(
            AudioSegment segment, TranscriptionOptions options, CancellationToken ct) =>
            Task.FromResult<TranscriptionResult?>(null);
    }

    /// <summary>Holds the stream open so the test can act while the session is Thinking.</summary>
    private sealed class GatedAssistant : IAssistant
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "gated";
        public string ModelId => "gated-1";
        public AssistUsage? LastUsage => null;

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<string> AskAsync(
            AssistRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult();
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            yield return "- noted";
        }
    }
}
