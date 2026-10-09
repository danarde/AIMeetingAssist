namespace MeetingAssist.Core.Transcription;

/// <summary>
/// ElevenLabs Scribe (v2 released 2026-01-09). Request shape from the API reference, checked
/// 2026-10-07: multipart to /v1/speech-to-text with the key in <c>xi-api-key</c>. Key terms are
/// a list of up to 1000, each at most 50 characters and five words; they add 20% to the price.
/// The reference shows no raw multipart example for the list, so one <c>keyterms</c> field per
/// term is an assumption, borrowed from how their realtime URL repeats the parameter.
/// </summary>
public sealed class ElevenLabsTranscriber : HttpTranscriber
{
    public const string DefaultModel = "scribe_v2";

    /// <summary>No published request rate for batch; same reasoning as <see cref="XaiTranscriber"/>.</summary>
    public const int DefaultRequestsPerMinute = 120;

    private const int MaxKeyterms = 1000;
    private const int MaxWordsPerKeyterm = 5;

    public ElevenLabsTranscriber(string apiKey, HttpClient? http = null, int requestsPerMinute = DefaultRequestsPerMinute)
        : base(apiKey, "ELEVENLABS_API_KEY", http, new RateLimiter(requestsPerMinute))
    {
        Http.DefaultRequestHeaders.Add("xi-api-key", apiKey);
    }

    public override string Name => "elevenlabs";

    protected override string Endpoint => "https://api.elevenlabs.io/v1/speech-to-text";

    protected override void AddFields(MultipartFormDataContent form, TranscriptionOptions options)
    {
        form.Add(new StringContent(options.ModelId), "model_id");
        form.Add(new StringContent(options.Language), "language_code");
        // "(laughter)" and the like would land in the transcript as if someone said it.
        form.Add(new StringContent("false"), "tag_audio_events");
        foreach (var term in Keyterms.From(options.Vocabulary, MaxKeyterms, MaxWordsPerKeyterm))
            form.Add(new StringContent(term), "keyterms");
    }

    /// <summary>Scribe reports no total duration; the last word's end time stands in for it.</summary>
    protected override double? ReadDuration(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("words", out var words) || words.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        double? end = null;
        foreach (var word in words.EnumerateArray())
            if (word.TryGetProperty("end", out var e) && e.TryGetDouble(out var seconds)) end = seconds;
        return end;
    }
}
