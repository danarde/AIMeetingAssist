using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using MeetingAssist.Core.Audio;
using Serilog;

namespace MeetingAssist.Core.Transcription;

/// <summary>
/// A provider that takes one segment as a multipart upload and answers with its text. Every
/// batch provider we use has that shape, so the pacing, timeout and retry rules live here once
/// and a provider only says how to build its form and read its answer.
/// </summary>
public abstract partial class HttpTranscriber : ITranscriber, IDisposable
{
    private readonly bool _ownsHttp;

    protected HttpTranscriber(string apiKey, string keyName, HttpClient? http, RateLimiter limiter)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException($"The API key is empty. Set {keyName}.", nameof(apiKey));

        _ownsHttp = http is null;
        Http = http ?? new HttpClient();

        // One limiter per transcriber instance, and both channel pipelines share the instance,
        // so the budget is enforced across the whole session rather than per channel.
        Limiter = limiter;
    }

    /// <summary>Subclasses set their key header here: in a header only, never a URL (spec FR-10.4).</summary>
    protected HttpClient Http { get; }

    /// <summary>Shared across every channel using this transcriber.</summary>
    public RateLimiter Limiter { get; }

    public abstract string Name { get; }

    protected abstract string Endpoint { get; }

    /// <summary>Everything but the audio, which is added first as <c>file</c>.</summary>
    protected abstract void AddFields(MultipartFormDataContent form, TranscriptionOptions options);

    /// <summary>The audio length the provider reports, when it reports one.</summary>
    protected virtual double? ReadDuration(System.Text.Json.JsonElement root) =>
        root.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds) ? seconds : null;

    public event Action<bool>? AttemptCompleted;

    public async Task<TranscriptionResult?> TranscribeAsync(
        AudioSegment segment, TranscriptionOptions options, CancellationToken ct)
    {
        var wav = WavIo.WrapPcm(segment.Pcm);
        var pacedFor = TimeSpan.Zero;

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                // Claim a slot before spending the round trip. Retries pass through here too,
                // since a retry is just as chargeable as a first attempt.
                var waited = await Limiter.AcquireAsync(ct).ConfigureAwait(false);
                pacedFor += waited;
                if (waited > TimeSpan.FromMilliseconds(250))
                {
                    Log.Debug("Paced segment {Id}: waited {WaitedMs:F0}ms for a rate-limit slot",
                        segment.Id.ToString()[..8], waited.TotalMilliseconds);
                }

                // The timeout covers the request, not the queue. Started before the wait, a
                // segment paced for longer than it timed out before it was ever sent — and
                // still spent the slot and the attempt, which under sustained load fed the
                // backlog that caused it.
                cts.CancelAfter(options.Timeout);

                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(wav);
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "chunk.wav");
                AddFields(form, options);

                using var response = await Http.PostAsync(Endpoint, form, cts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (!IsRetryable(response.StatusCode) || attempt == options.MaxAttempts)
                    {
                        Log.Warning("{Provider} {Status} for segment {Id}: {Body}",
                            Name, (int)response.StatusCode, segment.Id.ToString()[..8], Truncate(body, 300));
                        AttemptCompleted?.Invoke(false);
                        return null;
                    }

                    // A 429 means the pacer's model of the budget is behind the server's —
                    // clock skew, or another process sharing the key. The server states how
                    // long to wait; guessing shorter just burns another request.
                    var hint = RetryAfter(response, body);
                    if (hint is not null)
                    {
                        Log.Debug("{Provider} {Status} for segment {Id}; honouring retry-after {DelayMs:F0}ms",
                            Name, (int)response.StatusCode, segment.Id.ToString()[..8], hint.Value.TotalMilliseconds);
                        await Task.Delay(Cap(hint.Value), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    }
                    continue;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var duration = ReadDuration(doc.RootElement);

                text = text.Trim();

                // Reported healthy even when the text is empty: the provider answered, and a
                // silent two seconds is a correct answer rather than a failure.
                AttemptCompleted?.Invoke(true);

                return text.Length == 0 ? null : new TranscriptionResult(text, duration, attempt, pacedFor);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < options.MaxAttempts)
            {
                Log.Debug(ex, "{Provider} attempt {Attempt} failed for segment {Id}",
                    Name, attempt, segment.Id.ToString()[..8]);
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failing provider must not stop capture (spec FR-4.8) — log and drop the chunk.
                Log.Warning(ex, "{Provider} gave up on segment {Id} after {Attempts} attempts",
                    Name, segment.Id.ToString()[..8], options.MaxAttempts);
                AttemptCompleted?.Invoke(false);
                return null;
            }
        }

        AttemptCompleted?.Invoke(false);
        return null;
    }

    private static bool IsRetryable(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    /// <summary>
    /// How long the server asked us to wait. A <c>retry-after</c> header first; Groq also states
    /// the delay in the error body ("Please try again in 3s"), which is parsed as a fallback so
    /// a missing header does not cost us the hint.
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response, string body)
    {
        if (response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta && delta > TimeSpan.Zero) return delta;
            if (header.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                if (until > TimeSpan.Zero) return until;
            }
        }

        var match = RetryHint().Match(body);
        if (!match.Success) return null;

        var seconds = 0d;
        if (match.Groups["m"].Success) seconds += double.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60;
        if (match.Groups["s"].Success) seconds += double.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Matches "try again in 3s", "try again in 1m30s", "try again in 2.5s".</summary>
    [GeneratedRegex(@"try again in\s*(?:(?<m>[\d.]+)m)?(?:(?<s>[\d.]+)s)?", RegexOptions.IgnoreCase)]
    private static partial Regex RetryHint();

    /// <summary>
    /// A server can name a delay longer than the chunk is worth waiting for. Past this the
    /// segment is better dropped than allowed to stall the pipeline behind it.
    /// </summary>
    private static TimeSpan Cap(TimeSpan delay) =>
        delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var baseMs = 200 * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.Next(0, 120);
        return Task.Delay(TimeSpan.FromMilliseconds(baseMs + jitter), ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        // An injected HttpClient belongs to the caller.
        if (_ownsHttp) Http.Dispose();
        GC.SuppressFinalize(this);
    }
}
