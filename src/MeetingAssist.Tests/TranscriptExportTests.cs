using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Persistence;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

/// <summary>A meeting's transcript as a text file, to read or to give to an AI chat.</summary>
public class TranscriptExportTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");

    private static readonly MeetingSummary Meeting = new(
        Guid.NewGuid(), "acme-review",
        new DateTimeOffset(2026, 10, 7, 12, 2, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 7, 12, 49, 30, TimeSpan.Zero),
        Lines: 5, LastSpeech: default);

    private static TranscriptSegment Line(AudioChannelKind channel, double start, double end, string text) =>
        new(Guid.NewGuid(), channel, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, SegmentCutReason.Silence);

    [Fact]
    public void The_header_says_when_who_and_what_it_is()
    {
        var text = TranscriptExport.Render(Meeting, [], "Alex", "Client", Madrid);

        Assert.StartsWith("# Meeting transcript\n\n- Date: Wednesday 7 October 2026, 14:02 to 14:49 (47 min)\n", text);
        Assert.Contains("- Profile: acme-review\n", text);
        Assert.Contains("\"Alex\" is the person who recorded this meeting", text);
        Assert.Contains("\"Client\" is everyone else on the call", text);
        Assert.Contains("automatic transcription", text);
        Assert.Contains("no summary or notes were added", text);
    }

    [Fact]
    public void Lines_become_time_stamped_turns_in_the_order_they_were_said()
    {
        var text = TranscriptExport.Render(Meeting,
        [
            Line(AudioChannelKind.Loopback, 7, 9, "Tell me about the rollout."),
            Line(AudioChannelKind.Mic, 4, 6, "Hi, thanks for joining."),
            Line(AudioChannelKind.Mic, 3_725, 3_730, "That is all from me.")
        ], "Alex", "Client", Madrid);

        var transcript = text[text.IndexOf("## Transcript\n", StringComparison.Ordinal)..];
        Assert.Equal(
            "## Transcript\n"
            + "\n[00:00:04] Alex: Hi, thanks for joining.\n"
            + "\n[00:00:07] Client: Tell me about the rollout.\n"
            + "\n[01:02:05] Alex: That is all from me.\n",
            transcript);
    }

    [Fact]
    public void One_speaker_without_a_long_pause_is_one_paragraph()
    {
        var text = TranscriptExport.Render(Meeting,
        [
            Line(AudioChannelKind.Mic, 0, 3, "We split it"),
            Line(AudioChannelKind.Mic, 3.5, 6, "into small components."),
            Line(AudioChannelKind.Mic, 30, 32, "Much later, a new thought."),
            Line(AudioChannelKind.Loopback, 33, 34, "  "),
        ], "You", "Them", Madrid);

        Assert.Contains("\n[00:00:00] You: We split it into small components.\n", text);
        Assert.Contains("\n[00:00:30] You: Much later, a new thought.\n", text);
        Assert.DoesNotContain("Them:", text);
    }

    [Fact]
    public void A_meeting_that_never_ended_says_so()
    {
        var crashed = Meeting with { EndedUtc = null, LastSpeech = TimeSpan.FromMinutes(18) };

        var text = TranscriptExport.Render(crashed, [], "You", "Them", Madrid);

        Assert.Contains("14:02 (the end was not recorded; 18 min transcribed)\n", text);
    }

    [Fact]
    public void The_file_name_sorts_by_date_and_survives_any_profile_name() =>
        Assert.Equal("2026-10-07 1402 acme_q4.txt",
            TranscriptExport.FileName(Meeting with { Profile = "acme/q4" }, Madrid));
}
