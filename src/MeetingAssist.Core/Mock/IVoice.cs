namespace MeetingAssist.Core.Mock;

/// <summary>
/// How the mock counterpart is heard. The voice plays on the default output device, where the
/// session's loopback channel captures and transcribes it exactly as it would a real caller —
/// so a mock call exercises the whole pipeline, not a shortcut around it.
///
/// Two implementations: <see cref="GeminiVoice"/>, natural and the default, and
/// <see cref="WindowsVoice"/>, local, free and instant but hard to follow — which is also what
/// speaks a line Gemini cannot. <see cref="Voices.Create"/> picks between them.
/// </summary>
public interface IVoice : IDisposable
{
    /// <summary>Which voice is speaking, for the log and the settings window.</summary>
    string Description { get; }

    /// <summary>
    /// Completes when the line has been spoken. Cancelling stops the speech mid-sentence, which
    /// is what pressing "next" during a line does.
    /// </summary>
    Task SpeakAsync(string text, CancellationToken ct);
}
