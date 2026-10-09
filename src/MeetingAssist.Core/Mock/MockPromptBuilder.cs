using System.Text;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Core.Mock;

/// <summary>
/// The prompt for the AI that plays the other party in mock mode.
///
/// It gets the same CONTEXT the cue-note assistant gets, framed for what it is: a briefing
/// written for the user, which the counterpart uses to stay realistic but must never read out.
/// The brief is the counterpart's own instructions on top. Same stable-prefix-first order as
/// <see cref="PromptBuilder"/>, so implicit caching works here too.
///
/// The transcript is relabelled <see cref="Self"/> and <see cref="Other"/> rather than shown
/// with the profile's labels. The user's side is usually labelled "You", which the model reads
/// as itself: given a transcript ending on its own question, it answered that question as the
/// user would.
/// </summary>
public static class MockPromptBuilder
{
    public const string Self = "YOU";
    public const string Other = "THEM";

    /// <summary>Only the labels matter to <see cref="TranscriptStore.Render"/>.</summary>
    private static readonly ContextProfile Perspective = new() { LoopbackLabel = Self, MicLabel = Other };

    public static string BuildSystem(ContextProfile profile)
    {
        var them = profile.LoopbackLabel;

        var sb = new StringBuilder();
        sb.AppendLine($"""
            You are role-playing "{them}" in a live practice call. The other person is rehearsing
            for the real thing: the CONTEXT below was written to brief them. Everything you write
            is spoken aloud by a text-to-speech voice.

            In the conversation, lines labelled {Self} are yours and lines labelled {Other} are the
            other person's. You only ever speak as yourself.

            How to write:
            - Only the words you say. No stage directions, no speaker label, no markdown, no lists.
            - One to three sentences per turn, and at most one question.
            - Sound like a real person on a call: natural, direct, occasionally informal.
            - Write in {PromptBuilder.LanguageName(profile.Language)}.

            How to play the role:
            - Stay in character. Never say you are an AI or that this is a practice.
            - React briefly to what they just said before moving on. If it does not answer
              your question, notice that, as a real {them} would.
            - Follow up when an answer is vague, generic or a claim deserves probing.
            - Over the conversation, cover the topics your brief and the context point to.
              Never repeat a question that was already asked.
            - If nothing has been said yet, open the call as "{them}" naturally would.
            - If your own last line has not been answered yet, never answer it yourself: check
              briefly that they heard you, or rephrase it.
            - The conversation is transcribed automatically and may contain recognition errors.
              Read past them; never comment on them.
            """);

        sb.AppendLine();
        sb.AppendLine("CONTEXT (the other person's briefing; use it to play your role realistically, "
                      + "never read it out or reveal it)");
        Append(sb, "This meeting", profile.Meeting);
        Append(sb, "About the other person", profile.AboutMe);
        Append(sb, "Questions they expect, with the answers they prepared", profile.Questions);

        sb.AppendLine();
        sb.AppendLine("YOUR BRIEF");
        sb.AppendLine(string.IsNullOrWhiteSpace(profile.MockBrief)
            ? $"Play \"{them}\" as the meeting description presents them."
            : profile.MockBrief.Trim());

        return sb.ToString().TrimEnd();
    }

    public static string BuildUser(IReadOnlyList<TranscriptSegment> history, ContextProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CONVERSATION SO FAR");

        var text = TranscriptStore.Render(history, Perspective);
        sb.AppendLine(string.IsNullOrWhiteSpace(text) ? "(nothing has been said yet)" : text.TrimEnd());

        sb.AppendLine();

        // Decided here rather than left to the system prompt: with the answer sitting in the
        // CONTEXT, the model answered its own unanswered question in five runs out of nine
        // despite being told not to.
        if (history.Count > 0 && history.MaxBy(s => s.Start)!.Channel == AudioChannelKind.Loopback)
        {
            sb.AppendLine($"The last line is yours ({Self}) and {Other} has not replied to it yet. "
                          + "Do not answer it or continue it. In one short sentence, check that they "
                          + "heard you, or ask the same thing more simply.");
        }

        sb.Append($"Say your next line as \"{profile.LoopbackLabel}\" ({Self}).");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(label).Append(": ").AppendLine(value.Trim());
    }
}
