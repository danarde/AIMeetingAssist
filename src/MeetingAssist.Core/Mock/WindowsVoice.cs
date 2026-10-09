using System.Speech.Synthesis;
using Serilog;

namespace MeetingAssist.Core.Mock;

/// <summary>
/// The voices installed with Windows, through System.Speech. Local, free and instant, at the
/// cost of sounding synthetic — Whisper transcribes them cleanly, which is what matters for a
/// rehearsal.
/// </summary>
public sealed class WindowsVoice : IVoice
{
    private readonly SpeechSynthesizer _synth = new();

    /// <param name="voiceName">An installed voice by name, or null to pick one by language.</param>
    /// <param name="language">ISO-639-1, used when no name is given or the named one is gone.</param>
    public WindowsVoice(string? voiceName, string language)
    {
        _synth.SetOutputToDefaultAudioDevice();

        var installed = _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .ToList();

        // A named voice that has since been uninstalled falls back rather than failing, so a
        // settings file copied between machines still produces a mock call.
        var chosen =
            installed.FirstOrDefault(v => string.Equals(v.Name, voiceName, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(v => string.Equals(
                v.Culture.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault();

        if (chosen is not null) _synth.SelectVoice(chosen.Name);
        Description = chosen?.Name ?? "Windows default voice";

        if (voiceName is not null && chosen?.Name != voiceName)
            Log.Warning("Voice {Requested} is not installed; using {Chosen}", voiceName, Description);
    }

    public string Description { get; }

    /// <summary>Names of the enabled voices, for the settings window.</summary>
    public static IReadOnlyList<string> Installed()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            return [.. synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not list the installed voices");
            return [];
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The prompt object exists before SpeakAsync is called, so the completion handler can
        // recognise its own line even if a very short one finishes before the call returns.
        var prompt = new Prompt(text);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, SpeakCompletedEventArgs e)
        {
            if (!ReferenceEquals(e.Prompt, prompt)) return;

            if (e.Cancelled) done.TrySetCanceled(ct);
            else if (e.Error is not null) done.TrySetException(e.Error);
            else done.TrySetResult();
        }

        _synth.SpeakCompleted += OnCompleted;
        try
        {
            _synth.SpeakAsync(prompt);

            await using (ct.Register(() => _synth.SpeakAsyncCancel(prompt)))
            {
                await done.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _synth.SpeakCompleted -= OnCompleted;
        }
    }

    public void Dispose()
    {
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }
}
