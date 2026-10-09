using System.Diagnostics;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Spike.Commands;

public sealed record FixtureRun(
    TranscriptStore Store,
    ContextProfile Profile,
    int SegmentsSeen,
    int SegmentsTranscribed,
    int SegmentsEmpty,
    double AudioSecondsSent,
    TimeSpan WallClock);

/// <summary>
/// Replays a fixture through the real pipeline. Shared by `transcribe` and `sweep` so the
/// comparison and the single run cannot drift apart.
/// </summary>
public static class FixtureRunner
{
    public static async Task<FixtureRun> RunAsync(
        string fixtureDir,
        ContextProfile profile,
        SegmenterOptions segmenterOptions,
        TranscriptionOptions transcriptionOptions,
        ITranscriber transcriber,
        bool realTime,
        CancellationToken ct)
    {
        var (micPath, loopPath) = Options.FixtureFiles(fixtureDir);
        var store = new TranscriptStore();
        var pipelines = new List<ChannelPipeline>();

        foreach (var (path, channel) in new[] { (micPath, AudioChannelKind.Mic), (loopPath, AudioChannelKind.Loopback) })
        {
            if (!File.Exists(path)) continue;
            var source = new WavFileAudioSource(channel, path, realTime);
            var segmenter = new Segmenter(channel, segmenterOptions.Clone());
            pipelines.Add(new ChannelPipeline(source, segmenter, transcriber, transcriptionOptions, store));
        }

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(pipelines.Select(p => p.RunAsync(ct)));
        sw.Stop();

        return new FixtureRun(
            store,
            profile,
            pipelines.Sum(p => p.SegmentsSeen),
            pipelines.Sum(p => p.SegmentsTranscribed),
            pipelines.Sum(p => p.SegmentsEmpty),
            pipelines.Sum(p => p.AudioSecondsSent),
            sw.Elapsed);
    }
}
