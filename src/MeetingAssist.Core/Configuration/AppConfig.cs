using System.Text.Json;
using MeetingAssist.Core.Profiles;

namespace MeetingAssist.Core.Configuration;

/// <summary>
/// Shared configuration for both executables. Keys come from the DPAPI-encrypted store
/// (spec FR-10.1) or from environment variables. Either way they live outside the repository
/// and never appear on a command line, since command lines are visible to any process listing.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Used when neither the environment nor the store names a model. 3.6 Flash replaced
    /// 3.5 Flash-Lite after a side-by-side on the same transcript: ~0.5 s slower to first
    /// token, but the Flash-Lite models invented figures the profile never gave them.
    /// </summary>
    public const string DefaultGeminiModel = "gemini-3.6-flash";

    public string? GroqApiKey { get; init; }
    public string? GeminiApiKey { get; init; }

    /// <summary>
    /// Keys for the providers on trial against Groq (backlog §8). Environment only for now: they
    /// are used by the spike's <c>compare</c>, and get a place in the Keys page if one is adopted.
    /// </summary>
    public string? XaiApiKey { get; init; }
    public string? ElevenLabsApiKey { get; init; }
    public string GeminiModel { get; init; } = DefaultGeminiModel;
    public string SttModel { get; init; } = "whisper-large-v3-turbo";
    public string ProfilePath { get; init; } = "profile.json";
    public string FixturesRoot { get; init; } = "fixtures";

    /// <summary>
    /// The encrypted store these values came from, so a settings UI can edit the same instance
    /// rather than a second copy. Null when the config was assembled by hand, as in tests.
    /// </summary>
    public SecretStore? Secrets { get; init; }

    /// <summary>
    /// The plaintext secrets file. No longer read at runtime — it is an <b>import</b> path:
    /// drop keys here by hand and the next start moves them into the encrypted store and
    /// deletes this file. Superseded by the Keys tab in Settings for normal use; kept for a
    /// one-shot import of keys entered outside the app.
    /// </summary>
    public static string SecretsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingAssist", "secrets.local.json");

    public static AppConfig FromEnvironment() => From(SecretStore.Load());

    /// <summary>Overload for tests, which must not touch the real user's store.</summary>
    public static AppConfig From(SecretStore secrets) =>
        // Environment wins, so a one-off override needs no store edit at all.
        new()
        {
            Secrets = secrets,
            GroqApiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY")
                         ?? secrets.Get(SecretStore.GroqApiKey),
            XaiApiKey = Environment.GetEnvironmentVariable("XAI_API_KEY"),
            ElevenLabsApiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY"),
            GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                           ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                           ?? secrets.Get(SecretStore.GeminiApiKey),
            // Model id is a plain string so a new model needs no code change (spec D18).
            GeminiModel = Environment.GetEnvironmentVariable("GEMINI_MODEL")
                          ?? secrets.Get(SecretStore.GeminiModel) ?? DefaultGeminiModel,
            SttModel = Environment.GetEnvironmentVariable("GROQ_STT_MODEL")
                       ?? secrets.Get(SecretStore.SttModel) ?? "whisper-large-v3-turbo",
            ProfilePath = Environment.GetEnvironmentVariable("MA_PROFILE") ?? "profile.json"
        };

    public string RequireGroqKey() => GroqApiKey
        ?? throw new InvalidOperationException(
            "GROQ_API_KEY is not set.\n" +
            "  PowerShell:  $env:GROQ_API_KEY = \"gsk_...\"\n" +
            "  Persist:     setx GROQ_API_KEY \"gsk_...\"  (then reopen the terminal)");

    public string RequireGeminiKey() => GeminiApiKey
        ?? throw new InvalidOperationException(
            "GEMINI_API_KEY is not set.\n" +
            "  PowerShell:  $env:GEMINI_API_KEY = \"...\"\n" +
            "  Persist:     setx GEMINI_API_KEY \"...\"  (then reopen the terminal)");

    /// <summary>Loads the profile, writing a sample one on first run so nothing blocks.</summary>
    public ContextProfile LoadProfile()
    {
        if (File.Exists(ProfilePath)) return ContextProfile.Load(ProfilePath);

        var sample = ContextProfile.Sample();
        sample.Save(ProfilePath);
        Console.WriteLine($"No profile found — wrote a sample to {Path.GetFullPath(ProfilePath)}");
        Console.WriteLine("Edit it before drawing conclusions about answer quality.\n");
        return sample;
    }
}
