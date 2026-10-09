using System.Diagnostics;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Configuration;
using MeetingAssist.Core.Mock;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// The mock counterpart without the app (backlog §3). Three uses:
///
/// * <c>--voices</c> lists what <see cref="GeminiVoice"/> and <see cref="WindowsVoice"/> can pick from.
/// * <c>--transcript FILE</c> prints the line it would say next, so the prompt can be tuned
///   against a fixed conversation.
/// * <c>--chat</c> is a typed rehearsal: it speaks (or prints) a line, you type the answer, and
///   so on, with every turn timed. Answers can be piped in to script a whole conversation.
/// </summary>
public static class MockCommand
{
    public static async Task<int> RunAsync(Args cli, AppConfig config, CancellationToken ct)
    {
        if (cli.Has("voices"))
        {
            Console.WriteLine($"Gemini (--engine gemini, the default; {GeminiVoice.DefaultVoice} unless --voice):");
            Console.WriteLine("  " + string.Join(", ", GeminiVoice.Voices));
            Console.WriteLine("Windows (--engine windows):");
            foreach (var name in WindowsVoice.Installed()) Console.WriteLine("  " + name);
            return 0;
        }

        var profile = config.LoadProfile();
        var store = cli.Get("transcript") is { } path
            ? AskCommand.LoadTranscript(path, profile)
            : new TranscriptStore();

        IVoice voice = cli.Has("speak")
            ? Voices.Create(
                Enum.Parse<VoiceEngine>(cli.Get("engine") ?? nameof(VoiceEngine.Gemini), ignoreCase: true),
                cli.Get("voice"), profile.Language, config.GeminiApiKey)
            : new SilentVoice();

        using var mock = new MockCall(
            profile,
            new GeminiAssistant(config.RequireGeminiKey(), config.GeminiModel),
            voice,
            _ => Task.FromResult(store.All()));

        Console.WriteLine($"Counterpart: \"{profile.LoopbackLabel}\" on {config.GeminiModel}, voice: {voice.Description}");
        Console.WriteLine($"Brief:       {(string.IsNullOrWhiteSpace(profile.MockBrief) ? "(none: plays the meeting notes)" : $"{profile.MockBrief.Length} characters")}");

        var at = store.All().LastOrDefault()?.End ?? TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            var line = await mock.NextLineAsync(store.All(), ct);
            var elapsed = Stopwatch.GetElapsedTime(started);

            Console.WriteLine($"\n{profile.LoopbackLabel}: {line ?? "(nothing)"}");
            Console.WriteLine($"  [{elapsed.TotalMilliseconds:F0} ms]");

            if (line is null) return 1;
            if (cli.Has("speak")) await voice.SpeakAsync(line, ct);
            if (!cli.Has("chat")) return 0;

            at = Add(store, AudioChannelKind.Loopback, line, at);

            Console.Write($"{profile.MicLabel}: ");
            var answer = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(answer)) return 0;
            if (Console.IsInputRedirected) Console.WriteLine(answer);

            at = Add(store, AudioChannelKind.Mic, answer.Trim(), at);
        }

        return 0;
    }

    private static TimeSpan Add(TranscriptStore store, AudioChannelKind channel, string text, TimeSpan at)
    {
        var end = at + TimeSpan.FromSeconds(Math.Max(1.5, text.Split(' ').Length / 2.5));
        store.Append(new TranscriptSegment(Guid.NewGuid(), channel, at, end, text, SegmentCutReason.Silence));
        return end + TimeSpan.FromMilliseconds(400);
    }

    /// <summary>Prints instead of speaking, for prompt tuning without audio.</summary>
    private sealed class SilentVoice : IVoice
    {
        public string Description => "none (text only; --speak to hear it)";
        public Task SpeakAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
}
