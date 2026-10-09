using MeetingAssist.App;

namespace MeetingAssist.Tests;

/// <summary>One copy of the app at a time; a later launch shows the running one instead.</summary>
public class SingleInstanceTests
{
    // A name of its own per test, so a running copy of the app on this machine cannot interfere.
    private static string UniqueName() => $@"Local\MeetingAssistTest-{Guid.NewGuid():N}";

    [Fact]
    public void A_second_launch_is_not_first_and_asks_the_first_to_show_itself()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var shown = new ManualResetEventSlim();
        first.Listen(shown.Set);

        using (var second = new SingleInstance(name))
        {
            Assert.True(first.IsFirst);
            Assert.False(second.IsFirst);
            second.ShowFirst();
        }

        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "The running copy was never asked to show itself");
    }

    [Fact]
    public void Once_the_first_copy_exits_the_next_launch_is_first_again()
    {
        var name = UniqueName();
        new SingleInstance(name).Dispose();

        using var next = new SingleInstance(name);

        Assert.True(next.IsFirst);
    }
}
