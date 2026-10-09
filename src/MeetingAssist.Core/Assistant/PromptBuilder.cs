using System.Text;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Core.Assistant;

/// <summary>
/// Builds the prompt in the fixed block order of spec section 7.1.
///
/// Order is a caching requirement, not a style preference: implicit caching matches on an
/// exact token prefix, so the stable blocks must come first and must be byte-identical across
/// requests. Nothing varying (timestamp, counter, session id) may appear before the transcript.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Default answer style (FR-6.7). Not itself editable on disk — <see cref="Profiles.ContextProfile.AnswerStyle"/>
    /// is the editable field; a blank one falls back to this constant.
    /// </summary>
    public const string DefaultStyle = """
        You are a real-time briefing assistant. The user is taking part in a live meeting.
        They cannot pause the conversation to read. Your job is to give them cue notes for
        their own reference: the key things to keep in mind right now.

        Output contract:
        - At most 5 bullets. Aim for 8 words each, never more than 12.
        - Bullets only. No preamble, no heading, no closing remark, no caveats.
        - Notes to jog memory, not a script to read aloud.
        - Concrete and specific. Prefer a number, a name or a fact over a generalisation.
        - If the other party asked something the context can answer, lead with that answer.
        - Only state facts, numbers, prices or timelines found in CONTEXT or the conversation.
          If the answer is not there, the cue is to follow up on it. Never guess one.
        - Never hedge, never add disclaimers, never describe your own limitations.
        - Write in {LANGUAGE}.

        Example output:
        - 31 of 40 pipelines moved, 3 wait on CRM
        - 14 March holds if CRM export fixed by 28 Feb
        - Reporting work is outside scope: 6 days
        - Ask who signs off the extra scope

        Example output:
        - Lead with the onboarding case study
        - Setup cut from 6 weeks to 10 days
        - Ask what success looks like at 90 days
        """;

    /// <summary>
    /// The stable prefix: style template plus the pre-meeting context. Identical for every Ask
    /// in a session, which is what makes prefix caching effective.
    /// </summary>
    public static string BuildSystem(ContextProfile profile, string? styleTemplate = null)
    {
        var style = (styleTemplate ?? DefaultStyle)
            .Replace("{LANGUAGE}", LanguageName(profile.Language));

        var sb = new StringBuilder(style);
        sb.AppendLine().AppendLine().AppendLine("CONTEXT");

        Append(sb, "This meeting", profile.Meeting);
        Append(sb, "About the user", profile.AboutMe);
        Append(sb, "Likely questions, with the user's answers", profile.Questions);
        Append(sb, "Speakers", $"\"{profile.LoopbackLabel}\" is the other party. \"{profile.MicLabel}\" is the user.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>The volatile blocks: conversation so far, the recent window, and the instruction.</summary>
    public static string BuildUser(
        IReadOnlyList<TranscriptSegment> history,
        IReadOnlyList<TranscriptSegment> focus,
        ContextProfile profile)
    {
        var sb = new StringBuilder();

        sb.AppendLine("CONVERSATION SO FAR");
        var historyText = TranscriptStore.Render(history, profile);
        sb.AppendLine(string.IsNullOrWhiteSpace(historyText) ? "(nothing transcribed yet)" : historyText);

        sb.AppendLine();
        sb.AppendLine("--- most recent ---");
        var focusText = TranscriptStore.Render(focus, profile);
        sb.AppendLine(string.IsNullOrWhiteSpace(focusText) ? "(silence)" : focusText);

        sb.AppendLine();
        sb.Append("Give the user cue notes for what to say next, following the output contract.");

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(label).Append(": ").AppendLine(value.Trim());
    }

    internal static string LanguageName(string iso) => iso.ToLowerInvariant() switch
    {
        "en" => "English",
        "es" => "Spanish",
        "fr" => "French",
        "de" => "German",
        "pt" => "Portuguese",
        "it" => "Italian",
        _ => iso
    };
}
