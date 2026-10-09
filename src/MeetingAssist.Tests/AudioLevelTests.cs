using System.Runtime.CompilerServices;
using MeetingAssist.App.Overlay;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using static MeetingAssist.Tests.Doubles;

namespace MeetingAssist.Tests;

/// <summary>The overlay's level meter: sound arriving on each channel, so the user can see it working.</summary>
public class AudioLevelTests
{
    [Fact]
    public async Task Each_channel_reports_its_own_sound_and_resets_once_read()
    {
        using var session = new MeetingSession(
            ContextProfile.Sample(), new FakeTranscriber(TimeSpan.FromMilliseconds(10)), null,
            new TranscriptionOptions(), new SegmenterOptions(),
            (channel, _) => channel == AudioChannelKind.Loopback ? new ToneSource(channel) : new EmptySource(channel));

        Assert.Equal((0f, 0f), session.TakeLevels());

        session.Start();
        await Task.Delay(200);

        var (mic, loopback) = session.TakeLevels();
        Assert.Equal(0f, mic);
        Assert.InRange(loopback, 0.3f, 0.5f);

        // Read and reset: a second read sees only what arrived since, and the tone has ended.
        Assert.Equal(0f, session.TakeLevels().Loopback);

        await session.StopAsync();
        Assert.Equal((0f, 0f), session.TakeLevels());
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.0005, 0.0)]  // -66 dBFS: room noise, empty
    [InlineData(0.001, 0.0)]   // -60 dBFS
    [InlineData(0.0316, 0.6667)] // -30 dBFS: ordinary speech, two thirds
    [InlineData(0.1778, 1.0)]  // -15 dBFS: loud speech, full
    [InlineData(1.0, 1.0)]
    public void The_bar_follows_loudness_in_decibels(double rms, double expected) =>
        Assert.Equal(expected, OverlayWindow.Meter(rms), precision: 3);

    /// <summary>Half a second of a steady tone at RMS 0.4, then nothing.</summary>
    private sealed class ToneSource(AudioChannelKind channel) : IAudioSource
    {
        public AudioChannelKind Channel => channel;
        public string Description => $"{channel} (tone)";

        public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var frame = TimeSpan.FromMilliseconds(50);
            var pcm = new byte[AudioFormat.BytesFor(frame)];
            var amplitude = (short)(0.4 * short.MaxValue);
            for (var i = 0; i < pcm.Length; i += 2)
                BitConverter.TryWriteBytes(pcm.AsSpan(i), (i / 2 % 2 == 0) ? amplitude : (short)-amplitude);

            for (var at = TimeSpan.Zero; at < TimeSpan.FromMilliseconds(500); at += frame)
            {
                yield return new AudioFrame(channel, at, pcm, false);
                await Task.Yield();
            }

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
    }
}
