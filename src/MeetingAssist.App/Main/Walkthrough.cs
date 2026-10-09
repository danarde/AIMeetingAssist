using MeetingAssist.App.Interop;
using MeetingAssist.Core.Profiles;

namespace MeetingAssist.App.Main;

/// <summary>One numbered step of a first meeting.</summary>
public sealed record NumberedStep(int Number, string Text);

/// <summary>
/// What the Get started section decides and says, kept out of the window so it can be tested.
/// The section walks through how the app works, the two keys, the profile and a first meeting.
/// </summary>
public static class Walkthrough
{
    public const int Steps = 4;

    public const string GroqKeysUrl = "https://console.groq.com/keys";
    public const string GeminiKeysUrl = "https://aistudio.google.com/apikey";

    /// <summary>The guide is not installed with the app, so it is read where it is published.</summary>
    public const string ProfileGuideUrl =
        "https://github.com/danarde/AIMeetingAssist/blob/main/skills/meeting-profile/profile-guide.md";

    /// <summary>
    /// Opens by itself once, for someone who has not set the app up yet. It decides from what
    /// is actually stored as well as from the flag: an update, or a reinstall over saved keys,
    /// opens on Home as it always did.
    /// </summary>
    public static bool OpensAtStart(bool shownBefore, bool groqKey, bool geminiKey) =>
        !shownBefore && !(groqKey && geminiKey);

    public static (string Text, bool Done) KeyStatus(string service, bool present) => present
        ? ($"{service} key saved", true)
        : ($"No {service} key yet", false);

    /// <summary>Whether the profile describes a meeting of the user's own rather than the sample.</summary>
    public static (string Text, bool Done) ProfileStatus(ContextProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Meeting))
            return ($"Your profile \"{profile.Name}\" is empty.", false);

        return profile.Meeting.Trim() == ContextProfile.Sample().Meeting
            ? ($"Your profile \"{profile.Name}\" still holds the sample meeting.", false)
            : ($"Your profile \"{profile.Name}\" is filled in.", true);
    }

    /// <summary>
    /// A first meeting, step by step, naming the keys as they are actually bound. A hotkey
    /// another app owns is said to be unavailable, since pressing it would do nothing.
    /// </summary>
    public static IReadOnlyList<NumberedStep> FirstMeeting(
        IReadOnlyDictionary<string, string> bindings, IReadOnlyCollection<string> failed)
    {
        string Key(HotkeyAction action) =>
            bindings.TryGetValue(action.ToString(), out var combo) && combo.Length > 0
                ? failed.Contains(combo, StringComparer.OrdinalIgnoreCase)
                    ? $"{combo} (taken by another app)"
                    : combo
                : "(no hotkey)";

        string[] steps =
        [
            $"Join your call, then press Start meeting on Home, or {Key(HotkeyAction.ToggleSession)} "
            + "from anywhere. This window hides and the overlay appears in a corner of the screen.",
            $"When you want help, press Ask on the overlay, or {Key(HotkeyAction.AskNarrow)}, for "
            + $"notes on the last minute. {Key(HotkeyAction.AskWide)} looks at the last three.",
            $"{Key(HotkeyAction.ToggleOverlay)} hides or shows the overlay. "
            + $"{Key(HotkeyAction.PanicHide)} hides everything at once.",
            $"Press {Key(HotkeyAction.ToggleSession)} again to stop. The transcript is saved under History."
        ];
        return steps.Select((text, i) => new NumberedStep(i + 1, text)).ToList();
    }
}
