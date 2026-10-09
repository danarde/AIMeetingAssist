using System.Threading.Channels;
using MeetingAssist.Core.Diagnostics;

namespace MeetingAssist.Core.Audio;

public sealed class SegmenterOptions
{
    /// <summary>
    /// A silence cut is only allowed once the chunk is at least this long. Combined with
    /// <see cref="MaxChunk"/> this is what produces "2-3 s chunks": cut at the first silence
    /// after 2 s. THE CENTRAL TUNABLE OF THE POC — see `spike sweep`.
    /// </summary>
    public TimeSpan MinChunk { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Hard cap, forcing a cut during continuous speech. Without it a long monologue produces
    /// an unboundedly long chunk and a large latency spike.
    /// </summary>
    public TimeSpan MaxChunk { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How much trailing silence signals end-of-utterance.</summary>
    public TimeSpan Hangover { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A flush carrying less speech than this is not sent. Whisper hallucinates on very short
    /// fragments, and a flush happens at the worst possible moment to inject garbage.
    /// The audio is retained, not dropped — it simply stays in the buffer.
    /// </summary>
    public TimeSpan MinFlushSpeech { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Audio kept before the first detected speech, so word onsets are not clipped.</summary>
    public TimeSpan PreRoll { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Trailing silence kept on a cut, so word endings are not clipped.</summary>
    public TimeSpan TrailingKeep { get; set; } = TimeSpan.FromMilliseconds(150);

    public VadOptions Vad { get; set; } = new();

    public SegmenterOptions Clone()
    {
        var c = (SegmenterOptions)MemberwiseClone();
        c.Vad = Vad.Clone();
        return c;
    }
}

/// <summary>
/// Cuts a continuous audio stream into speech segments at silence boundaries, and supports an
/// explicit <see cref="FlushAsync"/> so pressing Ask does not have to wait for the current
/// chunk to fill (spec D7 — this is what keeps the just-spoken sentence out of the gap).
/// </summary>
public sealed class Segmenter
{
    private readonly SegmenterOptions _opt;
    private readonly EnergyVad _vad;
    private readonly Channel<AudioSegment> _out;
    private readonly List<byte> _buffer = [];
    private readonly Lock _gate = new();

    private TimeSpan _bufferStart;
    private TimeSpan _streamPos;
    private int _speechBytes;
    private int _trailingSilenceBytes;
    private byte[] _pending = [];
    private int _emitted;
    private TimeSpan? _lastFrameEnd;

    public Segmenter(AudioChannelKind channel, SegmenterOptions? options = null)
    {
        Channel = channel;
        _opt = options ?? new SegmenterOptions();
        _vad = new EnergyVad(_opt.Vad);
        _out = System.Threading.Channels.Channel.CreateUnbounded<AudioSegment>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    public AudioChannelKind Channel { get; }
    public ChannelReader<AudioSegment> Segments => _out.Reader;
    public SegmenterOptions Options => _opt;

    /// <summary>Segments written to <see cref="Segments"/> so far, for a reader to tell whether it has caught up.</summary>
    public int Emitted => Volatile.Read(ref _emitted);

    /// <summary>Feeds captured audio. Emits zero or more segments as boundaries are found.</summary>
    public void Push(AudioFrame frame)
    {
        lock (_gate)
        {
            FollowClock(frame);

            var data = _pending.Length == 0 ? frame.Pcm : [.. _pending, .. frame.Pcm];
            var windowBytes = _vad.WindowBytes;
            var offset = 0;

            while (offset + windowBytes <= data.Length)
            {
                ProcessWindow(data.AsSpan(offset, windowBytes));
                offset += windowBytes;
            }

            _pending = data.AsSpan(offset).ToArray();
        }
    }

    /// <summary>
    /// Keeps the stream position on the session clock, which every frame is stamped with.
    ///
    /// Between frames the position advances by the audio received, which is exact. But loopback
    /// capture delivers nothing at all while nothing is playing: in a mock meeting the voice
    /// only plays while it speaks, and counting audio alone stopped that channel's clock between
    /// lines. Four minutes of a mock meeting were stamped into its first 30 seconds, its lines
    /// sorted in among the user's, and lines minutes apart glued into one chunk (2026-10-08).
    ///
    /// So the first frame sets the position, which also puts a segmenter rebuilt after a pause
    /// or a device failure where the meeting really is rather than back at zero. A frame that
    /// starts at least a hangover after the previous one ended marks a gap: nothing was heard,
    /// which is silence, so an open utterance ends there and the position jumps to the frame.
    /// Gaps are measured between neighbouring frames, so the slow drift between a device clock
    /// and the system clock never adds up to one. A stamp that goes backwards is ignored, as a
    /// source with no real clock stamps every frame zero.
    /// </summary>
    private void FollowClock(AudioFrame frame)
    {
        var previousEnd = _lastFrameEnd;
        _lastFrameEnd = frame.Offset + frame.Duration;

        if (previousEnd is null)
        {
            _streamPos = frame.Offset;
            _bufferStart = frame.Offset;
            return;
        }

        if (frame.Offset - previousEnd.Value < _opt.Hangover) return;

        // Enough speech for a flush is enough to keep; less is a click, not worth a request.
        if (AudioFormat.DurationOf(_speechBytes) >= _opt.MinFlushSpeech)
            Emit(SegmentCutReason.Silence, trimTrailing: true);

        _buffer.Clear();
        _pending = [];
        _speechBytes = 0;
        _trailingSilenceBytes = 0;
        _streamPos = frame.Offset;
        _bufferStart = frame.Offset;
    }

    private void ProcessWindow(ReadOnlySpan<byte> window)
    {
        var isSpeech = _vad.IsSpeech(window);

        if (_buffer.Count == 0) _bufferStart = _streamPos;
        _buffer.AddRange(window);
        _streamPos += AudioFormat.DurationOf(window.Length);

        if (isSpeech)
        {
            _speechBytes += window.Length;
            _trailingSilenceBytes = 0;
        }
        else
        {
            _trailingSilenceBytes += window.Length;
        }

        var bufferedDuration = AudioFormat.DurationOf(_buffer.Count);

        // Nothing but silence so far: keep only a short pre-roll rather than accumulating.
        if (_speechBytes == 0)
        {
            var preRollBytes = AudioFormat.BytesFor(_opt.PreRoll);
            if (_buffer.Count > preRollBytes)
            {
                var excess = _buffer.Count - preRollBytes;
                _buffer.RemoveRange(0, excess);
                _bufferStart += AudioFormat.DurationOf(excess);
            }
            return;
        }

        if (bufferedDuration >= _opt.MaxChunk)
        {
            Emit(SegmentCutReason.MaxLength, trimTrailing: false);
            return;
        }

        var silenceLongEnough = AudioFormat.DurationOf(_trailingSilenceBytes) >= _opt.Hangover;
        if (silenceLongEnough && bufferedDuration >= _opt.MinChunk)
            Emit(SegmentCutReason.Silence, trimTrailing: true);
    }

    /// <summary>
    /// Closes and emits the open chunk immediately (spec FR-3.4). Returns the segment, or null
    /// when there was not enough speech to be worth transcribing — in which case the audio is
    /// retained for the next natural cut rather than discarded.
    /// </summary>
    public AudioSegment? Flush()
    {
        lock (_gate)
        {
            if (AudioFormat.DurationOf(_speechBytes) < _opt.MinFlushSpeech) return null;
            return Emit(SegmentCutReason.Flush, trimTrailing: true);
        }
    }

    public Task<AudioSegment?> FlushAsync(CancellationToken ct = default) => Task.FromResult(Flush());

    private AudioSegment? Emit(SegmentCutReason reason, bool trimTrailing)
    {
        var length = _buffer.Count;
        if (trimTrailing && _trailingSilenceBytes > 0)
        {
            var keep = AudioFormat.BytesFor(_opt.TrailingKeep);
            var trim = Math.Max(0, _trailingSilenceBytes - keep);
            length = Math.Max(AudioFormat.BytesFor(_opt.MinFlushSpeech), _buffer.Count - trim);
            length = Math.Min(length, _buffer.Count);
        }

        var pcm = new byte[length];
        _buffer.CopyTo(0, pcm, 0, length);

        var segment = new AudioSegment(
            Guid.NewGuid(),
            Channel,
            _bufferStart,
            _bufferStart + AudioFormat.DurationOf(length),
            pcm,
            reason);

        _buffer.Clear();
        _bufferStart = _streamPos;
        _speechBytes = 0;
        _trailingSilenceBytes = 0;

        LatencyStats.Record($"segment.{Channel}.durationMs", segment.Duration.TotalMilliseconds);
        if (_out.Writer.TryWrite(segment)) Interlocked.Increment(ref _emitted);
        return segment;
    }

    /// <summary>Emits whatever is buffered and closes the stream. Used at end of a fixture.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_speechBytes > 0) Emit(SegmentCutReason.Silence, trimTrailing: true);
            _out.Writer.TryComplete();
        }
    }
}
