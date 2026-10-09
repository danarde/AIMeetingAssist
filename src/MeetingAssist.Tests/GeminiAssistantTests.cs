using System.Runtime.CompilerServices;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Transcription;
using Microsoft.Extensions.AI;
using GenerateContentConfig = Google.GenAI.Types.GenerateContentConfig;
using ThinkingLevel = Google.GenAI.Types.ThinkingLevel;

namespace MeetingAssist.Tests;

/// <summary>
/// The assistant's recovery paths, which are invisible in normal use precisely because they
/// work. None of this can catch Google changing something — a test asserting we recognise the
/// words "invalid argument" keeps passing on the day that wording changes. What it pins is our
/// own behaviour, so a later refactor cannot quietly turn a handled failure into a blank
/// overlay mid-meeting.
/// </summary>
public class GeminiAssistantTests
{
    private static AssistRequest Request() =>
        new(ContextProfile.Sample(), [], [
            new TranscriptSegment(
                Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(2),
                "What does it cost?", SegmentCutReason.Silence)
        ]);

    /// <summary>The message the overlay shows when two attempts both come back with nothing.</summary>
    private const string NoAnswer = "- No answer — press Ask again";

    [Fact]
    public async Task An_empty_answer_is_retried_once_and_then_explains_itself()
    {
        // A blank panel mid-meeting is the failure that actually hurts (spec FR-6.6), and this
        // fallback is the only thing standing between an empty response and exactly that.
        var chat = new ScriptedChatClient(Step.Streams(), Step.Streams());
        using var assistant = new GeminiAssistant(chat, "test-model");

        var text = await Collect(assistant.AskAsync(Request(), default));

        Assert.Equal(NoAnswer, text);
        Assert.Equal(2, chat.Calls.Count);
    }

    private static Exception Rejected() =>
        new InvalidOperationException("Request contains an invalid argument.");

    [Fact]
    public async Task Thinking_is_asked_for_at_the_minimal_level_by_default()
    {
        // Gemini 3 thinks by default, which measured at seconds to first token and a truncated
        // answer (spec FR-6.9). The lowest level is the one that keeps an Ask inside budget.
        var chat = new ScriptedChatClient(Step.Streams("- answer"));
        using var assistant = new GeminiAssistant(chat, "test-model");

        await Collect(assistant.AskAsync(Request(), default));

        Assert.Equal(ThinkingLevel.Minimal, chat.Calls[0].Thinking);
    }

    [Fact]
    public async Task A_rejected_minimal_level_steps_to_low_inside_the_same_Ask()
    {
        // 3.7 and 3.8 Flash reject minimal. Dropping the config would leave them on their
        // medium default, so the retry asks for low instead — and the user sees an ordinary
        // answer, not an error.
        var chat = new ScriptedChatClient(Step.Throws(Rejected()), Step.Streams("- real ", "answer"));
        using var assistant = new GeminiAssistant(chat, "test-model");

        var text = await Collect(assistant.AskAsync(Request(), default));

        Assert.Equal("- real answer", text);
        Assert.Equal([ThinkingLevel.Minimal, ThinkingLevel.Low], chat.Calls.Select(c => c.Thinking));
    }

    [Fact]
    public async Task The_stepped_down_level_holds_for_later_Asks()
    {
        // Instance state for a reason: if it reset per Ask, every press would burn a failed
        // request first. Slow, and invisible unless something asserts it.
        var chat = new ScriptedChatClient(
            Step.Throws(Rejected()),
            Step.Streams("- first"),
            Step.Streams("- second"));
        using var assistant = new GeminiAssistant(chat, "test-model");

        await Collect(assistant.AskAsync(Request(), default));
        var text = await Collect(assistant.AskAsync(Request(), default));

        Assert.Equal("- second", text);
        Assert.Equal(3, chat.Calls.Count);
        Assert.Equal(ThinkingLevel.Low, chat.Calls[2].Thinking);
    }

    [Fact]
    public async Task A_model_that_rejects_every_level_ends_up_with_no_thinking_config()
    {
        // The last resort is the model's own default: slower, but an answer. That Ask has used
        // its one retry on discovery, so it says so; the next one goes through.
        var chat = new ScriptedChatClient(
            Step.Throws(Rejected()),
            Step.Throws(Rejected()),
            Step.Streams("- answer"));
        using var assistant = new GeminiAssistant(chat, "test-model");

        var first = await Collect(assistant.AskAsync(Request(), default));
        var second = await Collect(assistant.AskAsync(Request(), default));

        Assert.Equal(NoAnswer, first);
        Assert.Equal("- answer", second);
        Assert.Equal([ThinkingLevel.Minimal, ThinkingLevel.Low, null], chat.Calls.Select(c => c.Thinking));
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> chunks)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var c in chunks) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>One scripted turn: either it streams these chunks, or it throws.</summary>
    private sealed record Step(string[] Text, Exception? Error)
    {
        public static Step Streams(params string[] chunks) => new(chunks, null);
        public static Step Throws(Exception error) => new([], error);
    }

    /// <summary>
    /// Consumes one <see cref="Step"/> per call and records the thinking level attached, if
    /// any — the only way from outside to observe that a rejected one was stepped down.
    /// </summary>
    private sealed class ScriptedChatClient(params Step[] steps) : IChatClient
    {
        private readonly Queue<Step> _steps = new(steps);

        public List<Call> Calls { get; } = [];

        public sealed record Call(ThinkingLevel? Thinking);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var config = options?.RawRepresentationFactory?.Invoke(this) as GenerateContentConfig;
            Calls.Add(new Call(config?.ThinkingConfig?.ThinkingLevel));

            var step = _steps.Count > 0 ? _steps.Dequeue() : Step.Streams();
            await Task.Yield();

            if (step.Error is not null) throw step.Error;

            foreach (var chunk in step.Text)
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The assistant only ever streams.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
