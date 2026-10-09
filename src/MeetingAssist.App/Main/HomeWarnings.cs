using MeetingAssist.Core.Transcription;

namespace MeetingAssist.App.Main;

/// <summary>The sections of the main window, in sidebar order.</summary>
public enum Section { Home, History, Profile, Devices, Keys, Hotkeys, Overlay, Rehearsal, Playback, General }

/// <summary>One thing worth fixing, and the section where it is fixed, if there is one.</summary>
public sealed record HomeWarning(string Text, Section? FixIn = null)
{
    public string? FixLabel => FixIn is { } section ? $"Open {section}" : null;
}

/// <summary>What the warnings are made from, so their wording can be tested without a running app.</summary>
public sealed record WarningFacts
{
    public bool GroqKey { get; init; }
    public bool GeminiKey { get; init; }
    public GroqPlan GroqPlan { get; init; }
    public IReadOnlyList<string> FailedHotkeys { get; init; } = [];
    public bool StorageAvailable { get; init; } = true;
    public string StoragePath { get; init; } = "";
    public int FailedWrites { get; init; }
    public string? PlaintextKeysAt { get; init; }
    public bool PlaybackMode { get; init; }
    public bool PlaybackReady { get; init; }

    /// <summary>Windows whose capture exclusion did not take, each as "The overlay (reason)".</summary>
    public IReadOnlyList<string> NotHidden { get; init; } = [];
}

/// <summary>
/// The warnings on the Home section. Shown before a meeting rather than discovered in one: the
/// overlay's banner says the same things, but only once the meeting has started.
/// </summary>
public static class HomeWarnings
{
    public static IReadOnlyList<HomeWarning> From(WarningFacts facts)
    {
        var warnings = new List<HomeWarning>();

        foreach (var window in facts.NotHidden)
            warnings.Add(new HomeWarning($"{window} is NOT hidden from screen sharing."));

        if (!facts.GroqKey)
            warnings.Add(new HomeWarning("No Groq key: nothing will be transcribed.", Section.Keys));

        if (!facts.GeminiKey)
            warnings.Add(new HomeWarning(
                "No Gemini key: Ask cannot answer, and a rehearsal has no one to play the other party.",
                Section.Keys));

        if (!facts.StorageAvailable)
            warnings.Add(new HomeWarning(
                $"Transcripts are not being saved: could not open {facts.StoragePath}."));
        else if (facts.FailedWrites > 0)
            warnings.Add(new HomeWarning(
                $"{facts.FailedWrites} transcript write(s) failed, so a saved transcript is incomplete."));

        if (facts.FailedHotkeys.Count > 0)
            warnings.Add(new HomeWarning(
                $"Already used by another app, so they do nothing here: {string.Join(", ", facts.FailedHotkeys)}.",
                Section.Hotkeys));

        // Only with a key: without one the missing key is the problem, and the plan is moot.
        if (facts.GroqKey && facts.GroqPlan == GroqPlan.Free)
            warnings.Add(new HomeWarning(
                "Groq is set to the free plan. A lively meeting can outrun its 20 requests a minute, "
                + "and the transcript then falls behind. If you have upgraded, set the plan to Developer.",
                Section.Keys));

        if (facts.PlaintextKeysAt is { } path)
            warnings.Add(new HomeWarning(
                $"A plaintext copy of your keys is still at {path}. Delete it once the keys here work.",
                Section.Keys));

        if (facts.PlaybackMode)
            warnings.Add(facts.PlaybackReady
                ? new HomeWarning(
                    "Playback mode is on: meetings read WAV files, not your microphone and speakers.",
                    Section.Playback)
                : new HomeWarning(
                    "Playback mode is on but a WAV file is missing, so meetings use live audio.",
                    Section.Playback));

        return warnings;
    }
}
