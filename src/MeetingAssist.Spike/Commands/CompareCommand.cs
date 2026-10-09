using System.Text;
using MeetingAssist.Core.Configuration;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// The same fixture through several transcription providers, side by side (backlog §8). One
/// run per provider through the real pipeline, so the chunking each one gets is identical and
/// only the provider differs. A provider whose key is not set is skipped, not fatal, so the
/// comparison works with whichever keys are at hand.
/// </summary>
public static class CompareCommand
{
    private const string AllProviders = "groq,groq-v3,xai,elevenlabs";

    public static async Task<int> RunAsync(Args cli, AppConfig config, CancellationToken ct)
    {
        var dir = Options.FixtureDir(cli, config);
        var profile = config.LoadProfile();
        var segOptions = Options.Segmenter(cli);
        var names = (cli.Get("stt") ?? AllProviders)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var truthPath = Path.Combine(dir, "truth.txt");
        var truth = File.Exists(truthPath) ? File.ReadAllText(truthPath) : null;
        var terms = Keyterms.From(profile.Vocabulary, int.MaxValue);

        Console.WriteLine($"Fixture:   {Path.GetFullPath(dir)}");
        Console.WriteLine($"Truth:     {(truth is null ? "absent — transcripts only, no scores" : $"{Wer.Normalize(truth).Length} words")}");
        Console.WriteLine($"Key terms: {terms.Count} from the profile's vocabulary{(cli.Has("no-vocab") ? ", not sent (--no-vocab)" : "")}");
        Console.WriteLine();

        var rows = new List<string>();
        var transcripts = new StringBuilder();

        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var choice = Options.Stt(name, cli, config);
            if (choice.Problem is not null)
            {
                Console.WriteLine($"  {name,-12} skipped: {choice.Problem}");
                rows.Add($"{name,-12}  {"skipped",8}");
                continue;
            }

            var sttOptions = new TranscriptionOptions
            {
                ModelId = choice.Model,
                Language = cli.Get("language") ?? profile.Language,
                Vocabulary = cli.Has("no-vocab") ? "" : profile.Vocabulary
            };

            // A null result means silence or failure alike; the health signal tells them apart.
            var lost = 0;
            choice.Transcriber!.AttemptCompleted += ok => { if (!ok) Interlocked.Increment(ref lost); };

            Console.Write($"  {name,-12} {choice.Model} ... ");
            var run = await FixtureRunner.RunAsync(dir, profile, segOptions, sttOptions, choice.Transcriber!, realTime: false, ct);
            (choice.Transcriber as IDisposable)?.Dispose();

            var text = run.Store.RenderAll(profile);
            transcripts.AppendLine($"===== {name} ({choice.Model}) =====").AppendLine(text).AppendLine();

            var row = $"{name,-12}  {run.SegmentsTranscribed,3}/{run.SegmentsSeen,-3}  {lost,4}";
            if (truth is not null)
            {
                var wer = Wer.Compute(truth, text);
                var hits = Wer.Terms(truth, text, terms);
                row += $"  {wer.Rate,6:P1}  {hits.Found,2}/{hits.Expected.Count,-2}"
                       + (hits.Missed.Count > 0 ? $"  missed: {string.Join(", ", hits.Missed)}" : "");
                Console.WriteLine($"WER {wer.Rate:P1}, key terms {hits.Found}/{hits.Expected.Count}");
            }
            else
            {
                Console.WriteLine($"{run.SegmentsTranscribed} segments");
            }
            rows.Add(row);
        }

        Console.WriteLine();
        Console.WriteLine($"{"provider",-12}  {"segments",-7}  {"lost",4}" + (truth is not null ? $"  {"WER",6}  {"terms",-5}" : ""));
        Console.WriteLine(new string('-', 60));
        rows.ForEach(Console.WriteLine);
        Console.WriteLine();
        Console.WriteLine("lost = segments the provider refused or never answered (see the log for why).");
        if (truth is not null)
        {
            Console.WriteLine("WER counts every word alike. The reference spells numbers out (\"twenty nine\") while every");
            Console.WriteLine("provider writes digits (\"$29\"), so all of them carry the same few points for that.");
            Console.WriteLine("terms = vocabulary terms in the reference that the provider got exactly right.");
        }

        var outPath = cli.Get("save") ?? Path.Combine(dir, "compare.txt");
        File.WriteAllText(outPath, transcripts.ToString());
        Console.WriteLine($"\nEach provider's transcript: {Path.GetFullPath(outPath)}");
        return 0;
    }
}
