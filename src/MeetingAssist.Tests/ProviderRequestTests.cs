using System.Net;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

/// <summary>
/// What the providers on trial against Groq are sent, and how the comparison scores them.
/// The request shapes come from each provider's docs; these tests pin them so a refactor of the
/// shared base cannot quietly change what goes over the wire.
/// </summary>
public class ProviderRequestTests
{
    private static AudioSegment Segment() =>
        new(Guid.NewGuid(), AudioChannelKind.Mic, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            Synth.Tone(TimeSpan.FromSeconds(2)), SegmentCutReason.Silence);

    private static TranscriptionOptions Options(string model) => new()
    {
        ModelId = model,
        Language = "en",
        Vocabulary = "Acme Pulse, Power BI, SOC 2"
    };

    [Fact]
    public async Task Xai_gets_a_bearer_key_and_one_keyterm_field_per_term()
    {
        var handler = new CapturingHandler("""{"text":" Hello Acme Pulse. ","duration":2.0}""");
        using var transcriber = new XaiTranscriber("test-key", new HttpClient(handler));

        var result = await transcriber.TranscribeAsync(Segment(), Options(XaiTranscriber.DefaultModel), default);

        Assert.Equal("https://api.x.ai/v1/stt", handler.Url);
        Assert.Equal("Bearer test-key", handler.Header("Authorization"));
        Assert.Equal(["grok-voice-transcribe-2.0"], handler.Fields("model"));
        Assert.Equal(["en"], handler.Fields("language"));
        Assert.Equal(["Acme Pulse", "Power BI", "SOC 2"], handler.Fields("keyterm"));
        Assert.Equal("Hello Acme Pulse.", result?.Text);
        Assert.Equal(2.0, result?.AudioSeconds);
    }

    [Fact]
    public async Task ElevenLabs_gets_its_key_header_and_no_audio_event_tags()
    {
        var handler = new CapturingHandler("""{"text":"Hello","words":[{"text":"Hello","start":0.1,"end":0.6}]}""");
        using var transcriber = new ElevenLabsTranscriber("test-key", new HttpClient(handler));

        var result = await transcriber.TranscribeAsync(Segment(), Options(ElevenLabsTranscriber.DefaultModel), default);

        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", handler.Url);
        Assert.Equal("test-key", handler.Header("xi-api-key"));
        Assert.Null(handler.Header("Authorization"));
        Assert.Equal(["scribe_v2"], handler.Fields("model_id"));
        Assert.Equal(["en"], handler.Fields("language_code"));
        Assert.Equal(["false"], handler.Fields("tag_audio_events"));
        Assert.Equal(["Acme Pulse", "Power BI", "SOC 2"], handler.Fields("keyterms"));
        Assert.Equal(0.6, result?.AudioSeconds);
    }

    [Fact]
    public void Keyterms_split_the_vocabulary_and_drop_what_a_provider_would_refuse()
    {
        var vocabulary = "Acme, Power BI;  SOC 2\nacme, " + new string('x', 51) + ", one two three four five six";

        Assert.Equal(["Acme", "Power BI", "SOC 2", "one two three four five six"], Keyterms.From(vocabulary, 100));
        Assert.Equal(["Acme", "Power BI", "SOC 2"], Keyterms.From(vocabulary, 100, maxWords: 5));
        Assert.Equal(["Acme", "Power BI"], Keyterms.From(vocabulary, 2));
    }

    [Fact]
    public void Terms_are_scored_only_where_the_reference_has_them()
    {
        var hits = Wer.Terms(
            reference: "We have SOC 2 Type Two, and Pulse runs on Snowflake.",
            hypothesis: "You: We have SOC 2, type 2 and pulse runs on snow flake.",
            terms: ["SOC 2", "Snowflake", "Power BI", "Pulse"]);

        Assert.Equal(["SOC 2", "Snowflake", "Pulse"], hits.Expected);
        Assert.Equal(["Snowflake"], hits.Missed);
        Assert.Equal(2, hits.Found);
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        private readonly Dictionary<string, List<string>> _fields = new();
        private HttpRequestMessage? _request;

        public string? Url => _request?.RequestUri?.ToString();

        public string? Header(string name) =>
            _request is not null && _request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

        public List<string> Fields(string name) => _fields.GetValueOrDefault(name) ?? [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _request = request;
            foreach (var part in (MultipartFormDataContent)request.Content!)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "file") continue;
                if (!_fields.TryGetValue(name, out var list)) _fields[name] = list = [];
                list.Add(await part.ReadAsStringAsync(ct));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
