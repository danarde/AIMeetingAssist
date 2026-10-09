using System.Runtime.CompilerServices;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using static MeetingAssist.Tests.Doubles;

namespace MeetingAssist.Tests;

/// <summary>
/// The answer style is the one profile field that changes the model's instructions rather than
/// its inputs, so it has two properties worth pinning down: a blank one must fall back to the
/// built-in default (clearing the box is the user's way out of a bad edit), and a custom one
/// must not break the byte-identical prefix that implicit caching depends on (FR-6.5/D14).
/// </summary>
public class AnswerStyleTests
{
    private const string Custom = """
        Answer in exactly three bullets. Never more.
        Write in {LANGUAGE}.
        """;

    private static MeetingSession Build(string answerStyle, CapturingAssistant assistant)
    {
        var profile = ContextProfile.Sample();
        profile.AnswerStyle = answerStyle;

        return new MeetingSession(
            profile, new FakeTranscriber(), assistant,
            new TranscriptionOptions(), new SegmenterOptions(),
            (channel, _) => new EmptySource(channel))
        {
            FlushTimeout = TimeSpan.FromMilliseconds(100)
        };
    }

    private static async Task<CapturingAssistant> Ask(string answerStyle)
    {
        var assistant = new CapturingAssistant();
        using var session = Build(answerStyle, assistant);

        session.Start();
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "What does it cost?", SegmentCutReason.Silence));

        await foreach (var _ in session.AskAsync(TimeSpan.FromSeconds(60), default)) { }

        await session.StopAsync();
        return assistant;
    }

    [Fact]
    public async Task A_blank_style_falls_back_to_the_built_in_default()
    {
        var assistant = await Ask("");

        var request = Assert.Single(assistant.Requests);
        Assert.Null(request.StyleTemplate);

        // Null is what makes PromptBuilder use its own default, so assert the outcome too.
        Assert.Contains("At most 5 bullets", PromptBuilder.BuildSystem(request.Profile, request.StyleTemplate));
    }

    [Fact]
    public async Task Whitespace_counts_as_blank()
    {
        var assistant = await Ask("   \r\n  ");

        Assert.Null(Assert.Single(assistant.Requests).StyleTemplate);
    }

    [Fact]
    public async Task A_custom_style_replaces_the_default_rather_than_adding_to_it()
    {
        var assistant = await Ask(Custom);

        var request = Assert.Single(assistant.Requests);
        var system = PromptBuilder.BuildSystem(request.Profile, request.StyleTemplate);

        Assert.Contains("Answer in exactly three bullets", system);
        Assert.DoesNotContain("At most 5 bullets", system);

        // The language placeholder still resolves, so a custom style is not a way to
        // accidentally lose the profile's language.
        Assert.Contains("Write in English", system);
        Assert.DoesNotContain("{LANGUAGE}", system);
    }

    [Fact]
    public async Task Editing_the_profile_mid_session_cannot_change_the_prompt()
    {
        // The style is captured at construction precisely so a settings-window edit cannot
        // invalidate the cached prefix underneath a running meeting.
        var assistant = new CapturingAssistant();
        var profile = ContextProfile.Sample();
        profile.AnswerStyle = Custom;

        using var session = new MeetingSession(
            profile, new FakeTranscriber(), assistant,
            new TranscriptionOptions(), new SegmenterOptions(),
            (channel, _) => new EmptySource(channel))
        {
            FlushTimeout = TimeSpan.FromMilliseconds(100)
        };

        session.Start();
        session.Transcript.Append(new TranscriptSegment(
            Guid.NewGuid(), AudioChannelKind.Mic, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            "first", SegmentCutReason.Silence));

        await foreach (var _ in session.AskAsync(TimeSpan.FromSeconds(60), default)) { }

        profile.AnswerStyle = "Something completely different.";

        await foreach (var _ in session.AskAsync(TimeSpan.FromSeconds(60), default)) { }
        await session.StopAsync();

        Assert.Equal(2, assistant.Requests.Count);
        Assert.Equal(assistant.Requests[0].StyleTemplate, assistant.Requests[1].StyleTemplate);
        Assert.Equal(Custom, assistant.Requests[1].StyleTemplate);
    }

    private sealed class CapturingAssistant : IAssistant
    {
        public List<AssistRequest> Requests { get; } = [];

        public string Name => "capturing";
        public string ModelId => "capturing-1";
        public AssistUsage? LastUsage => null;

        public async IAsyncEnumerable<string> AskAsync(
            AssistRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return "- noted";
        }
    }
}
