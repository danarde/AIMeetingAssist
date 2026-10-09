using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using MeetingAssist.Core.Configuration;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// Milestone 2: transcribe a fixture offline. This is where the central question gets its
/// answer — is Whisper output at short chunk lengths good enough to feed a model?
/// </summary>
public static class TranscribeCommand
{
    public static async Task<int> RunAsync(Args cli, AppConfig config, CancellationToken ct)
    {
        var dir = Options.FixtureDir(cli, config);
        var profile = config.LoadProfile();
        var segOptions = Options.Segmenter(cli);

        var sttOptions = new TranscriptionOptions
        {
            ModelId = config.SttModel,
            Language = cli.Get("language") ?? profile.Language,
            Vocabulary = cli.Has("no-vocab") ? "" : profile.Vocabulary
        };

        var transcriber = Options.Transcriber(cli, config, sttOptions);

        Console.WriteLine($"Fixture:   {Path.GetFullPath(dir)}");
        Console.WriteLine($"Chunking:  min={segOptions.MinChunk.TotalSeconds:F1}s max={segOptions.MaxChunk.TotalSeconds:F1}s " +
                          $"hangover={segOptions.Hangover.TotalMilliseconds:F0}ms");
        Console.WriteLine($"STT:       {transcriber.Name}/{sttOptions.ModelId} lang={sttOptions.Language} " +
                          $"vocab={(sttOptions.Vocabulary.Length > 0 ? $"{sttOptions.Vocabulary.Length} chars" : "OFF")}");
        Console.WriteLine();

        var run = await FixtureRunner.RunAsync(
            dir, profile, segOptions, sttOptions, transcriber, cli.Has("realtime"), ct);

        Console.WriteLine("=== Transcript ===");
        Console.WriteLine(run.Store.RenderAll(profile));
        Console.WriteLine();

        Console.WriteLine("=== Run ===");
        Console.WriteLine($"  segments cut:         {run.SegmentsSeen}");
        Console.WriteLine($"  transcribed:          {run.SegmentsTranscribed}");
        Console.WriteLine($"  empty (no text back): {run.SegmentsEmpty}");
        Console.WriteLine($"  audio sent:           {run.AudioSecondsSent:F1}s");
        Console.WriteLine($"  wall clock:           {run.WallClock.TotalSeconds:F1}s");

        var outPath = cli.Get("save");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, run.Store.RenderAll(profile));
            Console.WriteLine($"\nTranscript written to {Path.GetFullPath(outPath)}");
        }

        var truth = Path.Combine(dir, "truth.txt");
        if (File.Exists(truth))
        {
            var wer = Wer.Compute(File.ReadAllText(truth), run.Store.RenderAll(profile));
            Console.WriteLine($"\n  WER vs truth.txt:     {wer.Rate:P1} ({wer.Errors}/{wer.ReferenceWords} words)");
        }
        else
        {
            Console.WriteLine($"\n  (no truth.txt in the fixture — add one to get a WER number)");
        }

        (transcriber as IDisposable)?.Dispose();
        return 0;
    }
}
