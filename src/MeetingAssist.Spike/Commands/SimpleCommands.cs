using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using NAudio.CoreAudioApi;
using MeetingAssist.Core.Configuration;

namespace MeetingAssist.Spike.Commands;

public static class DevicesCommand
{
    /// <summary>
    /// Enumeration itself lives in <see cref="AudioDevices"/> so the settings window's pickers
    /// and this command can never disagree about what is connected. This adds only what a
    /// console is good for: the endpoint id, which is what gets persisted, and the mix format.
    /// </summary>
    public static int Run(Args? cli = null)
    {
        foreach (var (devices, title) in new[]
                 {
                     (AudioDevices.Capture(), "CAPTURE (microphones)"),
                     (AudioDevices.Render(), "RENDER (loopback sources)")
                 })
        {
            Console.WriteLine($"\n{title}");

            foreach (var device in devices)
            {
                Console.WriteLine($"{(device.IsDefault ? "* " : "  ")}{device.Name}");
                Console.WriteLine($"    id: {device.Id}");

                using var resolved = AudioDevices.TryResolve(device.Id);
                if (resolved is null) continue;

                using var client = resolved.CreateAudioClient();
                Console.WriteLine($"    mix format: {client.MixFormat}");
            }
        }

        Console.WriteLine("\n* = default. Loopback captures the default render device.");

        if (cli?.Has("check") == true) ProbeFormats();
        return 0;
    }

    /// <summary>
    /// Verifies that WASAPI will actually hand us 16 kHz mono despite devices running at
    /// 48 kHz stereo float. If this fails, an explicit resampling stage is required and spec
    /// FR-2.4 changes. Opens each device only long enough to read the negotiated format —
    /// no audio is read, kept, or written anywhere.
    /// </summary>
    private static void ProbeFormats()
    {
        Console.WriteLine("\n=== Format negotiation probe (no audio is captured or stored) ===");
        var target = new NAudio.Wave.WaveFormat(AudioFormat.SampleRate, AudioFormat.Bits, AudioFormat.Channels);

        foreach (var (label, loopback) in new[] { ("microphone", false), ("loopback", true) })
        {
            try
            {
                var builder = new NAudio.Wave.WasapiRecorderBuilder().WithFormat(target);
                if (loopback) builder = builder.WithLoopbackCapture();

                // Build only. Do NOT StartRecording here: with nothing consuming buffers the
                // capture thread blocks and StopRecording never returns.
                using var recorder = builder.Build();
                var format = recorder.WaveFormat;
                var latency = recorder.LatencyMilliseconds;

                var ok = format.SampleRate == AudioFormat.SampleRate
                         && format.Channels == AudioFormat.Channels
                         && format.BitsPerSample == AudioFormat.Bits;

                Console.WriteLine($"  {label,-11} {(ok ? "OK  " : "FAIL")} negotiated {format}, latency {latency}ms");
                if (!ok) Console.WriteLine($"              -> resampling stage required (spec FR-2.4)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-11} FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

public static class ProfileCommand
{
    public static int Run(Args cli, AppConfig config)
    {
        var path = cli.Get("profile") ?? config.ProfilePath;

        if (cli.Positional.Count > 1 && cli.Positional[1] == "init")
        {
            if (File.Exists(path) && !cli.Has("force"))
            {
                Console.WriteLine($"{path} already exists. Use --force to overwrite.");
                return 1;
            }
            ContextProfile.Sample().Save(path);
            Console.WriteLine($"Wrote sample profile to {Path.GetFullPath(path)}");
            return 0;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine($"No profile at {path}. Run: spike profile init");
            return 1;
        }

        var profile = ContextProfile.Load(path);
        Console.WriteLine($"Profile:    {profile.Name}");
        Console.WriteLine($"Language:   {profile.Language}");
        Console.WriteLine($"Labels:     mic=\"{profile.MicLabel}\" loopback=\"{profile.LoopbackLabel}\"");
        Console.WriteLine($"Vocabulary: {profile.Vocabulary.Length} chars (~{profile.Vocabulary.Length / 4} tokens, cap ~224)");
        if (profile.Vocabulary.Length / 4 > 224)
            Console.WriteLine("  WARNING: likely over the Whisper prompt cap; the tail will be ignored.");
        return 0;
    }
}

/// <summary>Shared option parsing so every command tunes the same knobs the same way.</summary>
public static class Options
{
    public static SegmenterOptions Segmenter(Args cli) => new()
    {
        MinChunk = TimeSpan.FromSeconds(cli.GetDouble("min-chunk", 2.0)),
        MaxChunk = TimeSpan.FromSeconds(cli.GetDouble("max-chunk", 8.0)),
        Hangover = TimeSpan.FromMilliseconds(cli.GetDouble("hangover", 400)),
        MinFlushSpeech = TimeSpan.FromMilliseconds(cli.GetDouble("min-flush", 300)),
        Vad = new VadOptions
        {
            AbsoluteFloor = cli.GetDouble("vad-floor", 0.004),
            NoiseFactor = cli.GetDouble("vad-factor", 3.0),
            Adaptive = !cli.Has("vad-fixed")
        }
    };

    public static string FixtureDir(Args cli, AppConfig config)
    {
        var name = cli.Require("fixture");
        var dir = Path.IsPathRooted(name) ? name : Path.Combine(config.FixturesRoot, name);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"No fixture at {Path.GetFullPath(dir)}. Record one with: spike record --out {name}");
        return dir;
    }

    public static (string Mic, string Loopback) FixtureFiles(string dir)
    {
        var mic = Path.Combine(dir, "mic.wav");
        var loop = Path.Combine(dir, "loopback.wav");
        if (!File.Exists(mic) && !File.Exists(loop))
            throw new FileNotFoundException($"Neither mic.wav nor loopback.wav found in {dir}");
        return (mic, loop);
    }

    /// <summary>
    /// Real provider, or the offline stub. `--fake-stt` exercises the whole pipeline with no
    /// key and no network, which separates "is the plumbing right" from "is Whisper good enough".
    /// `--stt` picks a provider other than Groq; the model then comes from that choice, since a
    /// Whisper model id means nothing to the others.
    /// </summary>
    public static Core.Transcription.ITranscriber Transcriber(Args cli, AppConfig config, Core.Transcription.TranscriptionOptions options)
    {
        if (cli.Has("fake-stt"))
        {
            Console.WriteLine("STT:       FAKE (offline stub — no transcription is really happening)\n");
            return new Core.Transcription.FakeTranscriber();
        }

        var choice = Stt(cli.Get("stt") ?? "groq", cli, config);
        if (choice.Problem is not null) throw new InvalidOperationException(choice.Problem);
        options.ModelId = choice.Model;
        return choice.Transcriber!;
    }

    public sealed record SttChoice(Core.Transcription.ITranscriber? Transcriber, string Model, string? Problem);

    /// <summary>
    /// A provider by the name `--stt` and `compare --stt` use. A missing key is returned as a
    /// problem rather than thrown, so `compare` can skip that provider and run the rest.
    /// </summary>
    public static SttChoice Stt(string name, Args cli, AppConfig config)
    {
        static SttChoice Missing(string model, string variable) =>
            new(null, model, $"{variable} is not set in this terminal");

        switch (name.ToLowerInvariant())
        {
            case "groq":
            case "groq-v3":
                var whisper = name.Equals("groq-v3", StringComparison.OrdinalIgnoreCase) ? "whisper-large-v3" : config.SttModel;
                if (config.GroqApiKey is null)
                    return new(null, whisper, "no Groq key: enter it in the app's Keys page, or set GROQ_API_KEY");
                // Groq caps Whisper at 20 RPM on the free plan, so the pacer matters in real use
                // and not just in the sweep. --rpm is for a raised quota.
                return new(new Core.Transcription.GroqTranscriber(config.GroqApiKey,
                    requestsPerMinute: cli.GetInt("rpm", Core.Transcription.GroqTranscriber.DefaultRequestsPerMinute)), whisper, null);

            case "xai":
                var grok = cli.Get("stt-model") ?? Core.Transcription.XaiTranscriber.DefaultModel;
                return config.XaiApiKey is null
                    ? Missing(grok, "XAI_API_KEY")
                    : new(new Core.Transcription.XaiTranscriber(config.XaiApiKey), grok, null);

            case "elevenlabs":
                var scribe = cli.Get("stt-model") ?? Core.Transcription.ElevenLabsTranscriber.DefaultModel;
                return config.ElevenLabsApiKey is null
                    ? Missing(scribe, "ELEVENLABS_API_KEY")
                    : new(new Core.Transcription.ElevenLabsTranscriber(config.ElevenLabsApiKey), scribe, null);

            default:
                return new(null, "", $"unknown provider \"{name}\" (groq, groq-v3, xai, elevenlabs)");
        }
    }
}

/// <summary>
/// Generates a synthetic two-channel fixture: alternating tone and silence standing in for a
/// conversation. No microphone, no meeting, no API key — enough to smoke-test segmentation
/// and the pipeline. It cannot say anything about transcription quality; only a real
/// recording can do that.
/// </summary>
public static class SynthCommand
{
    public static int Run(Args cli)
    {
        var name = cli.Get("out") ?? "synthetic";
        var dir = Path.IsPathRooted(name) ? name : Path.Combine("fixtures", name);
        var turns = cli.GetInt("turns", 6);
        Directory.CreateDirectory(dir);

        // Alternating turns: while one side "speaks", the other is silent.
        var micPcm = new List<byte>();
        var loopPcm = new List<byte>();

        for (var i = 0; i < turns; i++)
        {
            var speech = TimeSpan.FromSeconds(2.5 + (i % 3));
            var gap = TimeSpan.FromMilliseconds(700);
            var micTurn = i % 2 == 0;

            micPcm.AddRange(micTurn ? Tone(speech, 200 + i * 20) : new byte[AudioFormat.BytesFor(speech)]);
            loopPcm.AddRange(micTurn ? new byte[AudioFormat.BytesFor(speech)] : Tone(speech, 300 + i * 20));

            var silence = new byte[AudioFormat.BytesFor(gap)];
            micPcm.AddRange(silence);
            loopPcm.AddRange(silence);
        }

        File.WriteAllBytes(Path.Combine(dir, "mic.wav"), WavIo.WrapPcm(micPcm.ToArray()));
        File.WriteAllBytes(Path.Combine(dir, "loopback.wav"), WavIo.WrapPcm(loopPcm.ToArray()));

        Console.WriteLine($"Wrote synthetic fixture to {Path.GetFullPath(dir)}");
        Console.WriteLine($"  {turns} alternating turns, {AudioFormat.DurationOf(micPcm.Count):mm\\:ss} per channel");
        Console.WriteLine($"\nSmoke-test the pipeline offline:");
        Console.WriteLine($"  spike transcribe --fixture {Path.GetFileName(dir)} --fake-stt");
        return 0;
    }

    private static byte[] Tone(TimeSpan duration, double frequency)
    {
        var pcm = new byte[AudioFormat.BytesFor(duration)];
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / AudioFormat.SampleRate;
            // Amplitude envelope, so onsets and endings look like speech to the VAD.
            var env = Math.Min(1.0, Math.Min(i, samples.Length - i) / (double)AudioFormat.SampleRate * 8);
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * t) * 0.28 * env * short.MaxValue);
        }
        return pcm;
    }
}
