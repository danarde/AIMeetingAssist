namespace MeetingAssist.Core.Mock;

public enum VoiceEngine { Gemini, Windows }

/// <summary>Builds the configured voice, the same way for a session and for the settings window's test.</summary>
public static class Voices
{
    /// <param name="name">A voice of the chosen engine, or null for its default.</param>
    /// <param name="language">ISO-639-1; picks the Windows voice when none is named.</param>
    /// <param name="geminiKey">Needed for <see cref="VoiceEngine.Gemini"/> only.</param>
    public static IVoice Create(VoiceEngine engine, string? name, string language, string? geminiKey)
    {
        if (engine == VoiceEngine.Windows) return new WindowsVoice(name, language);

        // The Windows voice by language is the fallback: a named Windows voice belongs to the
        // other setting, which is not the one in use.
        var fallback = new WindowsVoice(null, language);
        try
        {
            return new GeminiVoice(geminiKey ?? "", name, fallback);
        }
        catch
        {
            fallback.Dispose();
            throw;
        }
    }
}
