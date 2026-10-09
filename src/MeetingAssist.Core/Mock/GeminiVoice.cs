using System.Text.RegularExpressions;
using Google.GenAI;
using Google.GenAI.Types;
using NAudio.Wave;
using Serilog;

namespace MeetingAssist.Core.Mock;

/// <summary>
/// A Gemini text-to-speech voice: far easier to follow than the Windows voices, at the cost of
/// about a second before it starts and a network call per line.
///
/// The audio is streamed and played as it arrives rather than after the whole line is made:
/// a two-sentence line starts in 0.65–1.1 s instead of 2–2.5 s on <see cref="DefaultModel"/>.
///
/// A line Gemini cannot speak at all (no key, quota, network) is spoken by the fallback voice
/// instead, so a mock call never stalls on the voice. The free tier allows only a handful of
/// TTS requests a minute, which one line per turn stays well under, but a burst of "next"
/// presses can hit.
/// </summary>
public sealed partial class GeminiVoice : IVoice
{
    /// <summary>
    /// Measured 2026-10-06, a 2-sentence line, first audio / whole stream: 3.8-flash-lite-tts
    /// 0.65–1.1 s / 1.8–2.5 s, 3.8-flash-tts 1.1–1.4 s / 4.2–4.6 s, 3.1-flash-tts-preview 0.9 s /
    /// 3.4 s, 2.5-flash-preview-tts 5–6 s (it does not stream). All four were word-perfect
    /// through Whisper, so the fastest wins.
    /// </summary>
    public const string DefaultModel = "gemini-3.8-flash-lite-tts";

    public const string DefaultVoice = "Charon";

    /// <summary>The prebuilt voices; each name was checked against the API on 2026-10-06.</summary>
    public static readonly IReadOnlyList<string> Voices =
    [
        "Achernar", "Achird", "Algenib", "Algieba", "Alnilam", "Aoede", "Autonoe", "Callirrhoe",
        "Charon", "Despina", "Enceladus", "Erinome", "Fenrir", "Gacrux", "Iapetus", "Kore",
        "Laomedeia", "Leda", "Orus", "Puck", "Pulcherrima", "Rasalgethi", "Sadachbia",
        "Sadaltager", "Schedar", "Sulafat", "Umbriel", "Vindemiatrix", "Zephyr", "Zubenelgenubi"
    ];

    /// <summary>Longest wait for the next piece of audio, first or later, before giving up on the line.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Buffered before playback starts, so a slow second chunk is not heard as a stutter.</summary>
    private static readonly TimeSpan PreRoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Kept playing after the buffer empties: what was handed to the device is still in its
    /// own buffer, and stopping at once clips the last word.
    /// </summary>
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(300);

    private readonly Client _client;
    private readonly string _model;
    private readonly GenerateContentConfig _config;
    private readonly IVoice _fallback;

    /// <param name="voiceName">One of <see cref="Voices"/>, or null for <see cref="DefaultVoice"/>.</param>
    /// <param name="fallback">Speaks a line Gemini could not; owned and disposed by this voice.</param>
    public GeminiVoice(string apiKey, string? voiceName, IVoice fallback, string model = DefaultModel)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Gemini API key is empty. The Gemini voices need it.", nameof(apiKey));

        var voice = string.IsNullOrWhiteSpace(voiceName) ? DefaultVoice : voiceName;
        _client = new Client(apiKey: apiKey);
        _model = model;
        _fallback = fallback;
        Description = $"Gemini {voice}";

        // No language code: the model speaks the language the line is written in.
        _config = new GenerateContentConfig
        {
            ResponseModalities = ["AUDIO"],
            SpeechConfig = new SpeechConfig
            {
                VoiceConfig = new VoiceConfig { PrebuiltVoiceConfig = new PrebuiltVoiceConfig { VoiceName = voice } }
            }
        };
    }

    public string Description { get; }

    /// <summary>
    /// A line went to the fallback voice, with why. Raised so the change of voice can be
    /// explained where the user is looking, rather than only in the log.
    /// </summary>
    public event Action<string>? FellBack;

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var heard = false;
        try
        {
            await StreamAsync(text, () => heard = true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Half a line followed by the whole line in another voice is worse than the half:
            // the turn fails instead, and the next press asks again.
            if (heard) throw;

            Log.Warning("{Voice} could not speak the line ({Error}); using {Fallback}",
                Description, ex.Message, _fallback.Description);
            FellBack?.Invoke(ex.Message);
            await _fallback.SpeakAsync(text, ct).ConfigureAwait(false);
        }
    }

    private async Task StreamAsync(string text, Action started, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        BufferedWaveProvider? buffer = null;
        WasapiPlayer? player = null;
        Exception? playbackError = null;

        void Start()
        {
            player = new WasapiPlayerBuilder().WithSharedMode().Build();
            player.PlaybackStopped += (_, e) => playbackError ??= e.Exception;
            player.Init(buffer!);
            player.Play();
            started();
        }

        try
        {
            await foreach (var response in _client.Models
                               .GenerateContentStreamAsync(_model, text, _config, stall.Token)
                               .WithCancellation(stall.Token).ConfigureAwait(false))
            {
                foreach (var part in response.Candidates?.FirstOrDefault()?.Content?.Parts ?? [])
                {
                    if (part.InlineData is not { Data: { Length: > 0 } data } blob) continue;

                    // A whole line is seconds of audio, so the buffer is sized to never drop any.
                    // ReadFully: an empty buffer plays silence rather than ending playback, so a
                    // late chunk is a pause, not a cut-off line. The end is decided below.
                    buffer ??= new BufferedWaveProvider(ParseFormat(blob.MimeType), TimeSpan.FromMinutes(5))
                    {
                        ReadFully = true
                    };
                    buffer.AddSamples(data, 0, data.Length);
                    stall.CancelAfter(StallTimeout);

                    if (player is null && buffer.BufferedDuration >= PreRoll) Start();
                }
            }

            if (buffer is null) throw new InvalidOperationException("Gemini returned no audio");
            if (player is null) Start();

            while (buffer.BufferedBytes > 0)
            {
                if (playbackError is not null) throw playbackError;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            await Task.Delay(Tail, ct).ConfigureAwait(false);
            if (playbackError is not null) throw playbackError;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no audio from {_model} for {StallTimeout.TotalSeconds:F0} s");
        }
        finally
        {
            player?.Stop();
            player?.Dispose();
        }
    }

    /// <summary>
    /// The audio's format from its MIME type, e.g. <c>audio/l16; rate=24000; channels=1</c>.
    /// L16 is big-endian by its RFC, but Gemini's samples are little-endian — played as such
    /// they transcribe word for word — so they go to the device unconverted.
    /// </summary>
    public static WaveFormat ParseFormat(string? mimeType)
    {
        var mime = mimeType ?? "";
        if (!mime.StartsWith("audio/l16", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unexpected audio format from Gemini: \"{mime}\"");

        var rate = RateParameter().Match(mime) is { Success: true } r ? int.Parse(r.Groups[1].Value) : 24_000;
        var channels = ChannelsParameter().Match(mime) is { Success: true } c ? int.Parse(c.Groups[1].Value) : 1;
        return new WaveFormat(rate, 16, channels);
    }

    [GeneratedRegex(@"rate=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RateParameter();

    [GeneratedRegex(@"channels=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelsParameter();

    public void Dispose()
    {
        _client.Dispose();
        _fallback.Dispose();
    }
}
