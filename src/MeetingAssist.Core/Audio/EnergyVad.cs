namespace MeetingAssist.Core.Audio;

public sealed class VadOptions
{
    /// <summary>Analysis window. 20 ms is the usual granularity for speech activity.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Absolute RMS floor (0..1). Anything below this is silence regardless of the adaptive
    /// noise estimate — stops a dead-quiet stream from adapting its way into hearing speech.
    /// </summary>
    public double AbsoluteFloor { get; set; } = 0.004;

    /// <summary>Speech requires RMS above (noise floor x this). Higher = less sensitive.</summary>
    public double NoiseFactor { get; set; } = 3.0;

    /// <summary>
    /// Adapt the noise floor to the stream. A fixed threshold does not survive different
    /// microphones and room levels, which is the usual reason naive energy VAD disappoints.
    /// </summary>
    public bool Adaptive { get; set; } = true;

    public VadOptions Clone() => (VadOptions)MemberwiseClone();
}

/// <summary>
/// Energy-based voice activity detection with an adaptive noise floor.
/// Deliberately simple: spec FR-3.6 allows substituting a model-based VAD (Silero) behind the
/// same call site if the PoC shows this cutting badly.
/// </summary>
public sealed class EnergyVad(VadOptions? options = null)
{
    private readonly VadOptions _opt = options ?? new VadOptions();
    private double _noiseFloor = 0.005;

    public int WindowBytes => AudioFormat.BytesFor(_opt.Window);

    public double NoiseFloor => _noiseFloor;

    /// <summary>True if the window contains speech.</summary>
    public bool IsSpeech(ReadOnlySpan<byte> pcmWindow)
    {
        var rms = Rms(pcmWindow);
        var threshold = Math.Max(_opt.AbsoluteFloor, _noiseFloor * _opt.NoiseFactor);
        var speech = rms > threshold;

        if (_opt.Adaptive && !speech)
        {
            // Track the quiet level, rising slowly and falling fast, so a burst of noise does
            // not permanently desensitise the detector.
            var alpha = rms < _noiseFloor ? 0.25 : 0.02;
            _noiseFloor = (1 - alpha) * _noiseFloor + alpha * rms;
        }

        return speech;
    }

    /// <summary>Root-mean-square amplitude of a 16-bit PCM window, normalised to 0..1.</summary>
    public static double Rms(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2) return 0;
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length) / short.MaxValue;
    }
}
