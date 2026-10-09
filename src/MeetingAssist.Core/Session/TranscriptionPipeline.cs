using System.Collections.Concurrent;
using System.Diagnostics;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Transcription;
using Serilog;

namespace MeetingAssist.Core.Session;

/// <summary>
/// One channel end to end: source -> segmenter -> transcriber -> store, with per-chunk
/// timing recorded at every hop. Owns the concurrency so <see cref="ITranscriber"/>
/// implementations stay trivial.
/// </summary>
public sealed class ChannelPipeline(
    IAudioSource source,
    Segmenter segmenter,
    ITranscriber transcriber,
    TranscriptionOptions options,
    TranscriptStore store,
    int maxConcurrentRequests = 4)
{
    private readonly NewestFirstGate _slots = new(maxConcurrentRequests);
    private int _inFlight;

    /// <summary>Segments taken but not yet transcribed, with when each was taken.</summary>
    private readonly ConcurrentDictionary<Guid, long> _pending = new();

    /// <summary>The loudest frame since the last <see cref="TakePeak"/>, as float bits.</summary>
    private int _peakBits;

    /// <summary>Segments taken off the segmenter; behind <see cref="Segmenter.Emitted"/> while one waits to be read.</summary>
    private int _taken;

    public AudioChannelKind Channel => source.Channel;
    public int SegmentsSeen { get; private set; }
    public int SegmentsTranscribed { get; private set; }
    public int SegmentsEmpty { get; private set; }
    public double AudioSecondsSent { get; private set; }

    /// <summary>
    /// What is waiting to be transcribed: how many segments, and how long ago the oldest of
    /// them was spoken. This is how far behind the transcript is.
    /// </summary>
    public (int Count, TimeSpan OldestAge) Backlog
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            var oldest = TimeSpan.Zero;
            var count = 0;
            foreach (var taken in _pending.Values)
            {
                count++;
                var age = Stopwatch.GetElapsedTime(taken, now);
                if (age > oldest) oldest = age;
            }
            return (count, oldest);
        }
    }

    /// <summary>Runs until the source is exhausted or cancelled; cancelling abandons the backlog too.</summary>
    public Task RunAsync(CancellationToken ct) => RunAsync(ct, ct);

    /// <summary>
    /// Runs until the source is exhausted or <paramref name="capture"/> is cancelled, then
    /// transcribes what is left. Only <paramref name="transcription"/> abandons that: stopping
    /// to listen must not throw away what was already heard.
    /// </summary>
    public async Task RunAsync(CancellationToken capture, CancellationToken transcription)
    {
        var consumer = ConsumeSegmentsAsync(transcription);

        try
        {
            await foreach (var frame in source.ReadAsync(capture).ConfigureAwait(false))
            {
                RecordLevel(frame);
                segmenter.Push(frame);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        finally
        {
            segmenter.Complete();
        }

        await consumer.ConfigureAwait(false);
    }

    /// <summary>
    /// The loudest sound (RMS, 0–1) heard since the last call, then reset: what the overlay's
    /// level meter shows, so the user can see audio arriving on each channel.
    /// </summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    private void RecordLevel(AudioFrame frame)
    {
        var rms = frame.WasapiSilent ? 0f : (float)EnergyVad.Rms(frame.Pcm);
        int seen;
        do
        {
            seen = Volatile.Read(ref _peakBits);
            if (rms <= BitConverter.Int32BitsToSingle(seen)) return;
        }
        while (Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(rms), seen) != seen);
    }

    private async Task ConsumeSegmentsAsync(CancellationToken ct)
    {
        var pending = new List<Task>();

        try
        {
            await foreach (var segment in segmenter.Segments.ReadAllAsync(ct).ConfigureAwait(false))
            {
                SegmentsSeen++;
                pending.Add(TranscribeAsync(segment, ct));
                pending.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        var timer = new StageTimer($"chunk.{Channel}", segment.Id.ToString("N")[..8]);

        // In flight first, then taken: a drain that sees the segment taken also sees it in flight.
        Interlocked.Increment(ref _inFlight);
        _pending[segment.Id] = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _taken);

        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            timer.Mark("queued");
            try
            {
                var result = await transcriber.TranscribeAsync(segment, options, ct).ConfigureAwait(false);

                // Time spent queued behind the provider's rate limit is not the provider's
                // response time; recording it as such would misreport spec section 5.1.
                timer.MarkExcluding("sttResponse",
                    result?.WaitedForSlot.TotalMilliseconds ?? 0, "rateLimitWait");

                if (result is null)
                {
                    SegmentsEmpty++;
                    Log.Debug("Empty transcription {Channel} {Dur:F1}s cut={Cut}",
                        Channel, segment.Duration.TotalSeconds, segment.CutReason);
                    return;
                }

                store.Append(new TranscriptSegment(
                    segment.Id, segment.Channel, segment.Start, segment.End, result.Text, segment.CutReason));

                SegmentsTranscribed++;
                AudioSecondsSent += result.AudioSeconds ?? segment.Duration.TotalSeconds;
                timer.Mark("stored");

                Log.Debug("{Timing} cut={Cut} dur={Dur:F1}s attempts={Attempts}",
                    timer.Format(), segment.CutReason, segment.Duration.TotalSeconds, result.Attempts);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        finally
        {
            _pending.TryRemove(segment.Id, out _);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>
    /// Flushes the open chunk and waits for in-flight transcription, bounded by
    /// <paramref name="timeout"/>. This is the Ask path (spec FR-6.1): it is what keeps the
    /// sentence that just ended from being missing at exactly the moment it is needed.
    /// </summary>
    public async Task<bool> FlushAndDrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        segmenter.Flush();
        var deadline = DateTimeOffset.UtcNow + timeout;

        // A segment the reader has not taken yet is not in flight either, so in-flight alone can
        // read as drained just after a flush. Waiting for every emitted segment to be taken
        // closes that gap.
        bool Busy() => Volatile.Read(ref _taken) < segmenter.Emitted || Volatile.Read(ref _inFlight) > 0;

        while (Busy() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(15, ct).ConfigureAwait(false);

        var drained = !Busy();
        if (!drained)
            Log.Warning("{Channel}: {N} transcription(s) still in flight after {Timeout}ms",
                Channel, _inFlight, timeout.TotalMilliseconds);
        return drained;
    }
}

/// <summary>
/// A semaphore that hands a freed slot to the most recent waiter rather than the oldest.
///
/// When transcription falls behind, first-come-first-served makes every Ask wait for the whole
/// backlog: in a real meeting on the free plan that reached twenty minutes, and every answer
/// was about something said long before. Newest first keeps the last few minutes — what an
/// Ask is about — current, and the older speech fills in as the backlog clears.
/// </summary>
public sealed class NewestFirstGate(int slots)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = new();
    private int _free = Math.Max(1, slots);

    public async Task WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_free > 0)
            {
                _free--;
                return;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiting.AddLast(waiter);
        }

        await using (ct.Register(() =>
                     {
                         // Only a waiter still queued is cancelled; one already handed a slot keeps it.
                         lock (_gate)
                         {
                             if (node.List is null) return;
                             _waiting.Remove(node);
                         }
                         waiter.TrySetCanceled(ct);
                     }).ConfigureAwait(false))
        {
            await waiter.Task.ConfigureAwait(false);
        }
    }

    public void Release()
    {
        TaskCompletionSource? next = null;

        lock (_gate)
        {
            if (_waiting.Last is { } newest)
            {
                _waiting.RemoveLast();
                next = newest.Value;
            }
            else
            {
                _free++;
            }
        }

        next?.TrySetResult();
    }
}
