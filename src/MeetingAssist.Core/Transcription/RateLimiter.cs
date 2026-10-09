using System.Diagnostics;

namespace MeetingAssist.Core.Transcription;

/// <summary>
/// Sliding-window request pacer.
///
/// Groq caps Whisper at 20 requests per minute on the free plan. A live two-channel meeting
/// produces roughly 15–25 requests a minute on its own, so on that plan the pacer is what keeps
/// the session inside the cap — and the backlog it builds is what the session's lag warning
/// reports. The paid plan's cap is far above what a meeting needs.
///
/// Waiting is strictly better than being refused. A 429 costs the round trip *and* loses the
/// chunk; pacing delays the chunk and keeps it. Retries count against the same budget, so the
/// limiter gates every attempt rather than every segment.
/// </summary>
public sealed class RateLimiter(int requestsPerWindow, TimeSpan? window = null)
{
    private readonly int _limit = Math.Max(1, requestsPerWindow);
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(1);
    private readonly Queue<long> _issued = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Never sleep for less than this; sub-tick waits just burn a scheduling round.</summary>
    private static readonly TimeSpan MinimumSleep = TimeSpan.FromMilliseconds(15);

    public int Limit => _limit;

    /// <summary>Cumulative time callers have spent waiting, for reporting what pacing cost.</summary>
    public TimeSpan TotalWaited { get; private set; }

    /// <summary>Requests that had to wait at all.</summary>
    public int PacedRequests { get; private set; }

    /// <summary>
    /// Blocks until a slot is free, then claims it. Returns how long the caller waited so the
    /// cost of pacing can be logged and distinguished from real provider latency.
    /// </summary>
    public async Task<TimeSpan> AcquireAsync(CancellationToken ct)
    {
        var waited = TimeSpan.Zero;

        while (true)
        {
            TimeSpan delay;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = Stopwatch.GetTimestamp();
                Trim(now);

                if (_issued.Count < _limit)
                {
                    _issued.Enqueue(now);
                    TotalWaited += waited;
                    if (waited > TimeSpan.Zero) PacedRequests++;
                    return waited;
                }

                // The oldest request in the window is the one whose expiry frees a slot.
                delay = _window - Stopwatch.GetElapsedTime(_issued.Peek(), now);
            }
            finally
            {
                _gate.Release();
            }

            if (delay < MinimumSleep) delay = MinimumSleep;
            await Task.Delay(delay, ct).ConfigureAwait(false);
            waited += delay;
        }
    }

    /// <summary>How long a batch of this size will take, for warning before a long run.</summary>
    public TimeSpan EstimateFor(int requests) =>
        requests <= _limit ? TimeSpan.Zero : _window * ((requests - 1) / _limit);

    private void Trim(long now)
    {
        while (_issued.Count > 0 && Stopwatch.GetElapsedTime(_issued.Peek(), now) >= _window)
            _issued.Dequeue();
    }
}
