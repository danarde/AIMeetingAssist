using MeetingAssist.Core.Profiles;

namespace MeetingAssist.Tests;

/// <summary>
/// Reading a profile: the files written before the fields were made generic, and the JSON an
/// AI chat writes, which rarely arrives as a bare object.
/// </summary>
public class ContextProfileTests
{
    [Fact]
    public void A_profile_from_before_the_generic_fields_keeps_everything_the_user_wrote()
    {
        var profile = ContextProfile.FromJson("""
            {
              "name": "acme",
              "company": "Acme Analytics, BI tooling.",
              "product": "Pulse, $29 a seat.",
              "objections": "Too expensive -> analyst hours saved",
              "notes": "Discovery call with procurement.",
              "vocabulary": "Acme, Pulse"
            }
            """);

        Assert.Equal("Discovery call with procurement.", profile.Meeting);
        Assert.Equal("Acme Analytics, BI tooling.\n\nPulse, $29 a seat.", profile.AboutMe);
        Assert.Equal("Too expensive -> analyst hours saved", profile.Questions);
        Assert.Equal("Acme, Pulse", profile.Vocabulary);
    }

    [Fact]
    public void The_new_fields_win_over_old_ones_left_beside_them()
    {
        var profile = ContextProfile.FromJson("""{ "meeting": "New", "notes": "Old" }""");

        Assert.Equal("New", profile.Meeting);
    }

    [Fact]
    public void Saving_writes_only_the_new_field_names()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.json");
        try
        {
            ContextProfile.FromJson("""{ "company": "Acme", "notes": "Call" }""").Save(path);
            var written = File.ReadAllText(path);

            Assert.Contains("\"aboutMe\": \"Acme\"", written);
            Assert.DoesNotContain("\"company\"", written);
            Assert.DoesNotContain("\"notes\"", written);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Json_from_a_chat_is_read_through_its_code_fence_and_chatter()
    {
        var profile = ContextProfile.FromJson("""
            Here is your profile:

            ```json
            {
              "name": "northwind-checkin",
              "meeting": "Check-in with Priya.",
              "questions": "Will we make the date? -> Yes",
              "loopbackLabel": "Priya",
            }
            ```

            Let me know if you want changes.
            """);

        Assert.Equal("northwind-checkin", profile.Name);
        Assert.Equal("Check-in with Priya.", profile.Meeting);
        Assert.Equal("Priya", profile.LoopbackLabel);
        Assert.Equal("You", profile.MicLabel);
    }

    /// <summary>
    /// The guide given to AIs (skills/meeting-profile) shows complete profiles as examples. An
    /// AI copies their shape, so each must be one the app accepts, within the vocabulary limit.
    /// </summary>
    [Fact]
    public void Every_example_in_the_profile_guide_is_a_profile_the_app_reads()
    {
        var guide = File.ReadAllText(GuidePath());
        var examples = System.Text.RegularExpressions.Regex
            .Matches(guide, "```json\\r?\\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value)
            .Where(json => !json.Contains("\"...\""))
            .ToList();

        Assert.True(examples.Count >= 3, $"Expected the worked examples, found {examples.Count}.");
        foreach (var json in examples)
        {
            var profile = ContextProfile.FromJson(json);

            Assert.NotEqual(ProfileLibrary.DefaultName, profile.Name);
            Assert.Equal(ProfileLibrary.Sanitize(profile.Name), profile.Name);
            Assert.False(string.IsNullOrWhiteSpace(profile.Meeting), profile.Name);
            Assert.False(string.IsNullOrWhiteSpace(profile.AboutMe), profile.Name);
            Assert.False(string.IsNullOrWhiteSpace(profile.Questions), profile.Name);
            Assert.True(profile.Vocabulary.Length < 800, profile.Name);
        }
    }

    /// <summary>Found from this source file, so it works wherever the build output goes.</summary>
    private static string GuidePath([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var folder = new FileInfo(source).Directory; folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "skills", "meeting-profile", "profile-guide.md");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("skills/meeting-profile/profile-guide.md is not above the test source.");
    }

    [Theory]
    [InlineData("I could not write a profile without more details.")]
    [InlineData("{ \"meeting\": ")]
    [InlineData("{ \"meeting\": 5 }")]
    public void Text_that_is_not_a_profile_is_refused_with_a_reason(string text)
    {
        var error = Assert.Throws<InvalidDataException>(() => ContextProfile.FromJson(text));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}
