using NAudio.Wave;
using Serilog;

namespace MeetingAssist.Core.Audio;

/// <summary>
/// Live WASAPI capture for one channel, built on NAudio 3's <see cref="WasapiRecorderBuilder"/>.
///
/// Two properties of this API matter to the design:
///   * <c>WithFormat</c> in shared mode is honoured via the audio engine's AutoConvertPcm, so
///     16 kHz mono is captured directly and no resampling stage is needed.
///   * <c>CaptureAsync</c> yields buffers that are heap-allocated copies, safe to retain —
///     which removes the buffer-ownership hazard the zero-copy DataAvailable path has.
/// </summary>
public sealed class WasapiAudioSource(
    AudioChannelKind channel,
    TimeBase timeBase,
    int bufferLengthMs = 50,
    string? deviceId = null) : IAudioSource
{
    public AudioChannelKind Channel => channel;

    public string Description { get; private set; } = $"{channel} (not started)";

    public async IAsyncEnumerable<AudioFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var builder = new WasapiRecorderBuilder()
            .WithFormat(new WaveFormat(AudioFormat.SampleRate, AudioFormat.Bits, AudioFormat.Channels))
            .WithBufferLength(bufferLengthMs)
            .WithMmcssThreadPriority("Capture");

        // A pinned device is mutually exclusive with WithDefaultDeviceStreamRouting: NAudio
        // rejects the combination, and the semantics conflict anyway. Pinning therefore trades
        // automatic recovery for a guaranteed device (spec FR-2.5) — if that device disappears
        // the channel fails and the session's retry has to re-acquire it.
        //
        // A pinned device that is no longer present falls back to the default rather than
        // failing the session: an unplugged headset must not be the reason a meeting is not
        // recorded.
        // Disposed after the recorder below, which is declared later and so unwinds first.
        using var pinned = AudioDevices.TryResolve(deviceId);

        if (pinned is not null)
        {
            builder = builder.WithDevice(pinned);
            if (channel == AudioChannelKind.Loopback) builder = builder.WithLoopbackCapture();
        }
        else
        {
            if (deviceId is { Length: > 0 })
                Log.Warning("{Channel}: pinned device is unavailable, falling back to the Windows default", channel);

            if (channel == AudioChannelKind.Loopback)
            {
                // Loopback follows the default render device (what the meeting app is playing).
                builder = builder.WithLoopbackCapture();
            }
            else
            {
                // Survive the user switching or unplugging their microphone mid-session (FR-2.6).
                builder = builder.WithDefaultDeviceStreamRouting();
            }
        }

        // BuildAsync, not Build: WithDefaultDeviceStreamRouting activates asynchronously and
        // Build() throws outright when it is configured. For every other configuration
        // BuildAsync simply wraps Build, so this is the safe call for both channels.
        using var recorder = await builder.BuildAsync().ConfigureAwait(false);

        Description = $"{channel}: {recorder.DeviceFriendlyName} @ {recorder.WaveFormat} " +
                      $"latency={recorder.LatencyMilliseconds}ms";
        Log.Information("Capture started {Channel}: device={Device} format={Format} latencyMs={Latency}",
            channel, recorder.DeviceFriendlyName, recorder.WaveFormat.ToString(), recorder.LatencyMilliseconds);

        // If the engine could not give us 16 kHz mono, everything downstream would silently
        // misinterpret the bytes. Fail loudly instead.
        if (recorder.WaveFormat.SampleRate != AudioFormat.SampleRate ||
            recorder.WaveFormat.Channels != AudioFormat.Channels ||
            recorder.WaveFormat.BitsPerSample != AudioFormat.Bits)
        {
            throw new InvalidOperationException(
                $"{channel}: requested {AudioFormat.SampleRate}Hz/{AudioFormat.Channels}ch/{AudioFormat.Bits}bit " +
                $"but got {recorder.WaveFormat}. An explicit resampling stage is required " +
                $"(see PoC decision rule for spec FR-2.4).");
        }

        await foreach (var buffer in recorder.CaptureAsync(ct).ConfigureAwait(false))
        {
            if (buffer.Data.Length == 0) continue;

            var silent = buffer.Flags.HasFlag(NAudio.CoreAudioApi.AudioClientBufferFlags.Silent);
            var offset = timeBase.Offset((long)buffer.QPCPosition);
            yield return new AudioFrame(channel, offset, buffer.Data.ToArray(), silent);
        }
    }
}
