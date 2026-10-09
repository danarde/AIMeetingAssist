using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingAssist.Core.Mock;
using MeetingAssist.Core.Transcription;
using Serilog;

namespace MeetingAssist.App.Settings;

/// <summary>
/// The settings that are not the overlay's, the hotkeys' or the profile's: which profile is
/// active, how far back an Ask looks, which devices to use, and whether to run from WAV files
/// instead of live audio.
///
/// A plain JSON file beside the others. Every value has a working default, and a corrupt file
/// falls back to those rather than stopping the app — the alternative is an app that will not
/// start five minutes before a meeting.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Which named profile the next session uses (FR-9.3).</summary>
    public string ProfileName { get; set; } = "default";

    /// <summary>Ask look-back windows in seconds (FR-6.2). Spec §4.8 defaults are 60 and 180.</summary>
    public int NarrowAskSeconds { get; set; } = 60;

    public int WideAskSeconds { get; set; } = 180;

    /// <summary>
    /// Pinned WASAPI endpoint ids, or null to follow the Windows default. Pinning is mutually
    /// exclusive with automatic stream routing (FR-2.1), so it trades recovery for certainty.
    /// </summary>
    public string? MicDeviceId { get; set; }

    public string? LoopbackDeviceId { get; set; }

    /// <summary>Run the pipeline from WAV files rather than live capture (FR-12.1).</summary>
    public bool PlaybackMode { get; set; }

    public string? MicWavPath { get; set; }

    public string? LoopbackWavPath { get; set; }

    /// <summary>Play fixtures at real speed, so timing behaves like a live meeting.</summary>
    public bool PlaybackRealTime { get; set; } = true;

    /// <summary>
    /// The Groq plan the key is on, which sets how fast transcription may go. Free unless the
    /// user says otherwise: pacing a free key at the paid rate draws a refusal per request.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<GroqPlan>))]
    public GroqPlan GroqPlan { get; set; } = GroqPlan.Free;

    /// <summary>
    /// The voice of the other party in a rehearsal (backlog §3). Gemini unless Windows is chosen:
    /// the Windows voices proved hard to follow. Whether a meeting is a rehearsal is chosen each
    /// time on Home, so it is not a setting.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VoiceEngine>))]
    public VoiceEngine MockVoiceEngine { get; set; } = VoiceEngine.Gemini;

    /// <summary>A Gemini voice by name, or null for <see cref="GeminiVoice.DefaultVoice"/>.</summary>
    public string? MockGeminiVoice { get; set; }

    /// <summary>An installed Windows voice by name, or null to pick one by the profile's language.</summary>
    public string? MockVoice { get; set; }

    /// <summary>Get started has opened by itself once; from then on it opens only when chosen.</summary>
    public bool WalkthroughShown { get; set; }

    /// <summary>The voice name for whichever engine is chosen.</summary>
    [JsonIgnore]
    public string? MockVoiceName => MockVoiceEngine == VoiceEngine.Gemini ? MockGeminiVoice : MockVoice;

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingAssist", "settings.json");

    /// <summary>Clamped on read, so a hand-edited file cannot produce a nonsensical window.</summary>
    public TimeSpan NarrowAsk => TimeSpan.FromSeconds(Math.Clamp(NarrowAskSeconds, 10, 3600));

    public TimeSpan WideAsk => TimeSpan.FromSeconds(Math.Clamp(WideAskSeconds, 10, 3600));

    /// <summary>True when playback is both requested and actually usable.</summary>
    public bool PlaybackReady =>
        PlaybackMode
        && !string.IsNullOrWhiteSpace(MicWavPath) && File.Exists(MicWavPath)
        && !string.IsNullOrWhiteSpace(LoopbackWavPath) && File.Exists(LoopbackWavPath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read app settings; using defaults");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save app settings");
        }
    }
}
