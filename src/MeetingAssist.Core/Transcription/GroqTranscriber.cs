using System.Net.Http.Headers;

namespace MeetingAssist.Core.Transcription;

/// <summary>
/// Groq's OpenAI-compatible transcription endpoint (spec FR-4.2). Batch, not streaming:
/// each segment is one multipart POST. Chunk duration — not this request — is the dominant
/// latency term, which is why the segmenter supports flushing.
/// </summary>
public sealed class GroqTranscriber : HttpTranscriber
{
    /// <summary>
    /// The free plan's pace: Groq caps Whisper at 20 requests a minute there, and pacing at
    /// the cap exactly still drew a 429 every two minutes or so in a real meeting (2026-10-07),
    /// each one costing a slot and a retry. A little under the cap costs nothing.
    /// </summary>
    public const int DefaultRequestsPerMinute = 18;

    /// <summary>
    /// The Developer (paid) plan's pace. Groq's own rate-limit page lists only the free plan;
    /// 400 a minute is what two independent write-ups give for it (checked 2026-10-07). Paced at
    /// 90% of that, which is still twenty times what a two-person meeting needs.
    /// </summary>
    public const int DeveloperRequestsPerMinute = 360;

    public static int RequestsPerMinute(GroqPlan plan) =>
        plan == GroqPlan.Developer ? DeveloperRequestsPerMinute : DefaultRequestsPerMinute;

    public GroqTranscriber(string apiKey, HttpClient? http = null, int requestsPerMinute = DefaultRequestsPerMinute)
        : this(apiKey, http, new RateLimiter(requestsPerMinute)) { }

    /// <summary>
    /// Supplies the pacer directly, so a test can saturate it on a window of milliseconds
    /// rather than a minute.
    /// </summary>
    public GroqTranscriber(string apiKey, HttpClient? http, RateLimiter limiter)
        : base(apiKey, "GROQ_API_KEY", http, limiter)
    {
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public override string Name => "groq";

    protected override string Endpoint => "https://api.groq.com/openai/v1/audio/transcriptions";

    protected override void AddFields(MultipartFormDataContent form, TranscriptionOptions options)
    {
        form.Add(new StringContent(options.ModelId), "model");
        form.Add(new StringContent(options.Language), "language");
        form.Add(new StringContent("0"), "temperature");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(options.Vocabulary))
            form.Add(new StringContent(options.Vocabulary), "prompt");
    }
}

/// <summary>
/// Which Groq plan the key belongs to. It decides how fast the app may send: on the free plan
/// a lively meeting produces more speech than 20 requests a minute can carry, and the
/// transcript falls minutes behind (backlog §5).
/// </summary>
public enum GroqPlan { Free, Developer }
