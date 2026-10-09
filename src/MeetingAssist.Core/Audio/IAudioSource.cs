namespace MeetingAssist.Core.Audio;

/// <summary>
/// A source of 16 kHz mono PCM frames for one channel. Live capture and fixture playback both
/// implement this, which is what lets the whole downstream pipeline be exercised offline
/// (spec FR-12.1) with nothing but the source swapped.
/// </summary>
public interface IAudioSource
{
    AudioChannelKind Channel { get; }
    string Description { get; }
    IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Shared origin for timestamps across both channels. WASAPI reports a QPC position per
/// buffer, and QPC is system-wide, so using it (rather than arrival time on our own threads)
/// makes cross-channel interleaving correct rather than approximately correct.
/// </summary>
public sealed class TimeBase
{
    private long _originQpc = -1;

    public DateTimeOffset SessionStartUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>QPC positions are in 100 ns units.</summary>
    private const double TicksPerMs = 10_000.0;

    /// <summary>
    /// Converts a QPC position to an offset from session start. The first position seen on
    /// any channel defines the origin.
    /// </summary>
    public TimeSpan Offset(long qpcPosition)
    {
        var origin = Interlocked.CompareExchange(ref _originQpc, qpcPosition, -1);
        if (origin == -1) origin = qpcPosition;
        var ms = (qpcPosition - origin) / TicksPerMs;
        return ms < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
    }

    public bool HasOrigin => Interlocked.Read(ref _originQpc) != -1;
}
