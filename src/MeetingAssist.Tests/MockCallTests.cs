using System.Runtime.CompilerServices;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Mock;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

/// <summary>
/// Mock mode (backlog §3). Two things are worth pinning down. The prompt: the counterpart must
/// know which lines are its own — with the profile's "You" label it answered its own questions
/// as the user. And the turn: a press mid-line supersedes it, and however a turn ends the
/// stage must come back to Idle, or the overlay says "speaking" for the rest of the call.
/// </summary>
public class MockCallTests
{
    private static ContextProfile Profile(string brief = "")
    {
        var profile = ContextProfile.Sample();
        profile.MicLabel = "You";
        profile.LoopbackLabel = "Client";
        profile.MockBrief = brief;
        return profile;
    }

    private static TranscriptSegment Said(AudioChannelKind channel, string text, double at) =>
        new(Guid.NewGuid(), channel, TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(at + 2),
            text, SegmentCutReason.Silence);

    // ------------------------------------------------------------------ prompt

    [Fact]
    public void The_transcript_is_shown_from_the_counterparts_side()
    {
        var user = MockPromptBuilder.BuildUser(
        [
            Said(AudioChannelKind.Loopback, "Why this vendor?", 0),
            Said(AudioChannelKind.Mic, "Because of the support.", 3)
        ], Profile());

        Assert.Contains("YOU: Why this vendor?", user);
        Assert.Contains("THEM: Because of the support.", user);
        Assert.DoesNotContain("You: ", user);
    }

    [Fact]
    public void An_unanswered_question_of_its_own_is_flagged_as_unanswered()
    {
        var unanswered = MockPromptBuilder.BuildUser(
            [Said(AudioChannelKind.Loopback, "What about single sign-on?", 0)], Profile());
        var answered = MockPromptBuilder.BuildUser(
        [
            Said(AudioChannelKind.Loopback, "What about single sign-on?", 0),
            Said(AudioChannelKind.Mic, "It is on the Enterprise tier.", 3)
        ], Profile());

        Assert.Contains("has not replied to it yet", unanswered);
        Assert.DoesNotContain("has not replied to it yet", answered);
    }

    [Fact]
    public void An_empty_conversation_asks_for_an_opening()
    {
        var user = MockPromptBuilder.BuildUser([], Profile());

        Assert.Contains("(nothing has been said yet)", user);
        Assert.DoesNotContain("has not replied", user);
    }

    [Fact]
    public void The_brief_reaches_the_counterpart_and_never_the_cue_notes()
    {
        var profile = Profile("Ask about the data residency clause.");

        Assert.Contains("Ask about the data residency clause.", MockPromptBuilder.BuildSystem(profile));
        Assert.Contains(profile.Meeting, MockPromptBuilder.BuildSystem(profile));
        Assert.DoesNotContain("data residency", PromptBuilder.BuildSystem(profile));
    }

    [Fact]
    public void A_blank_brief_plays_the_party_the_meeting_describes()
    {
        Assert.Contains("Play \"Client\" as the meeting description presents them.",
            MockPromptBuilder.BuildSystem(Profile()));
    }

    [Theory]
    [InlineData("Client: Walk me through the pricing.", "Walk me through the pricing.")]
    [InlineData("YOU: Walk me through the pricing.", "Walk me through the pricing.")]
    [InlineData("\"Walk me through the **pricing**.\"", "Walk me through the pricing.")]
    [InlineData("Walk me\n\nthrough   the pricing.", "Walk me through the pricing.")]
    public void Lines_are_cleaned_so_the_voice_never_reads_out_markup(string raw, string spoken)
    {
        Assert.Equal(spoken, MockCall.Clean(raw, Profile()));
    }

    // ------------------------------------------------------------------ turns

    [Fact]
    public async Task A_turn_reads_the_settled_transcript_and_speaks_the_line()
    {
        var completion = new ScriptedCompletion("Client: So, why Acme?");
        var voice = new RecordingVoice();
        var settled = 0;
        using var mock = new MockCall(Profile(), completion, voice, _ =>
        {
            settled++;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>(
                [Said(AudioChannelKind.Mic, "Hello Priya.", 0)]);
        });

        var stages = new List<MockStage>();
        mock.StageChanged += stages.Add;

        var line = await mock.TakeTurnAsync(default);

        Assert.Equal("So, why Acme?", line);
        Assert.Equal(["So, why Acme?"], voice.Spoken);
        Assert.Equal(1, settled);
        Assert.Contains("THEM: Hello Priya.", Assert.Single(completion.Users));
        Assert.Equal([MockStage.Thinking, MockStage.Speaking, MockStage.Idle], stages);
    }

    [Fact]
    public async Task Nothing_from_the_model_means_nothing_is_spoken()
    {
        var voice = new RecordingVoice();
        using var mock = new MockCall(Profile(), new ScriptedCompletion(""), voice, NoTranscript);

        Assert.Null(await mock.TakeTurnAsync(default));
        Assert.Empty(voice.Spoken);
        Assert.Equal(MockStage.Idle, mock.Stage);
    }

    [Fact]
    public async Task A_second_press_mid_line_supersedes_the_first()
    {
        var voice = new RecordingVoice { HoldFirst = true };
        using var mock = new MockCall(Profile(), new ScriptedCompletion("First line.", "Second line."),
            voice, NoTranscript);

        var first = mock.TakeTurnAsync(default);
        await voice.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await mock.TakeTurnAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("Second line.", second);
        Assert.Equal(["First line.", "Second line."], voice.Spoken);
        Assert.Equal(MockStage.Idle, mock.Stage);
    }

    [Fact]
    public async Task Cancel_stops_a_line_and_returns_to_idle()
    {
        var voice = new RecordingVoice { HoldFirst = true };
        using var mock = new MockCall(Profile(), new ScriptedCompletion("A long question."), voice, NoTranscript);

        var turn = mock.TakeTurnAsync(default);
        await voice.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MockStage.Speaking, mock.Stage);

        mock.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(MockStage.Idle, mock.Stage);
    }

    // ------------------------------------------------------------------ voice

    [Theory]
    [InlineData("audio/l16; rate=24000; channels=1", 24000, 1)] // 3.x TTS models
    [InlineData("audio/L16;codec=pcm;rate=24000", 24000, 1)]     // 2.5 TTS models
    [InlineData("audio/L16;rate=16000;channels=2", 16000, 2)]
    public void Gemini_audio_formats_are_read_from_the_mime_type(string mime, int rate, int channels)
    {
        var format = GeminiVoice.ParseFormat(mime);

        Assert.Equal(rate, format.SampleRate);
        Assert.Equal(channels, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData(null)]
    public void Audio_that_is_not_raw_pcm_is_refused_before_it_is_played(string? mime)
    {
        Assert.Throws<NotSupportedException>(() => GeminiVoice.ParseFormat(mime));
    }

    [Fact]
    public void The_default_gemini_voice_is_one_of_the_offered_voices()
    {
        Assert.Contains(GeminiVoice.DefaultVoice, GeminiVoice.Voices);
        Assert.Equal(GeminiVoice.Voices.Count, GeminiVoice.Voices.Distinct().Count());
    }

    private static Task<IReadOnlyList<TranscriptSegment>> NoTranscript(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TranscriptSegment>>([]);

    /// <summary>Answers each call with the next scripted line, and keeps the prompts it was sent.</summary>
    private sealed class ScriptedCompletion(params string[] lines) : ICompletion
    {
        private int _next;

        public List<string> Users { get; } = [];

        public async IAsyncEnumerable<string> CompleteAsync(
            string system, string user, float temperature, [EnumeratorCancellation] CancellationToken ct)
        {
            Users.Add(user);
            await Task.Yield();
            var line = lines[Math.Min(_next++, lines.Length - 1)];
            if (line.Length > 0) yield return line;
        }
    }

    /// <summary>Records what it was asked to say. Can hold the first line until cancelled, like a long sentence.</summary>
    private sealed class RecordingVoice : IVoice
    {
        public bool HoldFirst { get; init; }
        public List<string> Spoken { get; } = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Description => "recording";

        public async Task SpeakAsync(string text, CancellationToken ct)
        {
            Spoken.Add(text);
            if (Spoken.Count == 1 && HoldFirst)
            {
                FirstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        }

        public void Dispose() { }
    }
}
