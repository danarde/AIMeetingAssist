using System.Globalization;
using System.Text;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Core.Persistence;

/// <summary>
/// A stored meeting written out as text, for a person to read or to hand to an AI chat.
///
/// The header says what an outside reader cannot know: when it was, which speaker is the person
/// who recorded it, and that it is automatic speech recognition. The body is one paragraph per
/// turn, time-stamped from the start of the meeting. It is light Markdown, which reads as plain
/// text and gives a model clear structure.
/// </summary>
public static class TranscriptExport
{
    /// <summary>
    /// Consecutive lines from the same speaker closer than this are one paragraph. Segments are
    /// cut at every short pause, so without this one answer becomes a column of fragments.
    /// </summary>
    public static readonly TimeSpan SameTurn = TimeSpan.FromSeconds(10);

    public static string Render(
        MeetingSummary meeting,
        IReadOnlyList<TranscriptSegment> segments,
        string micLabel,
        string loopbackLabel,
        TimeZoneInfo zone)
    {
        var started = TimeZoneInfo.ConvertTime(meeting.StartedUtc, zone);
        var turns = Turns(segments);
        var text = new StringBuilder();

        text.Append("# Meeting transcript\n\n");
        text.Append($"- Date: {started.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}");
        if (meeting.EndedUtc is { } ended)
        {
            var end = TimeZoneInfo.ConvertTime(ended, zone);
            text.Append($" to {end.ToString("HH:mm", CultureInfo.InvariantCulture)} ({Duration(meeting.Length)})\n");
        }
        else
        {
            text.Append($" (the end was not recorded; {Duration(meeting.Length)} transcribed)\n");
        }

        text.Append($"- Profile: {meeting.Profile}\n");
        text.Append($"- Speakers: \"{micLabel}\" is the person who recorded this meeting, from their microphone. "
                    + $"\"{loopbackLabel}\" is everyone else on the call, from the meeting's audio.\n\n");

        text.Append("This is an automatic transcription made while the meeting ran. Words can be misheard, "
                    + "names especially, and where two people spoke at once the order may be off by a moment. "
                    + "Times are from the start of the meeting. It holds only what was said: no summary or "
                    + "notes were added.\n\n");

        text.Append("## Transcript\n");
        foreach (var turn in turns)
        {
            var label = turn.Channel == AudioChannelKind.Mic ? micLabel : loopbackLabel;
            text.Append($"\n[{Stamp(turn.Start)}] {label}: {turn.Text}\n");
        }

        return text.ToString();
    }

    /// <summary>"2026-10-07 1402 acme-review.txt": sorts by date, and says what it is.</summary>
    public static string FileName(MeetingSummary meeting, TimeZoneInfo zone)
    {
        var started = TimeZoneInfo.ConvertTime(meeting.StartedUtc, zone);
        var profile = string.Concat(meeting.Profile.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{started.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)} {profile}.txt";
    }

    /// <summary>"under 1 min", "42 min", "1 h 05 min".</summary>
    public static string Duration(TimeSpan length)
    {
        if (length < TimeSpan.FromMinutes(1)) return "under 1 min";

        var minutes = (int)length.TotalMinutes;
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    private static string Stamp(TimeSpan at) =>
        $"{(int)at.TotalHours:00}:{at.Minutes:00}:{at.Seconds:00}";

    private sealed record Turn(AudioChannelKind Channel, TimeSpan Start, TimeSpan End, string Text);

    private static List<Turn> Turns(IReadOnlyList<TranscriptSegment> segments)
    {
        var turns = new List<Turn>();

        foreach (var segment in segments
                     .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                     .OrderBy(s => s.Start)
                     .ThenBy(s => s.Channel))
        {
            var text = segment.Text.Trim();
            var last = turns.Count > 0 ? turns[^1] : null;

            if (last is not null && last.Channel == segment.Channel && segment.Start - last.End <= SameTurn)
                turns[^1] = last with { End = segment.End, Text = $"{last.Text} {text}" };
            else
                turns.Add(new Turn(segment.Channel, segment.Start, segment.End, text));
        }

        return turns;
    }
}
