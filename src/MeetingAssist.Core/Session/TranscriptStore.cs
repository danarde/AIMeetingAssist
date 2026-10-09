using System.Text;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Core.Session;

/// <summary>
/// Holds the whole session transcript in memory (spec D27 — a full meeting is a few kilobytes
/// of text, so a ring buffer solved a problem that does not exist). Segments are kept sorted
/// by start time, which is also how out-of-order provider responses get reordered (FR-4.9).
/// </summary>
public sealed class TranscriptStore
{
    private readonly List<TranscriptSegment> _segments = [];
    private readonly Lock _gate = new();

    public event Action<TranscriptSegment>? SegmentAdded;

    public int Count { get { lock (_gate) return _segments.Count; } }

    public void Append(TranscriptSegment segment)
    {
        lock (_gate)
        {
            var index = _segments.FindLastIndex(s => s.Start <= segment.Start) + 1;
            _segments.Insert(index, segment);
        }
        SegmentAdded?.Invoke(segment);
    }

    /// <summary>
    /// Empties the store for a new meeting. Called by <c>MeetingSession.Start</c>: without it a
    /// second meeting in the same app run inherits the first one's transcript and feeds the
    /// previous customer's conversation into the new prompt.
    ///
    /// Raises no event — <see cref="SegmentAdded"/> means "a segment arrived", and subscribers
    /// (the persistence writer among them) have nothing to do on a reset.
    /// </summary>
    public void Clear()
    {
        lock (_gate) _segments.Clear();
    }

    public IReadOnlyList<TranscriptSegment> All()
    {
        lock (_gate) return [.. _segments];
    }

    /// <summary>
    /// The last <paramref name="window"/> of conversation, both channels interleaved
    /// chronologically. Anchored to the newest segment rather than wall-clock, so it behaves
    /// identically for live capture and fixture playback.
    /// </summary>
    public IReadOnlyList<TranscriptSegment> GetWindow(TimeSpan window)
    {
        lock (_gate)
        {
            if (_segments.Count == 0) return [];
            var latest = _segments.Max(s => s.End);
            var cutoff = latest - window;
            return [.. _segments.Where(s => s.End >= cutoff)];
        }
    }

    /// <summary>Renders segments as labeled dialogue for the prompt (spec section 7.4).</summary>
    public static string Render(IEnumerable<TranscriptSegment> segments, ContextProfile profile)
    {
        var sb = new StringBuilder();
        string? lastLabel = null;

        foreach (var s in segments.OrderBy(s => s.Start))
        {
            var label = profile.LabelFor(s.Channel);
            // Merge consecutive turns from the same speaker so chunk boundaries do not read
            // as separate utterances to the model.
            if (label == lastLabel && sb.Length > 0)
            {
                sb.Length -= 1; // drop trailing newline
                sb.Append(' ').Append(s.Text).Append('\n');
            }
            else
            {
                sb.Append(label).Append(": ").Append(s.Text).Append('\n');
            }
            lastLabel = label;
        }
        return sb.ToString().TrimEnd();
    }

    public string RenderAll(ContextProfile profile) => Render(All(), profile);
}
