using MeetingAssist.Core.Audio;

namespace MeetingAssist.Tests;

public class SegmenterTests
{
    private static SegmenterOptions Opts(double minChunk = 2, double maxChunk = 8, double hangoverMs = 400) => new()
    {
        MinChunk = TimeSpan.FromSeconds(minChunk),
        MaxChunk = TimeSpan.FromSeconds(maxChunk),
        Hangover = TimeSpan.FromMilliseconds(hangoverMs),
        MinFlushSpeech = TimeSpan.FromMilliseconds(300),
        Vad = new VadOptions { Adaptive = false, AbsoluteFloor = 0.02 }
    };

    [Fact]
    public void Silence_alone_never_produces_a_segment()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts());
        Synth.PushInFrames(seg, Synth.Silence(TimeSpan.FromSeconds(10)), AudioChannelKind.Mic);

        Assert.Empty(Synth.DrainSegments(seg));
    }

    [Fact]
    public void Speech_followed_by_silence_cuts_at_the_silence_boundary()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts(minChunk: 1));
        var audio = Synth.Concat(
            Synth.Tone(TimeSpan.FromSeconds(2)),
            Synth.Silence(TimeSpan.FromSeconds(1)));

        Synth.PushInFrames(seg, audio, AudioChannelKind.Mic);

        var segments = Synth.DrainSegments(seg);
        var one = Assert.Single(segments);
        Assert.Equal(SegmentCutReason.Silence, one.CutReason);
        // Roughly the speech plus the retained tail, not the whole silent stretch.
        Assert.InRange(one.Duration.TotalSeconds, 1.9, 2.8);
    }

    [Fact]
    public void Short_speech_does_not_cut_before_MinChunk()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts(minChunk: 3));
        var audio = Synth.Concat(
            Synth.Tone(TimeSpan.FromSeconds(1)),
            Synth.Silence(TimeSpan.FromSeconds(1)));

        Synth.PushInFrames(seg, audio, AudioChannelKind.Mic);

        Assert.Empty(Synth.DrainSegments(seg));
    }

    [Fact]
    public void Continuous_speech_is_cut_at_MaxChunk()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts(minChunk: 2, maxChunk: 3));
        Synth.PushInFrames(seg, Synth.Tone(TimeSpan.FromSeconds(7)), AudioChannelKind.Mic);

        var segments = Synth.DrainSegments(seg);
        Assert.True(segments.Count >= 2, $"expected repeated max-length cuts, got {segments.Count}");
        Assert.All(segments, s => Assert.Equal(SegmentCutReason.MaxLength, s.CutReason));
        Assert.All(segments, s => Assert.InRange(s.Duration.TotalSeconds, 2.9, 3.2));
    }

    [Fact]
    public void Flush_emits_the_open_chunk_before_MinChunk_is_reached()
    {
        // This is the mechanism the whole Ask interaction rests on (spec D7): without it the
        // sentence that just ended is the one missing from the transcript.
        var seg = new Segmenter(AudioChannelKind.Mic, Opts(minChunk: 5));
        Synth.PushInFrames(seg, Synth.Tone(TimeSpan.FromSeconds(1.5)), AudioChannelKind.Mic);

        Assert.Empty(Synth.DrainSegments(seg));

        var flushed = seg.Flush();

        Assert.NotNull(flushed);
        Assert.Equal(SegmentCutReason.Flush, flushed.CutReason);
        Assert.InRange(flushed.Duration.TotalSeconds, 1.2, 1.8);
    }

    [Fact]
    public void Flush_with_too_little_speech_returns_null_and_keeps_the_audio()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts(minChunk: 5));
        Synth.PushInFrames(seg, Synth.Tone(TimeSpan.FromMilliseconds(120)), AudioChannelKind.Mic);

        Assert.Null(seg.Flush());

        // The audio is retained, not dropped: more speech then completes into a real segment.
        Synth.PushInFrames(seg, Synth.Tone(TimeSpan.FromSeconds(1)), AudioChannelKind.Mic);
        var flushed = seg.Flush();

        Assert.NotNull(flushed);
        Assert.True(flushed.Duration.TotalSeconds > 1.0);
    }

    [Fact]
    public void Leading_silence_is_trimmed_to_a_short_preroll()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts(minChunk: 1));
        var audio = Synth.Concat(
            Synth.Silence(TimeSpan.FromSeconds(5)),
            Synth.Tone(TimeSpan.FromSeconds(1.5)),
            Synth.Silence(TimeSpan.FromSeconds(1)));

        Synth.PushInFrames(seg, audio, AudioChannelKind.Mic);

        var one = Assert.Single(Synth.DrainSegments(seg));
        // Five seconds of leading silence must not be uploaded to the STT provider.
        Assert.True(one.Duration.TotalSeconds < 2.5,
            $"expected leading silence to be trimmed, segment was {one.Duration.TotalSeconds:F2}s");
    }

    [Fact]
    public void Complete_emits_trailing_speech()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts(minChunk: 5));
        Synth.PushInFrames(seg, Synth.Tone(TimeSpan.FromSeconds(2)), AudioChannelKind.Mic);

        seg.Complete();

        Assert.Single(Synth.DrainSegments(seg));
    }

    // Loopback capture delivers nothing while nothing plays, so the frames of two lines spoken a
    // minute apart arrive back to back, distinguishable only by their stamps (2026-10-08).

    [Fact]
    public void Lines_with_nothing_heard_between_them_keep_their_real_times_and_stay_apart()
    {
        var seg = new Segmenter(AudioChannelKind.Loopback, Opts());

        PushAt(seg, Synth.Tone(TimeSpan.FromSeconds(1.5)), TimeSpan.Zero);
        PushAt(seg, Synth.Concat(Synth.Tone(TimeSpan.FromSeconds(1.5)), Synth.Silence(TimeSpan.FromSeconds(1))),
            TimeSpan.FromSeconds(60));

        var segments = Synth.DrainSegments(seg);
        Assert.Equal(2, segments.Count);
        Assert.Equal(0, segments[0].Start.TotalSeconds, precision: 1);
        Assert.Equal(1.5, segments[0].End.TotalSeconds, precision: 1);
        Assert.Equal(60, segments[1].Start.TotalSeconds, precision: 1);
    }

    [Fact]
    public void A_click_before_a_gap_is_dropped_rather_than_glued_to_the_next_line()
    {
        var seg = new Segmenter(AudioChannelKind.Loopback, Opts());

        PushAt(seg, Synth.Tone(TimeSpan.FromMilliseconds(100)), TimeSpan.Zero);
        PushAt(seg, Synth.Concat(Synth.Tone(TimeSpan.FromSeconds(2)), Synth.Silence(TimeSpan.FromSeconds(1))),
            TimeSpan.FromSeconds(30));

        var segment = Assert.Single(Synth.DrainSegments(seg));
        Assert.Equal(30, segment.Start.TotalSeconds, precision: 1);
    }

    [Fact]
    public void A_segmenter_started_mid_meeting_stamps_from_the_meeting_clock()
    {
        // Resuming after a pause, or rebuilding a failed device, starts a new segmenter.
        var seg = new Segmenter(AudioChannelKind.Mic, Opts());

        PushAt(seg, Synth.Concat(Synth.Tone(TimeSpan.FromSeconds(2)), Synth.Silence(TimeSpan.FromSeconds(1))),
            TimeSpan.FromMinutes(12));

        var segment = Assert.Single(Synth.DrainSegments(seg));
        Assert.Equal(720, segment.Start.TotalSeconds, precision: 1);
    }

    [Fact]
    public void Jitter_between_frames_shorter_than_the_hangover_does_not_split_speech()
    {
        var seg = new Segmenter(AudioChannelKind.Mic, Opts());
        var frame = Synth.Tone(TimeSpan.FromMilliseconds(50));

        // Three seconds of speech whose frames each arrive stamped 100 ms late.
        for (var i = 0; i < 60; i++)
            seg.Push(new AudioFrame(AudioChannelKind.Mic, TimeSpan.FromMilliseconds(i * 150), frame));
        PushAt(seg, Synth.Silence(TimeSpan.FromSeconds(1)), TimeSpan.FromMilliseconds(60 * 150));

        var segment = Assert.Single(Synth.DrainSegments(seg));
        Assert.InRange(segment.Duration.TotalSeconds, 3.0, 3.2); // plus the trailing silence kept
    }

    /// <summary>Feeds audio in 50 ms frames stamped from <paramref name="at"/>, as live capture does.</summary>
    private static void PushAt(Segmenter segmenter, byte[] pcm, TimeSpan at)
    {
        var frameBytes = AudioFormat.BytesFor(TimeSpan.FromMilliseconds(50));
        for (var pos = 0; pos < pcm.Length; pos += frameBytes)
        {
            var len = Math.Min(frameBytes, pcm.Length - pos);
            segmenter.Push(new AudioFrame(AudioChannelKind.Mic, at + AudioFormat.DurationOf(pos), pcm.AsSpan(pos, len).ToArray()));
        }
    }
}

public class EnergyVadTests
{
    [Fact]
    public void Silence_is_not_speech()
    {
        var vad = new EnergyVad(new VadOptions { Adaptive = false });
        Assert.False(vad.IsSpeech(Synth.Silence(TimeSpan.FromMilliseconds(20))));
    }

    [Fact]
    public void A_loud_tone_is_speech()
    {
        var vad = new EnergyVad(new VadOptions { Adaptive = false });
        Assert.True(vad.IsSpeech(Synth.Tone(TimeSpan.FromMilliseconds(20))));
    }

    [Fact]
    public void Rms_is_zero_for_silence_and_positive_for_tone()
    {
        Assert.Equal(0, EnergyVad.Rms(Synth.Silence(TimeSpan.FromMilliseconds(20))));
        Assert.True(EnergyVad.Rms(Synth.Tone(TimeSpan.FromMilliseconds(20))) > 0.1);
    }

    [Fact]
    public void Adaptive_floor_rejects_steady_low_level_noise()
    {
        // A fixed threshold is the usual reason naive energy VAD disappoints across devices.
        var vad = new EnergyVad(new VadOptions { Adaptive = true, AbsoluteFloor = 0.001, NoiseFactor = 3 });
        var hiss = Synth.Tone(TimeSpan.FromMilliseconds(20), amplitude: 0.01);

        for (var i = 0; i < 50; i++) vad.IsSpeech(hiss);

        Assert.False(vad.IsSpeech(hiss));
        Assert.True(vad.IsSpeech(Synth.Tone(TimeSpan.FromMilliseconds(20), amplitude: 0.3)));
    }
}
