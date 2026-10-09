namespace MeetingAssist.Core.Audio;

/// <summary>Which side of the conversation an audio stream represents.</summary>
public enum AudioChannelKind
{
    /// <summary>The local microphone — the user.</summary>
    Mic,

    /// <summary>System output loopback — the remote party.</summary>
    Loopback
}

/// <summary>
/// The canonical capture format for the whole pipeline: 16 kHz, mono, 16-bit PCM.
/// This is Whisper's native input format. WASAPI shared mode converts to it via
/// AutoConvertPcm, so no resampling stage is required (see PoC finding, spec FR-2.4).
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 16_000;
    public const int Bits = 16;
    public const int Channels = 1;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = SampleRate * BytesPerSample * Channels;

    public static TimeSpan DurationOf(int byteCount) =>
        TimeSpan.FromSeconds((double)byteCount / BytesPerSecond);

    public static int BytesFor(TimeSpan duration) =>
        (int)(duration.TotalSeconds * BytesPerSecond) / BytesPerSample * BytesPerSample;
}

/// <summary>
/// A block of captured audio. <paramref name="Offset"/> is measured from session start on a
/// timebase shared by both streams (QPC where available), which is what makes the two
/// channels safe to interleave.
/// </summary>
public sealed record AudioFrame(
    AudioChannelKind Channel,
    TimeSpan Offset,
    byte[] Pcm,
    bool WasapiSilent = false)
{
    public TimeSpan Duration => AudioFormat.DurationOf(Pcm.Length);
}

/// <summary>A speech segment cut at silence boundaries, ready to send to transcription.</summary>
public sealed record AudioSegment(
    Guid Id,
    AudioChannelKind Channel,
    TimeSpan Start,
    TimeSpan End,
    byte[] Pcm,
    SegmentCutReason CutReason)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Why the segmenter closed a chunk. Recorded so the sweep can show what drove cuts.</summary>
public enum SegmentCutReason
{
    /// <summary>Trailing silence exceeded the hangover — the desired case.</summary>
    Silence,

    /// <summary>Hard maximum length reached during continuous speech.</summary>
    MaxLength,

    /// <summary>An explicit FlushAsync, i.e. the user pressed Ask.</summary>
    Flush
}
