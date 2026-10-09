using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Diagnostics;
using MeetingAssist.Core.Profiles;
using MeetingAssist.Core.Session;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

public class WavIoTests
{
    [Fact]
    public void Wrapped_pcm_round_trips_through_a_file()
    {
        var pcm = Synth.Tone(TimeSpan.FromMilliseconds(500));
        var path = Path.Combine(Path.GetTempPath(), $"ma-{Guid.NewGuid():N}.wav");

        try
        {
            File.WriteAllBytes(path, WavIo.WrapPcm(pcm));
            var (readBack, rate, channels) = WavIo.ReadPcm(path);

            Assert.Equal(AudioFormat.SampleRate, rate);
            Assert.Equal(AudioFormat.Channels, channels);
            Assert.Equal(pcm, readBack);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Streaming_writer_produces_a_readable_file_with_correct_sizes()
    {
        var pcm = Synth.Tone(TimeSpan.FromSeconds(1));
        var path = Path.Combine(Path.GetTempPath(), $"ma-{Guid.NewGuid():N}.wav");

        try
        {
            using (var writer = new WavFileWriter(path))
            {
                // Written in frames, the way capture delivers it.
                for (var pos = 0; pos < pcm.Length; pos += 1600)
                    writer.Write(pcm.AsSpan(pos, Math.Min(1600, pcm.Length - pos)));
            }

            var (readBack, _, _) = WavIo.ReadPcm(path);
            Assert.Equal(pcm.Length, readBack.Length);
            Assert.Equal(pcm, readBack);
        }
        finally { File.Delete(path); }
    }
}

public class TranscriptStoreTests
{
    private static TranscriptSegment Seg(AudioChannelKind channel, double start, double end, string text) =>
        new(Guid.NewGuid(), channel, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, SegmentCutReason.Silence);

    [Fact]
    public void Out_of_order_arrivals_are_stored_chronologically()
    {
        // Two channels transcribe concurrently, so responses genuinely arrive out of order
        // (spec FR-4.9).
        var store = new TranscriptStore();
        store.Append(Seg(AudioChannelKind.Mic, 10, 12, "third"));
        store.Append(Seg(AudioChannelKind.Loopback, 0, 2, "first"));
        store.Append(Seg(AudioChannelKind.Mic, 5, 7, "second"));

        Assert.Equal(["first", "second", "third"], store.All().Select(s => s.Text));
    }

    [Fact]
    public void GetWindow_is_anchored_to_the_newest_segment()
    {
        var store = new TranscriptStore();
        store.Append(Seg(AudioChannelKind.Loopback, 0, 2, "old"));
        store.Append(Seg(AudioChannelKind.Mic, 100, 102, "recent"));
        store.Append(Seg(AudioChannelKind.Loopback, 103, 105, "newest"));

        var window = store.GetWindow(TimeSpan.FromSeconds(10));

        Assert.Equal(["recent", "newest"], window.Select(s => s.Text));
    }

    [Fact]
    public void Render_labels_speakers_and_merges_consecutive_turns()
    {
        var profile = new ContextProfile { MicLabel = "You", LoopbackLabel = "Customer" };
        var store = new TranscriptStore();
        store.Append(Seg(AudioChannelKind.Loopback, 0, 2, "What does it cost?"));
        store.Append(Seg(AudioChannelKind.Loopback, 2, 4, "And is there a discount?"));
        store.Append(Seg(AudioChannelKind.Mic, 5, 7, "Twenty nine per seat."));

        var rendered = store.RenderAll(profile);

        Assert.Equal(
            "Customer: What does it cost? And is there a discount?\nYou: Twenty nine per seat.",
            rendered);
    }
}

public class PromptBuilderTests
{
    private static ContextProfile Profile() => new()
    {
        AboutMe = "Acme: Pulse", Language = "en",
        MicLabel = "You", LoopbackLabel = "Customer"
    };

    [Fact]
    public void System_block_contains_context_but_never_transcript()
    {
        var system = PromptBuilder.BuildSystem(Profile());

        Assert.Contains("Acme", system);
        Assert.Contains("CONTEXT", system);
        Assert.DoesNotContain("CONVERSATION SO FAR", system);
    }

    [Fact]
    public void User_block_orders_history_before_focus_and_ends_with_the_instruction()
    {
        var profile = Profile();
        var history = new[] { new TranscriptSegment(Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.Zero, TimeSpan.FromSeconds(2), "early talk", SegmentCutReason.Silence) };
        var focus = new[] { new TranscriptSegment(Guid.NewGuid(), AudioChannelKind.Loopback, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(52), "recent question", SegmentCutReason.Flush) };

        var user = PromptBuilder.BuildUser(history, focus, profile);

        var historyIndex = user.IndexOf("early talk", StringComparison.Ordinal);
        var focusIndex = user.IndexOf("recent question", StringComparison.Ordinal);
        var markerIndex = user.IndexOf("--- most recent ---", StringComparison.Ordinal);

        Assert.True(historyIndex >= 0 && markerIndex > historyIndex && focusIndex > markerIndex,
            "history must precede the focus marker, which must precede the focus text");
        Assert.EndsWith("output contract.", user);
    }

    [Fact]
    public void Language_name_is_substituted_into_the_style()
    {
        var spanish = PromptBuilder.BuildSystem(new ContextProfile { Language = "es" });
        Assert.Contains("Write in Spanish.", spanish);
        Assert.DoesNotContain("{LANGUAGE}", spanish);
    }
}

public class WerTests
{
    [Fact]
    public void Identical_text_scores_zero()
    {
        Assert.Equal(0, Wer.Compute("the quick brown fox", "the quick brown fox").Rate);
    }

    [Fact]
    public void Speaker_labels_punctuation_and_case_are_ignored()
    {
        var result = Wer.Compute("Customer: The quick, brown fox!", "customer: the quick brown fox");
        Assert.Equal(0, result.Rate);
    }

    [Fact]
    public void One_wrong_word_in_four_is_twenty_five_percent()
    {
        var result = Wer.Compute("the quick brown fox", "the quick green fox");
        Assert.Equal(1, result.Errors);
        Assert.Equal(4, result.ReferenceWords);
        Assert.Equal(0.25, result.Rate, 3);
    }

    [Fact]
    public void Missing_words_count_as_errors()
    {
        var result = Wer.Compute("one two three four", "one four");
        Assert.Equal(2, result.Errors);
    }
}

public class TimeBaseTests
{
    [Fact]
    public void Both_channels_share_one_origin_so_they_interleave_correctly()
    {
        // QPC is system-wide, which is what makes cross-channel ordering exact rather than
        // approximate. The first position seen on either channel defines zero.
        var timeBase = new TimeBase();
        const long origin = 1_000_000_000L;

        var micAt0 = timeBase.Offset(origin);
        var loopAt50ms = timeBase.Offset(origin + 500_000);  // 100ns units
        var micAt1s = timeBase.Offset(origin + 10_000_000);

        Assert.Equal(TimeSpan.Zero, micAt0);
        Assert.Equal(50, loopAt50ms.TotalMilliseconds, 1);
        Assert.Equal(1000, micAt1s.TotalMilliseconds, 1);
    }

    [Fact]
    public void Positions_before_the_origin_clamp_to_zero()
    {
        var timeBase = new TimeBase();
        timeBase.Offset(1_000_000);
        Assert.Equal(TimeSpan.Zero, timeBase.Offset(900_000));
    }
}

public class AudioFormatTests
{
    [Fact]
    public void Duration_and_byte_count_round_trip()
    {
        var duration = TimeSpan.FromSeconds(2.5);
        var bytes = AudioFormat.BytesFor(duration);

        Assert.Equal(80_000, bytes); // 16000 Hz * 2 bytes * 2.5 s
        Assert.Equal(duration, AudioFormat.DurationOf(bytes));
    }

    [Fact]
    public void BytesFor_is_always_sample_aligned()
    {
        for (var ms = 1; ms < 100; ms++)
            Assert.Equal(0, AudioFormat.BytesFor(TimeSpan.FromMilliseconds(ms)) % AudioFormat.BytesPerSample);
    }
}
