using MeetingAssist.Core.Audio;

namespace MeetingAssist.Core.Transcription;

/// <summary>A transcribed utterance, attributed to a channel and placed on the session timeline.</summary>
public sealed record TranscriptSegment(
    Guid Id,
    AudioChannelKind Channel,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    SegmentCutReason CutReason)
{
    public TimeSpan Duration => End - Start;
}

public sealed class TranscriptionOptions
{
    public string ModelId { get; set; } = "whisper-large-v3-turbo";

    /// <summary>ISO-639-1, always sent explicitly (spec FR-4.4). Auto-detect is unreliable on short chunks.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Static vocabulary priming (spec D9/D10). Never prior transcript.</summary>
    public string Vocabulary { get; set; } = "";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// Transcribes one audio segment. Deliberately minimal — the concurrency, ordering and event
/// surface live in <see cref="TranscriptionPipeline"/> so that a genuinely streaming provider
/// (Deepgram, AssemblyAI) can replace the pipeline without every provider carrying that
/// machinery. Spec D6 keeps that swap open; this is the shape it settled into.
/// </summary>
public interface ITranscriber
{
    string Name { get; }

    /// <summary>Returns the text, or null if the segment yielded nothing usable.</summary>
    Task<TranscriptionResult?> TranscribeAsync(AudioSegment segment, TranscriptionOptions options, CancellationToken ct);

    /// <summary>
    /// Raised once per segment: <c>true</c> when the provider answered — with text, or with a
    /// confirmed nothing — and <c>false</c> when it could not be reached or refused after every
    /// retry.
    ///
    /// This exists because <see cref="TranscribeAsync"/> returns null for both "silence" and
    /// "the network is down", which are indistinguishable to the caller. Without the signal a
    /// total transcription outage looks exactly like a quiet room: the overlay would show a
    /// healthy indicator over an empty transcript, which is the misleading state FR-7.6 exists
    /// to prevent.
    /// </summary>
    event Action<bool>? AttemptCompleted;
}

/// <summary>
/// <paramref name="WaitedForSlot"/> is time spent queued behind the provider's rate limit
/// rather than waiting on the provider. It is reported separately so it can be subtracted from
/// the measured STT latency — otherwise a paced run makes transcription look 60s slow when the
/// request itself took 300ms.
/// </summary>
public sealed record TranscriptionResult(
    string Text, double? AudioSeconds, int Attempts, TimeSpan WaitedForSlot = default);
