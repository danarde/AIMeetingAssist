using MeetingAssist.App.Main;
using MeetingAssist.Core.Persistence;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

/// <summary>The main window's Home section: what it warns about, and how past meetings read.</summary>
public class HomeTests
{
    private static readonly WarningFacts AllWell = new()
    {
        GroqKey = true,
        GeminiKey = true,
        GroqPlan = GroqPlan.Developer
    };

    [Fact]
    public void Nothing_to_fix_means_no_warnings() =>
        Assert.Empty(HomeWarnings.From(AllWell));

    [Fact]
    public void A_missing_key_says_what_stops_working_and_where_to_add_it()
    {
        var warnings = HomeWarnings.From(AllWell with { GroqKey = false, GeminiKey = false });

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(Section.Keys, w.FixIn));
        Assert.Contains("nothing will be transcribed", warnings[0].Text);
        Assert.Equal("Open Keys", warnings[0].FixLabel);
    }

    [Fact]
    public void The_free_plan_is_flagged_only_when_there_is_a_Groq_key()
    {
        var withKey = HomeWarnings.From(AllWell with { GroqPlan = GroqPlan.Free });
        Assert.Contains("free plan", Assert.Single(withKey).Text);

        var withoutKey = HomeWarnings.From(AllWell with { GroqPlan = GroqPlan.Free, GroqKey = false });
        Assert.DoesNotContain(withoutKey, w => w.Text.Contains("free plan"));
    }

    [Fact]
    public void A_window_that_screen_sharing_can_see_comes_first()
    {
        var warnings = HomeWarnings.From(AllWell with
        {
            GroqKey = false,
            NotHidden = ["The overlay (affinity did not stick)"]
        });

        Assert.Equal("The overlay (affinity did not stick) is NOT hidden from screen sharing.", warnings[0].Text);
        Assert.Null(warnings[0].FixIn);
        Assert.Null(warnings[0].FixLabel);
    }

    [Fact]
    public void Hotkeys_taken_by_another_app_are_named()
    {
        var warning = Assert.Single(HomeWarnings.From(AllWell with { FailedHotkeys = ["Ctrl+Alt+R", "Ctrl+Alt+S"] }));

        Assert.Contains("Ctrl+Alt+R, Ctrl+Alt+S", warning.Text);
        Assert.Equal(Section.Hotkeys, warning.FixIn);
    }

    [Theory]
    [InlineData(true, "read WAV files")]
    [InlineData(false, "a WAV file is missing")]
    public void Playback_mode_is_never_on_by_surprise(bool ready, string expected) =>
        Assert.Contains(expected, Assert.Single(HomeWarnings.From(AllWell with
        {
            PlaybackMode = true,
            PlaybackReady = ready
        })).Text);

    [Theory]
    [InlineData("2026-10-07 14:02", "Today, 14:02")]
    [InlineData("2026-10-06 09:15", "Yesterday, 09:15")]
    [InlineData("2026-09-29 16:40", "Tue 29 Sep, 16:40")]
    [InlineData("2025-12-30 10:00", "30 Dec 2025, 10:00")]
    public void A_meeting_reads_as_when_it_started(string started, string expected) =>
        Assert.Equal(expected, MeetingRow.Started(DateTime.Parse(started), new DateTime(2026, 10, 7, 18, 0, 0)));

    [Theory]
    [InlineData(40, "under 1 min")]
    [InlineData(60, "1 min")]
    [InlineData(2_550, "42 min")]
    [InlineData(3_900, "1 h 05 min")]
    public void A_meeting_length_reads_in_minutes_then_hours(int seconds, string expected) =>
        Assert.Equal(expected, TranscriptExport.Duration(TimeSpan.FromSeconds(seconds)));
}
