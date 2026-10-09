using MeetingAssist.Core.Audio;

namespace MeetingAssist.Core.Transcription;

/// <summary>
/// Returns a synthetic description of each segment instead of calling a provider. Lets the
/// full pipeline — segmentation, ordering, store, prompt assembly — be exercised with no API
/// key and no network, which is how the plumbing gets verified separately from transcript
/// quality. Enabled with `--fake-stt`.
/// </summary>
public sealed class FakeTranscriber(TimeSpan? latency = null) : ITranscriber
{
    private readonly TimeSpan _latency = latency ?? TimeSpan.FromMilliseconds(120);
    private int _counter;

    public string Name => "fake";

    public event Action<bool>? AttemptCompleted;

    public async Task<TranscriptionResult?> TranscribeAsync(
        AudioSegment segment, TranscriptionOptions options, CancellationToken ct)
    {
        // Simulated round trip, with jitter, so latency instrumentation has something to show.
        var jitter = Random.Shared.Next(0, 60);
        await Task.Delay(_latency + TimeSpan.FromMilliseconds(jitter), ct).ConfigureAwait(false);

        var n = Interlocked.Increment(ref _counter);
        var rms = EnergyVad.Rms(segment.Pcm);

        var text = $"[{segment.Channel} #{n} {segment.Duration.TotalSeconds:F1}s " +
                   $"cut={segment.CutReason} rms={rms:F3}]";

        AttemptCompleted?.Invoke(true);
        return new TranscriptionResult(text, segment.Duration.TotalSeconds, 1);
    }
}
