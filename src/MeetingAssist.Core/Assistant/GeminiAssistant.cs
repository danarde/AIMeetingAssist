using Google.GenAI;
using GenerateContentConfig = Google.GenAI.Types.GenerateContentConfig;
using ThinkingConfig = Google.GenAI.Types.ThinkingConfig;
using ThinkingLevel = Google.GenAI.Types.ThinkingLevel;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Transcription;
using Microsoft.Extensions.AI;
using Serilog;

namespace MeetingAssist.Core.Assistant;

public sealed record AssistRequest(
    ContextProfile Profile,
    IReadOnlyList<TranscriptSegment> History,
    IReadOnlyList<TranscriptSegment> Focus,
    string? StyleTemplate = null);

public sealed record AssistUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);

public interface IAssistant
{
    string Name { get; }
    string ModelId { get; }
    IAsyncEnumerable<string> AskAsync(AssistRequest request, CancellationToken ct);
    AssistUsage? LastUsage { get; }
}

/// <summary>
/// One system + user exchange, streamed. The cue-note path is built on it, and so is the mock
/// counterpart, which needs the same model, retry and thinking-level handling but its own
/// prompt and a livelier temperature.
/// </summary>
public interface ICompletion
{
    /// <summary>Yields nothing at all when the model produced nothing, even after the retry.</summary>
    IAsyncEnumerable<string> CompleteAsync(string system, string user, float temperature, CancellationToken ct);
}

/// <summary>
/// Gemini through the official <c>Google.GenAI</c> SDK, consumed as
/// <see cref="IChatClient"/> (spec D17). Using the Microsoft.Extensions.AI abstraction means
/// swapping to OpenAI, Groq or Anthropic is a change of registration, not of this call site —
/// while the underlying <see cref="Client"/> stays available for Gemini-specific features
/// (safety settings, thinking config) if they are ever needed.
/// </summary>
public sealed class GeminiAssistant : IAssistant, ICompletion, IDisposable
{
    private readonly Client? _client;
    private readonly IChatClient _chat;

    /// <summary>
    /// The thinking level currently being sent, or null to send no thinking config at all.
    /// Accepted levels are model-dependent — <c>gemini-3.7-flash</c> and <c>3.8-flash</c> reject
    /// <c>minimal</c> with "Request contains an invalid argument" — so a rejection steps this
    /// down rather than failing the Ask. See <see cref="NextAfterRejection"/>.
    /// </summary>
    private ThinkingLevel? _thinking;

    /// <summary>Thinking at the lowest level the model allows (spec FR-6.9).</summary>
    public GeminiAssistant(string apiKey, string modelId) : this(apiKey, modelId, ThinkingLevel.Minimal) { }

    /// <param name="thinkingLevel">Null sends no thinking config, leaving the model's default.</param>
    public GeminiAssistant(string apiKey, string modelId, ThinkingLevel? thinkingLevel)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Gemini API key is empty. Set GEMINI_API_KEY.", nameof(apiKey));

        ModelId = modelId;
        _thinking = thinkingLevel;
        _client = new Client(apiKey: apiKey);
        _chat = _client.AsIChatClient(modelId);
    }

    /// <summary>
    /// Supplies the chat client directly instead of building one from a key, so the retry and
    /// thinking-config recovery below can be exercised without a network. Mirrors
    /// <see cref="Transcription.GroqTranscriber"/>'s injectable <c>HttpClient</c>; as there, an
    /// injected client belongs to the caller and is not disposed here.
    /// </summary>
    public GeminiAssistant(IChatClient chat, string modelId) : this(chat, modelId, ThinkingLevel.Minimal) { }

    /// <inheritdoc cref="GeminiAssistant(IChatClient, string)"/>
    public GeminiAssistant(IChatClient chat, string modelId, ThinkingLevel? thinkingLevel)
    {
        ModelId = modelId;
        _thinking = thinkingLevel;
        _chat = chat;
    }

    public string Name => "gemini";
    public string ModelId { get; }

    /// <summary>The level sent on the next request, after any step-down; null when none is sent.</summary>
    public ThinkingLevel? Thinking => _thinking;

    public AssistUsage? LastUsage { get; private set; }

    /// <summary>Low, so cue notes stay close to the context rather than improvising.</summary>
    private const float AnswerTemperature = 0.3f;

    public async IAsyncEnumerable<string> AskAsync(
        AssistRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var produced = false;

        // Stable prefix first, volatile content second — see PromptBuilder.
        await foreach (var chunk in CompleteAsync(
                           PromptBuilder.BuildSystem(request.Profile, request.StyleTemplate),
                           PromptBuilder.BuildUser(request.History, request.Focus, request.Profile),
                           AnswerTemperature, ct).ConfigureAwait(false))
        {
            produced = true;
            yield return chunk;
        }

        // A blank panel mid-meeting is the failure that actually hurts (spec FR-6.6).
        if (!produced) yield return "- No answer — press Ask again";
    }

    public async IAsyncEnumerable<string> CompleteAsync(
        string system,
        string user,
        float temperature,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        };

        var produced = false;

        await foreach (var chunk in StreamOnceAsync(messages, temperature, ct).ConfigureAwait(false))
        {
            produced = true;
            yield return chunk;
        }

        if (produced) yield break;

        // Empty, blocked, or the thinking config was rejected and has now been disabled.
        // One retry; what to show when that fails too is the caller's decision.
        Log.Warning("Assistant returned nothing; retrying once");
        await foreach (var chunk in StreamOnceAsync(messages, temperature, ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<string> StreamOnceAsync(
        List<ChatMessage> messages,
        float temperature,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var options = new ChatOptions
        {
            Temperature = temperature,
            MaxOutputTokens = 512
        };

        // Gemini 3.x models think by default, and for this workload that is pure loss:
        // measured at ~5.8 s to first token, with the reasoning tokens also consuming the
        // output budget so the answer came back truncated after five tokens. Five bullets
        // from a short transcript needs no deliberation.
        //
        // RawRepresentationFactory is Microsoft.Extensions.AI's escape hatch for
        // provider-specific settings, so this keeps IChatClient as the seam (spec D17)
        // instead of forcing a drop to the native client.
        //
        // Gemini 3 is controlled by thinkingLevel. The older thinkingBudget = 0 was rejected
        // outright by 3.5-flash-lite, so every new client paid one failed request before its
        // first answer. Accepted levels are still model-dependent, so a rejection is handled
        // below rather than being allowed to fail the Ask.
        var level = _thinking;
        if (level is not null)
        {
            options.RawRepresentationFactory = _ => new GenerateContentConfig
            {
                ThinkingConfig = new ThinkingConfig { ThinkingLevel = level }
            };
        }

        IAsyncEnumerator<ChatResponseUpdate> updates;
        try
        {
            updates = _chat.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
        }
        catch (Exception ex) when (level is not null && IsInvalidArgument(ex))
        {
            StepDown(level.Value, ex);
            yield break;
        }

        await using (updates.ConfigureAwait(false))
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await updates.MoveNextAsync().ConfigureAwait(false)) break;
                    update = updates.Current;
                }
                catch (Exception ex) when (level is not null && IsInvalidArgument(ex))
                {
                    StepDown(level.Value, ex);
                    yield break;
                }

                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usage)
                    {
                        LastUsage = new AssistUsage(
                            usage.Details.InputTokenCount,
                            usage.Details.OutputTokenCount,
                            usage.Details.TotalTokenCount);
                    }
                }

                if (!string.IsNullOrEmpty(update.Text)) yield return update.Text;
            }
        }
        yield break;
    }

    /// <summary>
    /// A rejected <c>minimal</c> steps to <c>low</c> before giving up on the config: dropping it
    /// would leave the model on its own default, which for 3.7 and 3.8 Flash is <c>medium</c> —
    /// the 2–6 s, truncated-answer behaviour this setting exists to prevent. Anything else that
    /// is rejected stops being sent. The new level holds for the instance's life, so only one
    /// Ask ever pays for the discovery.
    /// </summary>
    private void StepDown(ThinkingLevel rejected, Exception ex)
    {
        _thinking = NextAfterRejection(rejected);
        Log.Warning("{Model} rejected thinkingLevel={Rejected}; retrying with {Next}. {Message}",
            ModelId, rejected.Value, _thinking?.Value ?? "no thinking config", ex.Message);
    }

    // The cast matters: ThinkingLevel converts implicitly from string, so a bare null here
    // becomes a level holding null — sent as THINKING_LEVEL_UNSPECIFIED, not as no config.
    private static ThinkingLevel? NextAfterRejection(ThinkingLevel rejected) =>
        rejected.Equals(ThinkingLevel.Minimal) ? ThinkingLevel.Low : (ThinkingLevel?)null;

    private static bool IsInvalidArgument(Exception ex) =>
        ex.Message.Contains("invalid argument", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("INVALID_ARGUMENT", StringComparison.Ordinal)
        || ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        // An injected chat client belongs to the caller; only the one built from a key here,
        // together with the Client that produced it, is ours to dispose.
        if (_client is null) return;
        _chat.Dispose();
        _client.Dispose();
    }
}
