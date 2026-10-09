using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Spike.Commands;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using MeetingAssist.Core.Configuration;

namespace MeetingAssist.Spike;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Transcripts and profiles may contain accented characters (Spanish is a target
        // language); the default console codepage mangles them.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var cli = new Args(args);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(cli.Has("verbose") ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            // Compact JSON so timings can be post-processed rather than eyeballed.
            .WriteTo.File(new CompactJsonFormatter(), "logs/spike-.jsonl",
                rollingInterval: RollingInterval.Day, shared: true)
            .CreateLogger();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var config = AppConfig.FromEnvironment();

        try
        {
            switch (cli.Command)
            {
                case "devices": return DevicesCommand.Run(cli);
                case "record": return await RecordCommand.RunAsync(cli, cts.Token);
                case "synth": return SynthCommand.Run(cli);
                case "transcribe": return await TranscribeCommand.RunAsync(cli, config, cts.Token);
                case "sweep": return await SweepCommand.RunAsync(cli, config, cts.Token);
                case "compare": return await CompareCommand.RunAsync(cli, config, cts.Token);
                case "models": return await ModelsCommand.RunAsync(cli, config, cts.Token);
                case "ask": return await AskCommand.RunAsync(cli, config, cts.Token);
                case "live": return await LiveCommand.RunAsync(cli, config, cts.Token);
                case "mock": return await MockCommand.RunAsync(cli, config, cts.Token);
                case "profile": return ProfileCommand.Run(cli, config);
                default:
                    PrintUsage();
                    return cli.Command is null or "help" or "--help" or "-h" ? 0 : 2;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nCancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Log.Error("{Message}", ex.Message);
            if (cli.Has("verbose")) Log.Error(ex, "Detail");
            return 1;
        }
        finally
        {
            var table = LatencyStats.FormatTable();
            if (!table.StartsWith('(')) Console.WriteLine($"\n=== Latency ===\n{table}");
            await Log.CloseAndFlushAsync();
        }
    }

    private static void PrintUsage() => Console.WriteLine("""
        spike — Meeting Assist engine harness

        Commands:
          devices [--check]                     List devices; --check probes 16kHz mono negotiation
          profile init [--profile PATH]        Write a sample context profile
          record --out NAME [--seconds N]      Record mic + loopback fixtures to fixtures/NAME
          synth --out NAME [--turns N]         Generate a synthetic fixture (no mic, no key)
          transcribe --fixture NAME [options]  Transcribe a fixture and print the transcript
          sweep --fixture NAME --chunks 2,3,4  Compare chunk lengths side by side
          compare --fixture NAME [--stt LIST]  Compare transcription providers side by side
                                               (default groq,groq-v3,xai,elevenlabs)
          models [--filter TEXT]               List Gemini models this key can use
          ask --fixture NAME                   One Ask over a fixture (offline, no mic)
          ask --transcript FILE                One Ask over a canned transcript (no audio)
          live [options]                       Live capture; SPACE = Ask, Q = quit
          mock --transcript FILE [--speak]     The mock counterpart's next line
          mock --chat [--speak] [--voice NAME] Typed rehearsal with the mock counterpart
               [--engine gemini|windows]       (voice engine for --speak; gemini by default)
          mock --voices                        List the Gemini and installed Windows voices

        Common options:
          --profile PATH      Context profile (default: profile.json)
          --min-chunk SEC     Minimum chunk length before a silence cut (default 2)
          --max-chunk SEC     Hard cap on chunk length (default 8)
          --hangover MS       Trailing silence that ends an utterance (default 400)
          --window SEC        Ask window for narrow queries (default 60)
          --no-vocab          Disable Whisper vocabulary priming (for A/B)
          --fake-stt          Offline STT stub: exercise the pipeline with no key
          --stt NAME          Transcription provider: groq (default), groq-v3, xai, elevenlabs
          --stt-model ID      Model id for xai or elevenlabs, overriding their default
          --rpm N             Groq request cap per minute (default 18, under the free plan's 20)
          --thinking LEVEL    minimal (default), low, medium, high; off sends no config
          --realtime          Play fixtures at real speed instead of as fast as possible
          --verbose           Debug logging

        Environment:
          GROQ_API_KEY        Required for transcribe / sweep / live
          XAI_API_KEY         Required for --stt xai
          ELEVENLABS_API_KEY  Required for --stt elevenlabs
          GEMINI_API_KEY      Required for live (Ask)
          GEMINI_MODEL        Override the model id (default gemini-3.6-flash)
        """);
}

/// <summary>Minimal argument parsing. A CLI framework would be ceremony for six commands.</summary>
public sealed class Args
{
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var key = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
                _flags[key] = value;
            }
            else positional.Add(args[i]);
        }
        Command = positional.FirstOrDefault();
        Positional = positional;
    }

    public string? Command { get; }
    public IReadOnlyList<string> Positional { get; }

    public bool Has(string name) => _flags.ContainsKey(name);
    public string? Get(string name) => _flags.GetValueOrDefault(name);

    public string Require(string name) => Get(name)
        ?? throw new ArgumentException($"Missing required option --{name}");

    public double GetDouble(string name, double fallback) =>
        double.TryParse(Get(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), out var v) ? v : fallback;
}
