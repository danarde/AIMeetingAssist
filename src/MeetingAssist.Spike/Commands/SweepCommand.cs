using System.Diagnostics;
using System.Globalization;
using System.Text;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Transcription;
using MeetingAssist.Core.Configuration;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// Runs one fixture at several chunk lengths and prints them side by side.
///
/// Chunk length is the central unknown of the design and manual fiddling will not settle it.
/// With a truth.txt in the fixture this produces a WER per configuration; without one it still
/// gives segment counts, empty-response counts and latency, which is usually enough to see
/// where quality falls apart.
/// </summary>
public static class SweepCommand
{
    public static async Task<int> RunAsync(Args cli, AppConfig config, CancellationToken ct)
    {
        var dir = Options.FixtureDir(cli, config);
        var profile = config.LoadProfile();

        var chunks = (cli.Get("chunks") ?? "2,3,4,6")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

        var truthPath = Path.Combine(dir, "truth.txt");
        var truth = File.Exists(truthPath) ? File.ReadAllText(truthPath) : null;

        var baseSeg = Options.Segmenter(cli);
        var sttOptions = new TranscriptionOptions
        {
            ModelId = config.SttModel,
            Language = cli.Get("language") ?? profile.Language,
            Vocabulary = cli.Has("no-vocab") ? "" : profile.Vocabulary
        };
        var transcriber = Options.Transcriber(cli, config, sttOptions);

        Console.WriteLine($"Fixture:  {Path.GetFullPath(dir)}");
        Console.WriteLine($"Chunks:   {string.Join(", ", chunks.Select(c => $"{c:F1}s"))}");
        Console.WriteLine($"Truth:    {(truth is null ? "absent — no WER will be computed" : $"{Wer.Normalize(truth).Length} words")}");

        // A sweep multiplies requests by the number of configurations, so it hits the provider
        // cap long before a live session would. Saying so up front stops a paced run from
        // looking like a hang — and an unannounced 4-minute wait is what a rate limit feels
        // like from the outside.
        if (transcriber is Core.Transcription.HttpTranscriber groq)
        {
            var requests = EstimateRequests(dir, chunks);
            var eta = groq.Limiter.EstimateFor(requests);
            Console.WriteLine($"Pacing:   {groq.Limiter.Limit} requests/min (provider cap)");
            Console.WriteLine(eta > TimeSpan.Zero
                ? $"Estimate: ~{requests} requests, so expect around {eta.TotalMinutes:F0}–{eta.TotalMinutes + 1:F0} min. Waiting is deliberate."
                : $"Estimate: ~{requests} requests, within one minute's budget.");
        }
        Console.WriteLine();

        var results = new List<SweepResult>();
        var transcripts = new StringBuilder();

        foreach (var minChunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            LatencyStats.Reset();

            var seg = baseSeg.Clone();
            seg.MinChunk = TimeSpan.FromSeconds(minChunk);

            Console.Write($"  min-chunk {minChunk:F1}s ... ");
            var clock = Stopwatch.StartNew();
            var run = await FixtureRunner.RunAsync(dir, profile, seg, sttOptions, transcriber, realTime: false, ct);
            clock.Stop();

            var text = run.Store.RenderAll(profile);
            var wer = truth is not null ? Wer.Compute(truth, text).Rate : (double?)null;
            var sttStats = LatencyStats.Snapshot().FirstOrDefault(s => s.Name.EndsWith("sttResponse"));

            results.Add(new SweepResult(minChunk, run.SegmentsSeen, run.SegmentsTranscribed,
                run.SegmentsEmpty, run.AudioSecondsSent, wer, sttStats.P50, sttStats.P95));

            transcripts.AppendLine($"===== min-chunk {minChunk:F1}s =====");
            transcripts.AppendLine(text);
            transcripts.AppendLine();

            var verdict = wer is not null ? $"WER {wer:P1}" : $"{run.SegmentsTranscribed} segments";
            Console.WriteLine($"{verdict}  ({clock.Elapsed.TotalSeconds:F0}s"
                + (run.SegmentsEmpty > 0 ? $", {run.SegmentsEmpty} dropped" : "") + ")");
        }

        Console.WriteLine();
        Console.WriteLine(FormatTable(results, truth is not null));

        if (transcriber is Core.Transcription.HttpTranscriber paced && paced.Limiter.PacedRequests > 0)
        {
            Console.WriteLine($"Pacing held back {paced.Limiter.PacedRequests} requests for a total of "
                + $"{paced.Limiter.TotalWaited.TotalSeconds:F0}s to stay under {paced.Limiter.Limit}/min.");
            Console.WriteLine("That wait is the provider cap, not transcription latency; the stt columns");
            Console.WriteLine("exclude it and report it separately as rateLimitWait.");
            Console.WriteLine();
        }

        var outPath = cli.Get("save") ?? Path.Combine(dir, "sweep.txt");
        File.WriteAllText(outPath, transcripts.ToString());
        Console.WriteLine($"Transcripts for each configuration: {Path.GetFullPath(outPath)}");

        if (truth is null)
            Console.WriteLine("\nTip: hand-type a reference into truth.txt in the fixture folder to get WER numbers.");

        (transcriber as IDisposable)?.Dispose();
        return 0;
    }

    /// <summary>
    /// Rough request count for the whole sweep, from fixture duration over chunk length. Only
    /// needs to be good enough to set expectations before a long paced run.
    /// </summary>
    private static int EstimateRequests(string dir, double[] chunks)
    {
        var seconds = 0d;
        foreach (var path in Directory.GetFiles(dir, "*.wav"))
        {
            var pcmBytes = Math.Max(0, new FileInfo(path).Length - 44);
            seconds += AudioFormat.DurationOf((int)pcmBytes).TotalSeconds;
        }

        // Silence is trimmed rather than sent, so the raw duration overstates it; chunks also
        // run longer than the minimum. Both pull the same way, hence the discount.
        return (int)Math.Ceiling(chunks.Sum(c => seconds * 0.75 / Math.Max(0.5, c)));
    }

    private sealed record SweepResult(
        double MinChunkSec, int Cut, int Transcribed, int Empty,
        double AudioSeconds, double? Wer, double SttP50, double SttP95);

    private static string FormatTable(List<SweepResult> rows, bool withWer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"min-chunk",10}  {"cut",5}  {"ok",5}  {"empty",6}  {"audio s",8}  {"stt p50",8}  {"stt p95",8}{(withWer ? "  " + "WER".PadLeft(7) : "")}");
        sb.AppendLine(new string('-', withWer ? 74 : 65));
        foreach (var r in rows)
        {
            sb.Append($"{r.MinChunkSec,9:F1}s  {r.Cut,5}  {r.Transcribed,5}  {r.Empty,6}  {r.AudioSeconds,8:F1}  {r.SttP50,8:F0}  {r.SttP95,8:F0}");
            if (withWer) sb.Append($"  {r.Wer,6:P1}");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("cut = segments the segmenter produced; empty = segments STT returned nothing for.");
        sb.AppendLine("A rising 'empty' count as chunks shrink is the signal that chunks are too short.");
        return sb.ToString();
    }
}
