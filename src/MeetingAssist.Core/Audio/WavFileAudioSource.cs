namespace MeetingAssist.Core.Audio;

/// <summary>
/// Replays a fixture WAV through the same pipeline as live capture (spec FR-12.1).
/// This is what makes the central question — transcript quality versus chunk length —
/// answerable deterministically, without a microphone or a scheduled meeting.
/// </summary>
public sealed class WavFileAudioSource : IAudioSource
{
    private readonly byte[] _pcm;
    private readonly bool _realTime;
    private readonly int _frameBytes;

    public WavFileAudioSource(
        AudioChannelKind channel,
        string path,
        bool realTime = false,
        int frameMs = 50)
    {
        Channel = channel;
        Path = path;

        var (pcm, sampleRate, channels) = WavIo.ReadPcm(path);
        if (sampleRate != AudioFormat.SampleRate || channels != AudioFormat.Channels)
            throw new NotSupportedException(
                $"{path} is {sampleRate}Hz/{channels}ch; fixtures must be " +
                $"{AudioFormat.SampleRate}Hz mono. Re-record with `spike record`.");

        _pcm = pcm;
        _realTime = realTime;
        _frameBytes = AudioFormat.BytesFor(TimeSpan.FromMilliseconds(frameMs));
        Duration = AudioFormat.DurationOf(pcm.Length);
    }

    public AudioChannelKind Channel { get; }
    public string Path { get; }
    public TimeSpan Duration { get; }
    public string Description => $"{Channel}: {System.IO.Path.GetFileName(Path)} ({Duration:mm\\:ss})";

    public async IAsyncEnumerable<AudioFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;

        for (var pos = 0; pos < _pcm.Length && !ct.IsCancellationRequested; pos += _frameBytes)
        {
            var len = Math.Min(_frameBytes, _pcm.Length - pos);
            var offset = AudioFormat.DurationOf(pos);

            if (_realTime)
            {
                var due = start + offset;
                var wait = due - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            yield return new AudioFrame(Channel, offset, _pcm.AsSpan(pos, len).ToArray());
        }
    }
}
