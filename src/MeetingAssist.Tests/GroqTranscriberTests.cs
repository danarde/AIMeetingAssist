using System.Net;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

/// <summary>
/// What the transcriber does when the provider is unhelpful. The distinction that matters most
/// here is between "nobody spoke" and "nobody answered": both produce no text, and confusing
/// them is what FR-7.6 exists to prevent — a green dot over an empty transcript looks exactly
/// like a quiet room.
/// </summary>
public class GroqTranscriberTests
{
    private static AudioSegment Segment() =>
        new(Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            Synth.Tone(TimeSpan.FromSeconds(2)), SegmentCutReason.Silence);

    private static (GroqTranscriber Transcriber, ScriptedHandler Handler, List<bool> Health) Build(
        params HttpResponseMessage[] responses)
    {
        var handler = new ScriptedHandler(responses);
        var transcriber = new GroqTranscriber("test-key", new HttpClient(handler));
        var health = new List<bool>();
        transcriber.AttemptCompleted += health.Add;
        return (transcriber, handler, health);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task Silence_is_reported_healthy_rather_than_as_a_failure()
    {
        // Two seconds of nobody talking is a correct answer, not an outage. The whole health
        // indicator rests on this: ResilienceTests assumes it, and nothing else proves it.
        var (transcriber, _, health) = Build(Json(HttpStatusCode.OK, """{"text":""}"""));
        using var _t = transcriber;

        var result = await transcriber.TranscribeAsync(Segment(), new TranscriptionOptions(), default);

        Assert.Null(result);
        Assert.Equal([true], health);
    }

    [Fact]
    public async Task A_rejected_key_drops_the_chunk_and_reports_unhealthy()
    {
        // A failing provider must never stop capture (spec FR-4.8) — the chunk is lost, the
        // meeting is not. A 401 is not retryable, so it must not burn the other attempts.
        var (transcriber, handler, health) = Build(
            Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key"}}"""));
        using var _t = transcriber;

        var result = await transcriber.TranscribeAsync(Segment(), new TranscriptionOptions(), default);

        Assert.Null(result);
        Assert.Equal([false], health);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_429_is_retried_using_the_delay_the_server_names()
    {
        // Groq states the wait in the error body as well as the header, and the body is parsed
        // as a fallback. That parser is a regex over someone else's prose — the same species of
        // fragility as the assistant's error-string match, and nothing else exercises it.
        var throttled = Json(HttpStatusCode.TooManyRequests,
            """{"error":{"message":"Rate limit reached. Please try again in 0.05s"}}""");

        var (transcriber, handler, health) = Build(
            throttled,
            Json(HttpStatusCode.OK, """{"text":"the customer asked about pricing","duration":2.0}"""));
        using var _t = transcriber;

        var result = await transcriber.TranscribeAsync(Segment(), new TranscriptionOptions(), default);

        Assert.NotNull(result);
        Assert.Equal("the customer asked about pricing", result.Text);
        Assert.Equal(2, handler.Requests);
        Assert.Equal([true], health);
    }

    [Fact]
    public async Task Waiting_for_a_rate_limit_slot_does_not_count_against_the_request_timeout()
    {
        // In a busy two-sided meeting the pacer can hold a segment longer than the request
        // timeout. That wait must not expire the attempt: it used to, so the request was never
        // sent, the slot and the attempt were both spent, and the retry queued again.
        var handler = new ScriptedHandler([
            Json(HttpStatusCode.OK, """{"text":"first"}"""),
            Json(HttpStatusCode.OK, """{"text":"second"}""")
        ]);
        using var transcriber = new GroqTranscriber(
            "test-key", new HttpClient(handler), new RateLimiter(1, TimeSpan.FromMilliseconds(500)));
        var options = new TranscriptionOptions { Timeout = TimeSpan.FromMilliseconds(150) };

        await transcriber.TranscribeAsync(Segment(), options, default);
        var paced = await transcriber.TranscribeAsync(Segment(), options, default);

        Assert.NotNull(paced);
        Assert.Equal("second", paced.Text);
        Assert.True(paced.WaitedForSlot > options.Timeout, "the test did not actually pace past the timeout");
        Assert.Equal(1, paced.Attempts);
    }

    /// <summary>Hands back queued responses in order, and counts what was asked of it.</summary>
    private sealed class ScriptedHandler(HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // As the real handler would: an expired attempt never reaches the server.
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":""}""") });
        }
    }
}
