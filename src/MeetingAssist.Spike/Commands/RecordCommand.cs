using System.Diagnostics;
using MeetingAssist.Core.Audio;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// Records both channels to WAV. This is milestone 1 and also the fixture generator: every
/// later offline experiment replays what this produces, so no external recording tool is needed.
/// </summary>
public static class RecordCommand
{
    public static async Task<int> RunAsync(Args cli, CancellationToken ct)
    {
        var name = cli.Get("out") ?? $"call-{DateTime.Now:yyyyMMdd-HHmmss}";
        var dir = Path.IsPathRooted(name) ? name : Path.Combine("fixtures", name);
        var seconds = cli.GetDouble("seconds", 60);

        Directory.CreateDirectory(dir);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        var timeBase = new TimeBase();
        var mic = new WasapiAudioSource(AudioChannelKind.Mic, timeBase);
        var loop = new WasapiAudioSource(AudioChannelKind.Loopback, timeBase);

        Console.WriteLine($"Recording to {Path.GetFullPath(dir)}");
        Console.WriteLine(seconds > 0 ? $"Duration: {seconds:F0}s (Ctrl+C to stop early)\n" : "Ctrl+C to stop\n");

        var micStats = new ChannelStats("mic");
        var loopStats = new ChannelStats("loopback");

        var tasks = new[]
        {
            CaptureToFileAsync(mic, Path.Combine(dir, "mic.wav"), micStats, cts.Token),
            CaptureToFileAsync(loop, Path.Combine(dir, "loopback.wav"), loopStats, cts.Token)
        };

        var meter = MeterLoopAsync(micStats, loopStats, cts.Token);
        await Task.WhenAll(tasks);
        cts.Cancel();
        try { await meter; } catch (OperationCanceledException) { }

        Console.WriteLine("\n\nDone.");
        Console.WriteLine(micStats.Summary());
        Console.WriteLine(loopStats.Summary());

        if (micStats.PeakRms < 0.005)
            Console.WriteLine("\nWARNING: microphone signal is essentially silent. Check the input device.");
        if (loopStats.PeakRms < 0.005)
            Console.WriteLine("\nWARNING: loopback is essentially silent. Was anything playing through the default output?");

        Console.WriteLine($"\nNext: spike transcribe --fixture {Path.GetFileName(dir)}");
        return 0;
    }

    private static async Task CaptureToFileAsync(IAudioSource source, string path, ChannelStats stats, CancellationToken ct)
    {
        using var writer = new WavFileWriter(path);
        try
        {
            await foreach (var frame in source.ReadAsync(ct).ConfigureAwait(false))
            {
                writer.Write(frame.Pcm);
                stats.Observe(frame);
            }
        }
        catch (OperationCanceledException) { /* expected: duration elapsed or Ctrl+C */ }
        catch (Exception ex)
        {
            Console.WriteLine($"\n{source.Channel} capture failed: {ex.Message}");
        }
    }

    private static async Task MeterLoopAsync(ChannelStats mic, ChannelStats loop, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            Console.Write($"\r{sw.Elapsed:mm\\:ss}  mic {Bar(mic.CurrentRms)}  loopback {Bar(loop.CurrentRms)}   ");
        }
    }

    private static string Bar(double rms)
    {
        var level = Math.Clamp((int)(Math.Sqrt(rms) * 30), 0, 12);
        return $"[{new string('#', level).PadRight(12, '.')}]";
    }

    private sealed class ChannelStats(string name)
    {
        private long _bytes;
        private int _silentFrames;
        private int _frames;

        public double CurrentRms { get; private set; }
        public double PeakRms { get; private set; }

        public void Observe(AudioFrame frame)
        {
            _bytes += frame.Pcm.Length;
            _frames++;
            if (frame.WasapiSilent) _silentFrames++;
            var rms = EnergyVad.Rms(frame.Pcm);
            CurrentRms = rms;
            if (rms > PeakRms) PeakRms = rms;
        }

        public string Summary() =>
            $"  {name,-9} {AudioFormat.DurationOf((int)_bytes):mm\\:ss}  peak RMS {PeakRms:F4}  " +
            $"WASAPI-silent frames {_silentFrames}/{_frames}";
    }
}
