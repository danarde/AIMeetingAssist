using MeetingAssist.App.Interop;
using MeetingAssist.App.Overlay;
using MeetingAssist.App.Settings;
using MeetingAssist.Core.Configuration;
using MeetingAssist.Core.Persistence;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;

namespace MeetingAssist.App.Main;

/// <summary>
/// What the main window is allowed to reach. It exists so the window stays a view: it reads
/// and writes these objects and asks the host to act, but owns no session logic of its own —
/// the same reason <see cref="MeetingSession"/> knows nothing about WPF.
/// </summary>
public interface IMainHost
{
    AppSettings Settings { get; }
    OverlaySettings Overlay { get; }
    HotkeySettings Hotkeys { get; }
    SecretStore Secrets { get; }
    ProfileLibrary Profiles { get; }

    /// <summary>Where transcripts are going, or why they are not.</summary>
    string StorageDescription { get; }

    bool SessionRunning { get; }
    SessionState State { get; }

    /// <summary>The running session is a rehearsal: an AI plays the other party.</summary>
    bool Rehearsing { get; }

    /// <summary>Combinations that another application already owns (FR-8.1).</summary>
    IReadOnlyList<string> FailedHotkeys { get; }

    /// <summary>What is worth fixing before the next meeting, most serious first.</summary>
    IReadOnlyList<HomeWarning> Warnings();

    IReadOnlyList<MeetingSummary> Meetings();

    /// <summary>Where exported transcripts are written.</summary>
    string TranscriptFolder { get; }

    /// <summary>
    /// Writes a meeting's transcript to a text file in <see cref="TranscriptFolder"/>, replacing
    /// an earlier export of it. Returns the file, or why it could not be written.
    /// </summary>
    (string? Path, string? Problem) ExportTranscript(Guid meetingId);

    /// <summary>
    /// Removes a meeting and its transcript from the database for good. Files already exported
    /// stay where they are. Returns why it could not, or null when it is gone.
    /// </summary>
    Task<string?> DeleteMeetingAsync(Guid meetingId);

    /// <summary>
    /// Starts a meeting, or a rehearsal with an AI playing the other party. The overlay appears
    /// and the main window hides. Returns why it could not start, or null when it did.
    /// </summary>
    string? StartMeeting(bool rehearse);

    /// <summary>Finishes transcribing what was heard, then shows the main window again.</summary>
    Task StopMeetingAsync();

    /// <summary>Re-registers hotkeys after they were edited.</summary>
    void ApplyHotkeys();

    /// <summary>Moves the overlay to a preset immediately, so the choice can be seen.</summary>
    void ApplyOverlayPreset(int index);

    /// <summary>
    /// Rebuilds the transcriber, assistant and profile from what is now on disk. Deferred to the
    /// end of the meeting when one is running.
    /// </summary>
    void ReloadConfiguration();
}
