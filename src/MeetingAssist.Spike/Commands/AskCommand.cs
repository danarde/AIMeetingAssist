using System.Text;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using MeetingAssist.Core.Configuration;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// One Ask, offline. Either replays a fixture through the real pipeline first, or loads a
/// canned transcript and skips audio entirely.
///
/// The canned-transcript path is how the assistant gets tested without a microphone: it
/// isolates prompt assembly and the LLM call from transcription quality, so a bad answer can
/// be attributed to one or the other.
/// </summary>
public static class AskCommand
{
    public static async Task<int> RunAsync(Args cli, AppConfig config, CancellationToken ct)
    {
        var profile = config.LoadProfile();
        var window = TimeSpan.FromSeconds(cli.GetDouble("window", 60));

        TranscriptStore store;

        var transcriptPath = cli.Get("transcript");
        if (transcriptPath is not null)
        {
            store = LoadTranscript(transcriptPath, profile);
            Console.WriteLine($"Transcript: {Path.GetFullPath(transcriptPath)} ({store.Count} turns)");
        }
        else
        {
            var dir = Options.FixtureDir(cli, config);
            var sttOptions = new TranscriptionOptions
            {
                ModelId = config.SttModel,
                Language = cli.Get("language") ?? profile.Language,
                Vocabulary = cli.Has("no-vocab") ? "" : profile.Vocabulary
            };
            var transcriber = Options.Transcriber(cli, config, sttOptions);

            Console.WriteLine($"Fixture:    {Path.GetFullPath(dir)}");
            var run = await FixtureRunner.RunAsync(
                dir, profile, Options.Segmenter(cli), sttOptions, transcriber, realTime: false, ct);
            (transcriber as IDisposable)?.Dispose();
            store = run.Store;

            Console.WriteLine("\n=== Transcript ===");
            Console.WriteLine(store.RenderAll(profile));
        }

        if (store.Count == 0)
        {
            Console.WriteLine("\nNothing transcribed — no point asking. Check the fixture has audible speech.");
            return 1;
        }

        using var assistant = new GeminiAssistant(config.RequireGeminiKey(), config.GeminiModel, Thinking(cli));
        Console.WriteLine($"\nAssistant:  {config.GeminiModel}");

        var history = store.All();
        var focus = store.GetWindow(window);

        if (cli.Has("show-prompt"))
        {
            Console.WriteLine("\n=== System block (stable prefix) ===");
            Console.WriteLine(PromptBuilder.BuildSystem(profile, string.IsNullOrWhiteSpace(profile.AnswerStyle) ? null : profile.AnswerStyle));
            Console.WriteLine("\n=== User block ===");
            Console.WriteLine(PromptBuilder.BuildUser(history, focus, profile));
        }

        Console.WriteLine($"\n=== Cue notes (last {window.TotalSeconds:F0}s, {focus.Count} segments) ===");

        var timer = new StageTimer("ask");
        var answer = new StringBuilder();
        var first = true;

        // The profile's own answer style, as the app sends it (MeetingSession); blank means the
        // built-in default. Without this a profile with a custom style was measured against the
        // wrong prompt.
        var style = string.IsNullOrWhiteSpace(profile.AnswerStyle) ? null : profile.AnswerStyle;

        await foreach (var chunk in assistant.AskAsync(new AssistRequest(profile, history, focus, style), ct))
        {
            if (first) { timer.Mark("firstToken"); first = false; }
            answer.Append(chunk);
            Console.Write(chunk);
        }
        timer.Mark("lastToken");

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine(timer.Format());

        var usage = assistant.LastUsage;
        if (usage is not null)
            Console.WriteLine($"tokens in={usage.InputTokens} out={usage.OutputTokens} total={usage.TotalTokens}");

        return 0;
    }

    /// <summary>
    /// <c>--thinking minimal|low|medium|high</c> picks the level; <c>off</c> sends no thinking
    /// config, leaving the model's default. Absent means the app's own default.
    /// </summary>
    private static Google.GenAI.Types.ThinkingLevel? Thinking(Args cli)
    {
        var value = cli.Get("thinking");
        if (value is null) return Google.GenAI.Types.ThinkingLevel.Minimal;
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;

        return Google.GenAI.Types.ThinkingLevel.AllValues
            .FirstOrDefault(l => l.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            is { Value: not null } level && !level.Equals(Google.GenAI.Types.ThinkingLevel.ThinkingLevelUnspecified)
            ? level
            : throw new ArgumentException($"--thinking expects minimal, low, medium, high or off, not '{value}'.");
    }

    /// <summary>
    /// Parses a "Label: text" transcript. Timestamps are synthesised at a steady cadence,
    /// which is enough for windowing and ordering.
    /// </summary>
    internal static TranscriptStore LoadTranscript(string path, ContextProfile profile)
    {
        var store = new TranscriptStore();
        var at = TimeSpan.Zero;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            var (label, text) = colon > 0 && colon < 24
                ? (line[..colon].Trim(), line[(colon + 1)..].Trim())
                : (profile.LoopbackLabel, line);

            if (text.Length == 0) continue;

            var channel = label.Equals(profile.MicLabel, StringComparison.OrdinalIgnoreCase)
                ? AudioChannelKind.Mic
                : AudioChannelKind.Loopback;

            // Roughly 150 words per minute, so windowing behaves like a real conversation.
            var duration = TimeSpan.FromSeconds(Math.Max(1.5, text.Split(' ').Length / 2.5));
            store.Append(new TranscriptSegment(
                Guid.NewGuid(), channel, at, at + duration, text, SegmentCutReason.Silence));
            at += duration + TimeSpan.FromMilliseconds(400);
        }

        return store;
    }
}
