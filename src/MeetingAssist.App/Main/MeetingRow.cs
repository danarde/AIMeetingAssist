using System.Globalization;
using MeetingAssist.Core.Persistence;

namespace MeetingAssist.App.Main;

/// <summary>One row of the past-meetings list, already worded.</summary>
public sealed record MeetingRow(Guid Id, string When, string Type, string Profile, string Length, string Lines)
{
    public static MeetingRow From(MeetingSummary meeting, DateTime now) => new(
        meeting.Id,
        Started(meeting.StartedUtc.ToLocalTime().DateTime, now),
        TypeOf(meeting.Kind),
        meeting.Profile,
        TranscriptExport.Duration(meeting.Length),
        meeting.Lines.ToString(CultureInfo.InvariantCulture));

    /// <summary>Blank for meetings stored before the type was recorded, rather than a guess.</summary>
    public static string TypeOf(MeetingKind? kind) => kind switch
    {
        MeetingKind.Meeting => "Meeting",
        MeetingKind.Rehearsal => "Rehearsal",
        MeetingKind.Playback => "Playback",
        _ => ""
    };

    /// <summary>"Today, 14:02", "Yesterday, 09:15", "Tue 7 Oct, 14:02", or with the year when it is not this one.</summary>
    public static string Started(DateTime started, DateTime now)
    {
        var time = started.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (started.Date == now.Date) return $"Today, {time}";
        if (started.Date == now.Date.AddDays(-1)) return $"Yesterday, {time}";

        var format = started.Year == now.Year ? "ddd d MMM" : "d MMM yyyy";
        return $"{started.ToString(format, CultureInfo.InvariantCulture)}, {time}";
    }
}
