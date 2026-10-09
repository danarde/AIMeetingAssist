using MeetingAssist.Core.Audio;

namespace MeetingAssist.Tests;

/// <summary>
/// Synthetic 16 kHz mono PCM. Lets the segmenter and VAD be tested deterministically without
/// a microphone — the same reason the pipeline supports fixture playback.
/// </summary>
public static class Synth
{
    public static byte[] Silence(TimeSpan duration) => new byte[AudioFormat.BytesFor(duration)];

    /// <summary>A tone loud enough to read as speech to an energy VAD.</summary>
    public static byte[] Tone(TimeSpan duration, double amplitude = 0.3, double frequency = 220)
    {
        var bytes = AudioFormat.BytesFor(duration);
        var pcm = new byte[bytes];
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan());

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / AudioFormat.SampleRate;
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * t) * amplitude * short.MaxValue);
        }
        return pcm;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    public static AudioFrame Frame(byte[] pcm, AudioChannelKind channel = AudioChannelKind.Mic) =>
        new(channel, TimeSpan.Zero, pcm);

    /// <summary>Feeds audio in small frames, the way live capture delivers it.</summary>
    public static void PushInFrames(Segmenter segmenter, byte[] pcm, AudioChannelKind channel, int frameMs = 50)
    {
        var frameBytes = AudioFormat.BytesFor(TimeSpan.FromMilliseconds(frameMs));
        for (var pos = 0; pos < pcm.Length; pos += frameBytes)
        {
            var len = Math.Min(frameBytes, pcm.Length - pos);
            segmenter.Push(new AudioFrame(channel, AudioFormat.DurationOf(pos), pcm.AsSpan(pos, len).ToArray()));
        }
    }

    public static List<AudioSegment> DrainSegments(Segmenter segmenter)
    {
        var result = new List<AudioSegment>();
        while (segmenter.Segments.TryRead(out var segment)) result.Add(segment);
        return result;
    }
}
