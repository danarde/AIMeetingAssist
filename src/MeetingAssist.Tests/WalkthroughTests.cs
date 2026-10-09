using MeetingAssist.App.Interop;
using MeetingAssist.App.Main;
using MeetingAssist.Core.Profiles;

namespace MeetingAssist.Tests;

/// <summary>The Get started section: when it opens by itself, and what its steps say.</summary>
public class WalkthroughTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    public void Opens_by_itself_only_once_and_only_without_both_keys(
        bool shownBefore, bool groq, bool gemini, bool opens) =>
        Assert.Equal(opens, Walkthrough.OpensAtStart(shownBefore, groq, gemini));

    [Fact]
    public void The_sample_profile_is_not_counted_as_prepared()
    {
        var sample = ContextProfile.Sample();
        sample.Name = "default";

        var (text, done) = Walkthrough.ProfileStatus(sample);

        Assert.False(done);
        Assert.Contains("\"default\" still holds the sample", text);
    }

    [Fact]
    public void A_profile_of_your_own_is_prepared_and_an_empty_one_is_not()
    {
        Assert.True(Walkthrough.ProfileStatus(new ContextProfile { Name = "acme", Meeting = "Renewal call" }).Done);
        Assert.Contains("is empty", Walkthrough.ProfileStatus(new ContextProfile { Name = "blank" }).Text);
    }

    [Fact]
    public void First_meeting_names_the_keys_as_bound_and_flags_one_another_app_owns()
    {
        var bindings = new HotkeySettings().Bindings;
        bindings[nameof(HotkeyAction.AskNarrow)] = "Ctrl+Alt+K";

        var steps = Walkthrough.FirstMeeting(bindings, failed: ["ctrl+alt+x"]);

        Assert.Equal([1, 2, 3, 4], steps.Select(s => s.Number));
        Assert.Contains("Ctrl+Alt+K, for notes on the last minute", steps[1].Text);
        Assert.Contains("Ctrl+Alt+X (taken by another app) hides everything", steps[2].Text);
    }
}
