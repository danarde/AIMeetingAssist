namespace MeetingAssist.Core.Transcription;

/// <summary>
/// The profile's vocabulary as a list of terms. Whisper takes it as one hint text; the newer
/// providers take separate terms with a length cap each, and refuse a request that breaks it,
/// so a term too long for the provider is dropped rather than sent.
/// </summary>
public static class Keyterms
{
    /// <summary>Both xAI and ElevenLabs cap a term at 50 characters.</summary>
    public const int MaxLength = 50;

    public static IReadOnlyList<string> From(string vocabulary, int maxTerms, int maxWords = int.MaxValue) =>
        vocabulary
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length <= MaxLength)
            .Where(term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= maxWords)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxTerms)
            .ToList();
}
