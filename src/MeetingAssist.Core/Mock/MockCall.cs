using System.Text;
using System.Text.RegularExpressions;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Transcription;
using Serilog;

namespace MeetingAssist.Core.Mock;

public enum MockStage { Idle, Thinking, Speaking }

/// <summary>
/// Mock mode: an AI plays the other party, so a meeting can be rehearsed with the real
/// pipeline running. Turns are taken on demand (the "next line" hotkey), never on a silence
/// timer — a pause to think must never be talked over.
///
/// A turn reads the transcript the session has captured, which includes the counterpart's own
/// earlier lines as the loopback channel heard them. Nothing it says is injected into the
/// transcript directly: its voice reaches the session the way a real caller's would.
/// </summary>
public sealed class MockCall : IDisposable
{
    /// <summary>Higher than the cue notes: a scripted-sounding counterpart is a poor rehearsal.</summary>
    private const float Temperature = 0.9f;

    private readonly ContextProfile _profile;
    private readonly ICompletion _completion;
    private readonly IVoice _voice;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TranscriptSegment>>> _settleTranscript;

    /// <summary>One turn at a time; a new one cancels the current one and waits for it to unwind.</summary>
    private readonly SemaphoreSlim _turn = new(1, 1);
    private CancellationTokenSource? _current;
    private MockStage _stage;

    /// <param name="settleTranscript">
    /// Flushes what is still being spoken into the transcript and returns all of it — the same
    /// step an Ask takes first, so the end of the user's answer is not missed.
    /// </param>
    public MockCall(
        ContextProfile profile,
        ICompletion completion,
        IVoice voice,
        Func<CancellationToken, Task<IReadOnlyList<TranscriptSegment>>> settleTranscript)
    {
        _profile = profile;
        _completion = completion;
        _voice = voice;
        _settleTranscript = settleTranscript;
    }

    public string VoiceDescription => _voice.Description;

    public MockStage Stage
    {
        get => _stage;
        private set
        {
            if (_stage == value) return;
            _stage = value;
            StageChanged?.Invoke(value);
        }
    }

    public event Action<MockStage>? StageChanged;

    /// <summary>
    /// The counterpart's next line, spoken. Pressing again mid-turn supersedes: the current line
    /// stops and a new one is written from the transcript as it now stands.
    /// </summary>
    /// <returns>The line spoken, or null when the model produced nothing.</returns>
    public async Task<string?> TakeTurnAsync(CancellationToken ct)
    {
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var previous = Interlocked.Exchange(ref _current, mine);
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { /* it finished on its own meanwhile */ }

        var acquired = false;
        try
        {
            // Inside the try: a turn cancelled while still waiting must reset the stage too.
            await _turn.WaitAsync(mine.Token).ConfigureAwait(false);
            acquired = true;

            Stage = MockStage.Thinking;
            var history = await _settleTranscript(mine.Token).ConfigureAwait(false);
            var line = await NextLineAsync(history, mine.Token).ConfigureAwait(false);

            if (line is null)
            {
                Log.Warning("Mock counterpart produced nothing");
                return null;
            }

            Stage = MockStage.Speaking;
            await _voice.SpeakAsync(line, mine.Token).ConfigureAwait(false);
            return line;
        }
        finally
        {
            if (acquired) _turn.Release();

            // A superseded turn leaves the stage to the turn that replaced it.
            if (Interlocked.CompareExchange(ref _current, null, mine) == mine)
                Stage = MockStage.Idle;
        }
    }

    /// <summary>Stops the current turn, if any — used when the session stops.</summary>
    public void Cancel()
    {
        try { _current?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    /// <summary>What the counterpart would say next, without speaking it.</summary>
    public async Task<string?> NextLineAsync(IReadOnlyList<TranscriptSegment> history, CancellationToken ct)
    {
        var raw = new StringBuilder();
        await foreach (var chunk in _completion.CompleteAsync(
                           MockPromptBuilder.BuildSystem(_profile),
                           MockPromptBuilder.BuildUser(history, _profile),
                           Temperature, ct).ConfigureAwait(false))
        {
            raw.Append(chunk);
        }

        var line = Clean(raw.ToString(), _profile);
        return line.Length == 0 ? null : line;
    }

    /// <summary>
    /// Makes the model's text speakable. The prompt asks for bare speech, but a speaker label
    /// or a stray asterisk read aloud by the voice breaks the illusion in a way a prompt alone
    /// cannot be trusted to prevent.
    /// </summary>
    public static string Clean(string raw, ContextProfile profile)
    {
        var text = raw.Trim();

        foreach (var label in new[] { profile.LoopbackLabel + ":", MockPromptBuilder.Self + ":" })
        {
            if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) text = text[label.Length..];
        }

        text = Regex.Replace(text, @"[*_#`]+", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // A line wrapped whole in quotes reads as quoting someone else.
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();

        return text;
    }

    public void Dispose()
    {
        Cancel();
        _voice.Dispose();
        (_completion as IDisposable)?.Dispose();
        _turn.Dispose();
    }
}
