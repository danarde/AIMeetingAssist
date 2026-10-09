using System.Text.RegularExpressions;

namespace MeetingAssist.Core.Diagnostics;

/// <summary>
/// Word error rate against a hand-typed reference. Turns the chunk-length sweep from
/// eyeballing into a number, which is the difference between "seems fine" and knowing.
/// </summary>
public static partial class Wer
{
    public readonly record struct Result(int Errors, int ReferenceWords, double Rate);

    public static Result Compute(string reference, string hypothesis)
    {
        var refWords = Normalize(reference);
        var hypWords = Normalize(hypothesis);

        if (refWords.Length == 0) return new Result(0, 0, 0);

        var distance = Levenshtein(refWords, hypWords);
        return new Result(distance, refWords.Length, (double)distance / refWords.Length);
    }

    /// <summary>
    /// Lowercases, drops speaker labels and punctuation, and collapses whitespace, so the
    /// comparison measures transcription rather than formatting.
    /// </summary>
    public static string[] Normalize(string text)
    {
        text = SpeakerLabel().Replace(text, " ");
        text = Punctuation().Replace(text.ToLowerInvariant(), " ");
        return Whitespace().Split(text.Trim())
            .Where(w => w.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Of the terms the reference contains, the ones the hypothesis also contains. Names and
    /// jargon are what a cue note is built on, and one wrong product name costs WER no more than
    /// a dropped "the"; this counts them on their own.
    /// </summary>
    public static (int Found, IReadOnlyList<string> Expected, IReadOnlyList<string> Missed) Terms(
        string reference, string hypothesis, IEnumerable<string> terms)
    {
        var refText = $" {string.Join(' ', Normalize(reference))} ";
        var hypText = $" {string.Join(' ', Normalize(hypothesis))} ";

        var expected = terms
            .Where(term => Normalize(term).Length > 0 && refText.Contains($" {string.Join(' ', Normalize(term))} "))
            .ToList();
        var missed = expected
            .Where(term => !hypText.Contains($" {string.Join(' ', Normalize(term))} "))
            .ToList();
        return (expected.Count - missed.Count, expected, missed);
    }

    private static int Levenshtein(string[] a, string[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    [GeneratedRegex(@"^\s*[A-Za-z][\w ]{0,20}:", RegexOptions.Multiline)]
    private static partial Regex SpeakerLabel();

    [GeneratedRegex(@"[^\p{L}\p{Nd}' ]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
