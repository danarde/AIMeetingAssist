using System.Net.Http.Headers;

namespace MeetingAssist.Core.Transcription;

/// <summary>
/// xAI's batch speech-to-text, Grok Voice Transcribe (released 2026-09-18). Request shape from
/// docs.x.ai, checked 2026-10-07: multipart to /v1/stt, bearer key, one <c>keyterm</c> field
/// per term, up to 100.
/// </summary>
public sealed class XaiTranscriber : HttpTranscriber
{
    public const string DefaultModel = "grok-voice-transcribe-2.0";

    /// <summary>
    /// xAI publishes no rate limit for this endpoint. A pace well above what a meeting needs
    /// still keeps a runaway loop from billing without bound; 429s are retried either way.
    /// </summary>
    public const int DefaultRequestsPerMinute = 120;

    private const int MaxKeyterms = 100;

    public XaiTranscriber(string apiKey, HttpClient? http = null, int requestsPerMinute = DefaultRequestsPerMinute)
        : base(apiKey, "XAI_API_KEY", http, new RateLimiter(requestsPerMinute))
    {
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public override string Name => "xai";

    protected override string Endpoint => "https://api.x.ai/v1/stt";

    protected override void AddFields(MultipartFormDataContent form, TranscriptionOptions options)
    {
        form.Add(new StringContent(options.ModelId), "model");
        form.Add(new StringContent(options.Language), "language");
        // Numbers, dates and currencies in written form, as Whisper and Scribe write them,
        // so the providers are compared on the same footing.
        form.Add(new StringContent("true"), "format");
        foreach (var term in Keyterms.From(options.Vocabulary, MaxKeyterms))
            form.Add(new StringContent(term), "keyterm");
    }
}
