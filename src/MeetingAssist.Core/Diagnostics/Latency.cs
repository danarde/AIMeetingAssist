using System.Collections.Concurrent;
using System.Diagnostics;

namespace MeetingAssist.Core.Diagnostics;

/// <summary>
/// Records the time between named stages for one unit of work (a chunk, or one Ask).
/// This is the PoC's primary deliverable: spec section 5.1 is a set of hypotheses and
/// these measurements are what replace them.
/// </summary>
public sealed class StageTimer
{
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private readonly List<(string Stage, double Ms)> _marks = [];
    private double _lastMs;

    public StageTimer(string kind, string? id = null)
    {
        Kind = kind;
        Id = id ?? Guid.NewGuid().ToString("N")[..8];
    }

    public string Kind { get; }
    public string Id { get; }

    public double ElapsedMs => Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

    /// <summary>Records arrival at <paramref name="stage"/> and the delta since the previous mark.</summary>
    public void Mark(string stage)
    {
        var now = ElapsedMs;
        var delta = now - _lastMs;
        _lastMs = now;
        lock (_marks) _marks.Add((stage, delta));
        LatencyStats.Record($"{Kind}.{stage}", delta);
    }

    /// <summary>
    /// Records a stage while attributing <paramref name="excludedMs"/> of the delta to
    /// <paramref name="excludedStage"/> instead. Used where a stage's wall clock includes time
    /// that is not the thing being measured — queueing behind a provider's rate limit is not
    /// transcription latency, and folding it in would make the numbers say the opposite of
    /// what they mean.
    /// </summary>
    public void MarkExcluding(string stage, double excludedMs, string excludedStage)
    {
        var now = ElapsedMs;
        var delta = now - _lastMs;
        _lastMs = now;

        var attributed = Math.Max(0, delta - excludedMs);
        lock (_marks) _marks.Add((stage, attributed));
        LatencyStats.Record($"{Kind}.{stage}", attributed);

        if (excludedMs > 0)
        {
            lock (_marks) _marks.Add((excludedStage, excludedMs));
            LatencyStats.Record($"{Kind}.{excludedStage}", excludedMs);
        }
    }

    public IReadOnlyList<(string Stage, double Ms)> Marks
    {
        get { lock (_marks) return _marks.ToArray(); }
    }

    public string Format()
    {
        var parts = Marks.Select(m => $"{m.Stage}={m.Ms:F0}ms");
        return $"[{Kind} {Id}] {string.Join(" ", parts)} total={ElapsedMs:F0}ms";
    }
}

/// <summary>
/// Process-wide collector. Percentiles are computed at the end of a run for the Spike, which is
/// short-lived; the live app runs indefinitely and never reads this, so each name's samples are
/// capped at <see cref="MaxSamplesPerName"/> — recent history is what a p95 needs, and an
/// unbounded list would otherwise grow for as long as a meeting runs.
/// </summary>
public static class LatencyStats
{
    private const int MaxSamplesPerName = 1000;

    private static readonly ConcurrentDictionary<string, List<double>> Samples = new();

    public static void Record(string name, double ms)
    {
        var list = Samples.GetOrAdd(name, _ => []);
        lock (list)
        {
            if (list.Count >= MaxSamplesPerName) list.RemoveAt(0);
            list.Add(ms);
        }
    }

    public static void Reset() => Samples.Clear();

    public static IReadOnlyList<StatLine> Snapshot()
    {
        var result = new List<StatLine>();
        foreach (var (name, list) in Samples)
        {
            double[] values;
            lock (list) values = [.. list];
            if (values.Length == 0) continue;
            Array.Sort(values);
            result.Add(new StatLine(
                name,
                values.Length,
                Percentile(values, 0.50),
                Percentile(values, 0.95),
                values[^1]));
        }
        return [.. result.OrderBy(r => r.Name, StringComparer.Ordinal)];
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static string FormatTable()
    {
        var rows = Snapshot();
        if (rows.Count == 0) return "(no latency samples recorded)";

        var nameWidth = Math.Max(24, rows.Max(r => r.Name.Length));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"stage".PadRight(nameWidth)}  {"n",5}  {"p50 ms",9}  {"p95 ms",9}  {"max ms",9}");
        sb.AppendLine(new string('-', nameWidth + 40));
        foreach (var r in rows)
            sb.AppendLine($"{r.Name.PadRight(nameWidth)}  {r.Count,5}  {r.P50,9:F0}  {r.P95,9:F0}  {r.Max,9:F0}");
        return sb.ToString();
    }

    public readonly record struct StatLine(string Name, int Count, double P50, double P95, double Max);
}
