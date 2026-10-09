using MeetingAssist.Core.Profiles;

namespace MeetingAssist.Tests;

/// <summary>
/// Profiles are the app's most-edited data, and the picker is how a user switches between
/// customers. Losing one, or loading the wrong one, is a visible failure in front of a client.
/// </summary>
public class ProfileLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "MeetingAssistTests", Guid.NewGuid().ToString("N"));

    private ProfileLibrary Library => new(Path.Combine(_folder, "profiles"));

    /// <summary>Deliberately outside the library folder, which is where it lived historically.</summary>
    private string LegacyPath => Path.Combine(_folder, "profile.json");

    [Fact]
    public void A_missing_profile_is_seeded_rather_than_failing()
    {
        // Five minutes before a meeting, a starter profile beats an exception.
        var profile = Library.Load("acme");

        Assert.Equal("acme", profile.Name);
        Assert.True(Library.Exists("acme"));
    }

    [Fact]
    public void Every_field_survives_a_round_trip()
    {
        var library = Library;
        var profile = library.Load("acme");

        profile.Meeting = "Discovery call";
        profile.AboutMe = "Acme Analytics: Pulse, $29/user/month";
        profile.Questions = "Too expensive -> analyst hours";
        profile.Vocabulary = "Acme, Pulse, VSAQ";
        profile.Language = "es";
        profile.MicLabel = "Yo";
        profile.LoopbackLabel = "Cliente";
        profile.AnswerStyle = "Answer in exactly three bullets.";
        Assert.True(library.Save(profile));

        var reloaded = library.Load("acme");

        Assert.Equal("Discovery call", reloaded.Meeting);
        Assert.Equal("Acme Analytics: Pulse, $29/user/month", reloaded.AboutMe);
        Assert.Equal("Too expensive -> analyst hours", reloaded.Questions);
        Assert.Equal("Acme, Pulse, VSAQ", reloaded.Vocabulary);
        Assert.Equal("es", reloaded.Language);
        Assert.Equal("Yo", reloaded.MicLabel);
        Assert.Equal("Cliente", reloaded.LoopbackLabel);
        Assert.Equal("Answer in exactly three bullets.", reloaded.AnswerStyle);
    }

    [Fact]
    public void Profiles_are_listed_alphabetically()
    {
        var library = Library;
        library.Load("zebra");
        library.Load("acme");
        library.Load("mango");

        Assert.Equal(["acme", "mango", "zebra"], library.Names());
    }

    [Fact]
    public void The_file_name_wins_over_a_mismatched_name_inside()
    {
        // Otherwise the picker would show one profile and the session would load another.
        var library = Library;
        var profile = library.Load("acme");
        profile.Name = "something-else";
        profile.Save(library.PathFor("acme"));

        Assert.Equal("acme", library.Load("acme").Name);
    }

    [Fact]
    public void Duplicating_copies_the_content_under_a_new_name()
    {
        var library = Library;
        var original = library.Load("acme");
        original.AboutMe = "Acme Analytics";
        library.Save(original);

        var copy = library.Duplicate("acme", "acme-renewal");

        Assert.NotNull(copy);
        Assert.Equal("acme-renewal", copy.Name);
        Assert.Equal("Acme Analytics", copy.AboutMe);

        // And the original is untouched.
        Assert.Equal("Acme Analytics", library.Load("acme").AboutMe);
    }

    [Fact]
    public void Duplicating_onto_an_existing_name_is_refused()
    {
        var library = Library;
        library.Load("acme");
        library.Load("taken");

        Assert.Null(library.Duplicate("acme", "taken"));
    }

    [Fact]
    public void The_last_profile_is_never_deleted()
    {
        // An empty library leaves the picker blank and the next session with nothing to load.
        var library = Library;
        library.Load("only-one");

        Assert.False(library.Delete("only-one"));
        Assert.True(library.Exists("only-one"));
    }

    [Fact]
    public void Deleting_works_when_another_profile_remains()
    {
        var library = Library;
        library.Load("acme");
        library.Load("beta");

        Assert.True(library.Delete("beta"));
        Assert.Equal(["acme"], library.Names());
    }

    [Theory]
    [InlineData("acme/renewal", "acme-renewal")]
    [InlineData("Q3: pipeline", "Q3- pipeline")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "default")]
    [InlineData("CON", "CON-profile")]
    public void Names_that_cannot_be_file_names_are_made_into_ones(string input, string expected)
    {
        Assert.Equal(expected, ProfileLibrary.Sanitize(input));
    }

    [Fact]
    public void A_corrupt_profile_falls_back_instead_of_throwing()
    {
        var library = Library;
        Directory.CreateDirectory(library.Folder);
        File.WriteAllText(library.PathFor("broken"), "{ this is not json");

        var profile = library.Load("broken");

        Assert.Equal("broken", profile.Name);
        Assert.NotEqual("", profile.AboutMe);
    }

    [Fact]
    public void A_pre_library_profile_json_is_adopted_rather_than_replaced_by_a_sample()
    {
        Directory.CreateDirectory(_folder);
        var legacy = LegacyPath;

        var original = ContextProfile.Sample();
        original.AboutMe = "The company the user actually wrote";
        original.Save(legacy);

        var library = Library;
        Assert.True(library.ImportLegacy(legacy));

        Assert.Equal("The company the user actually wrote", library.Load("default").AboutMe);

        // The original is left alone; adopting is a copy, not a move.
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void Adoption_never_overwrites_an_existing_library()
    {
        var library = Library;
        var existing = library.Load("default");
        existing.AboutMe = "Already here";
        library.Save(existing);

        Directory.CreateDirectory(_folder);
        var legacy = LegacyPath;
        var other = ContextProfile.Sample();
        other.AboutMe = "Should not win";
        other.Save(legacy);

        Assert.False(library.ImportLegacy(legacy));
        Assert.Equal("Already here", library.Load("default").AboutMe);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test run over.
        }
    }
}
