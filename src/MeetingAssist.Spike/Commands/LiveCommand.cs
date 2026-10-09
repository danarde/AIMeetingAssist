using System.Text;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;
using MeetingAssist.Core.Configuration;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// Milestone 3: the whole engine live. SPACE is the Ask hotkey — the console has focus, so
/// RegisterHotKey is not needed here; that belongs with the overlay in V1.
///
/// The measurement this produces is the one that decides the design: hotkey to first bullet.
/// </summary>
public static class LiveCommand
{
    public static async Task<int> RunAsync(Args cli, AppConfig config, CancellationToken ct)
    {
        var profile = config.LoadProfile();
        var segOptions = Options.Segmenter(cli);
        var narrow = TimeSpan.FromSeconds(cli.GetDouble("window", 60));
        var wide = TimeSpan.FromSeconds(cli.GetDouble("wide-window", 180));
        var flushTimeout = TimeSpan.FromMilliseconds(cli.GetDouble("flush-timeout", 1500));

        var sttOptions = new TranscriptionOptions
        {
            ModelId = config.SttModel,
            Language = cli.Get("language") ?? profile.Language,
            Vocabulary = cli.Has("no-vocab") ? "" : profile.Vocabulary
        };

        var transcriber = Options.Transcriber(cli, config, sttOptions);

        IAssistant? assistant = null;
        if (!cli.Has("no-ai"))
        {
            assistant = new GeminiAssistant(config.RequireGeminiKey(), config.GeminiModel);
            Console.WriteLine($"Assistant: {config.GeminiModel}");
        }

        var store = new TranscriptStore();
        var timeBase = new TimeBase();
        var pipelines = new List<ChannelPipeline>();

        foreach (var channel in new[] { AudioChannelKind.Mic, AudioChannelKind.Loopback })
        {
            var source = new WasapiAudioSource(channel, timeBase);
            var segmenter = new Segmenter(channel, segOptions.Clone());
            pipelines.Add(new ChannelPipeline(source, segmenter, transcriber, sttOptions, store));
        }

        store.SegmentAdded += s =>
        {
            var label = profile.LabelFor(s.Channel);
            var colour = s.Channel == AudioChannelKind.Mic ? ConsoleColor.Cyan : ConsoleColor.Yellow;
            lock (Console.Out)
            {
                Console.ForegroundColor = colour;
                Console.Write($"{s.Start:mm\\:ss} {label}: ");
                Console.ResetColor();
                Console.WriteLine(s.Text);
            }
        };

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = pipelines.Select(p => p.RunAsync(stop.Token)).ToArray();

        Console.WriteLine($"""

            Live. Chunking min={segOptions.MinChunk.TotalSeconds:F1}s max={segOptions.MaxChunk.TotalSeconds:F1}s.
              SPACE  Ask about the last {narrow.TotalSeconds:F0}s
              W      Ask about the last {wide.TotalSeconds:F0}s
              Q      Quit

            """);

        try
        {
            await KeyLoopAsync(async (wideAsk) =>
            {
                if (assistant is null) { Console.WriteLine("(--no-ai: transcript only)"); return; }
                await AskAsync(assistant, pipelines, store, profile, wideAsk ? wide : narrow, flushTimeout, stop.Token);
            }, stop);
        }
        finally
        {
            await stop.CancelAsync();
            try { await Task.WhenAll(running); } catch (OperationCanceledException) { }
            (assistant as IDisposable)?.Dispose();
            (transcriber as IDisposable)?.Dispose();
        }

        Console.WriteLine("\n=== Session ===");
        foreach (var p in pipelines)
            Console.WriteLine($"  {p.Channel,-9} cut={p.SegmentsSeen} transcribed={p.SegmentsTranscribed} " +
                              $"empty={p.SegmentsEmpty} audio={p.AudioSecondsSent:F0}s");

        var savePath = cli.Get("save");
        if (savePath is not null)
        {
            File.WriteAllText(savePath, store.RenderAll(profile));
            Console.WriteLine($"  transcript -> {Path.GetFullPath(savePath)}");
        }

        return 0;
    }

    private static async Task AskAsync(
        IAssistant assistant,
        List<ChannelPipeline> pipelines,
        TranscriptStore store,
        Core.Profiles.ContextProfile profile,
        TimeSpan window,
        TimeSpan flushTimeout,
        CancellationToken ct)
    {
        var timer = new StageTimer("ask");

        // Close the open chunk on both channels so the sentence that just ended is included.
        await Task.WhenAll(pipelines.Select(p => p.FlushAndDrainAsync(flushTimeout, ct)));
        timer.Mark("flushDrained");

        var history = store.All();
        var focus = store.GetWindow(window);
        // The profile's own answer style, as the app sends it; blank means the built-in default.
        var style = string.IsNullOrWhiteSpace(profile.AnswerStyle) ? null : profile.AnswerStyle;
        var request = new AssistRequest(profile, history, focus, style);
        timer.Mark("promptBuilt");

        lock (Console.Out)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n--- Ask (last {window.TotalSeconds:F0}s, {focus.Count} segments) ---");
            Console.ResetColor();
        }

        var first = true;
        var answer = new StringBuilder();

        await foreach (var chunk in assistant.AskAsync(request, ct).ConfigureAwait(false))
        {
            if (first) { timer.Mark("firstToken"); first = false; }
            answer.Append(chunk);
            Console.Write(chunk);
        }

        timer.Mark("lastToken");
        Console.WriteLine();

        var usage = assistant.LastUsage;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(timer.Format() + (usage is null ? "" : $" tokens in={usage.InputTokens} out={usage.OutputTokens}"));
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>Reads keys without blocking the pipeline. SPACE / W trigger an Ask, Q stops.</summary>
    private static async Task KeyLoopAsync(Func<bool, Task> onAsk, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(40, stop.Token).ConfigureAwait(false);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Spacebar: await onAsk(false); break;
                case ConsoleKey.W: await onAsk(true); break;
                case ConsoleKey.Q: await stop.CancelAsync(); return;
            }
        }
    }
}
