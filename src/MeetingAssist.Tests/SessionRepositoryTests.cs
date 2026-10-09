using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Persistence;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

/// <summary>
/// Persistence is the only part of a meeting that outlives it, so the round-trip is worth
/// asserting exactly — a transcript stored with the wrong timestamps or the wrong channel is
/// worse than no transcript, because it looks correct.
/// </summary>
public class SessionRepositoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "MeetingAssistTests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_folder, "sessions.db");

    private static TranscriptSegment Segment(
        AudioChannelKind channel, double startSeconds, double endSeconds, string text) =>
        new(Guid.NewGuid(), channel, TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds), text, SegmentCutReason.Silence);

    [Fact]
    public async Task A_session_and_its_segments_survive_a_round_trip()
    {
        var id = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var mic = Segment(AudioChannelKind.Mic, 0, 2.5, "What does the licence cost?");
        var loopback = Segment(AudioChannelKind.Loopback, 2.5, 5, "Twenty-nine per seat.");

        await using var repository = SessionRepository.Open(DbPath);
        Assert.True(repository.Available);

        repository.StartSession(id, "acme-discovery", started);
        repository.RecordSegment(id, mic);
        repository.RecordSegment(id, loopback);
        await repository.FlushAsync();

        var segments = repository.ReadSegments(id);
        Assert.Equal(2, segments.Count);

        Assert.Equal(mic.Id, segments[0].Id);
        Assert.Equal(AudioChannelKind.Mic, segments[0].Channel);
        Assert.Equal(mic.Start, segments[0].Start);
        Assert.Equal(mic.End, segments[0].End);
        Assert.Equal(mic.Text, segments[0].Text);
        Assert.Equal(mic.CutReason, segments[0].CutReason);

        Assert.Equal(AudioChannelKind.Loopback, segments[1].Channel);

        var session = Assert.Single(repository.ReadSessions());
        Assert.Equal(id, session.Id);
        Assert.Equal("acme-discovery", session.Profile);
        Assert.Null(session.EndedUtc);

        // Second-level equality: the value is stored as a round-trip string, and sub-tick drift
        // through SQLite is not what this test is about.
        Assert.Equal(started.ToUnixTimeSeconds(), session.StartedUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Segments_are_readable_before_the_session_ends()
    {
        // FR-5.4: written as they finalize, not batched at stop. A crash forty minutes into a
        // meeting must leave forty minutes of transcript on disk.
        var id = Guid.NewGuid();

        await using var repository = SessionRepository.Open(DbPath);
        repository.StartSession(id, "default", DateTimeOffset.UtcNow);
        repository.RecordSegment(id, Segment(AudioChannelKind.Mic, 0, 1, "mid-meeting"));
        await repository.FlushAsync();

        Assert.Single(repository.ReadSegments(id));
    }

    [Fact]
    public async Task Ending_a_session_records_when_it_finished()
    {
        var id = Guid.NewGuid();

        await using var repository = SessionRepository.Open(DbPath);
        repository.StartSession(id, "default", DateTimeOffset.UtcNow);
        repository.EndSession(id, DateTimeOffset.UtcNow);
        await repository.FlushAsync();

        var session = Assert.Single(repository.ReadSessions());
        Assert.NotNull(session.EndedUtc);
    }

    [Fact]
    public async Task Two_sessions_in_one_database_keep_their_segments_apart()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await using var repository = SessionRepository.Open(DbPath);
        repository.StartSession(first, "customer-a", DateTimeOffset.UtcNow);
        repository.RecordSegment(first, Segment(AudioChannelKind.Mic, 0, 1, "customer A pricing"));

        repository.StartSession(second, "customer-b", DateTimeOffset.UtcNow.AddMinutes(1));
        repository.RecordSegment(second, Segment(AudioChannelKind.Mic, 0, 1, "customer B pricing"));
        await repository.FlushAsync();

        Assert.Equal("customer A pricing", Assert.Single(repository.ReadSegments(first)).Text);
        Assert.Equal("customer B pricing", Assert.Single(repository.ReadSegments(second)).Text);
        Assert.Equal(2, repository.ReadSessions().Count);
    }

    [Fact]
    public async Task The_meetings_list_is_newest_first_and_leaves_out_meetings_where_nothing_was_said()
    {
        var started = DateTimeOffset.UtcNow.AddHours(-2);
        var older = Guid.NewGuid();
        var silent = Guid.NewGuid();
        var crashed = Guid.NewGuid();

        await using var repository = SessionRepository.Open(DbPath);
        repository.StartSession(older, "customer-a", started);
        repository.RecordSegment(older, Segment(AudioChannelKind.Mic, 0, 4, "hello"));
        repository.RecordSegment(older, Segment(AudioChannelKind.Loopback, 4, 9, "hi"));
        repository.EndSession(older, started.AddMinutes(30));

        repository.StartSession(silent, "customer-a", started.AddMinutes(40));
        repository.EndSession(silent, started.AddMinutes(41));

        // Never ended: its length is as far as the transcript got.
        repository.StartSession(crashed, "customer-b", started.AddMinutes(50));
        repository.RecordSegment(crashed, Segment(AudioChannelKind.Mic, 600, 754, "still talking"));
        await repository.FlushAsync();

        var meetings = repository.ReadMeetings();

        Assert.Equal([crashed, older], meetings.Select(m => m.Id));
        Assert.Equal(1, meetings[0].Lines);
        Assert.Null(meetings[0].EndedUtc);
        Assert.Equal(TimeSpan.FromSeconds(754), meetings[0].Length);
        Assert.Equal(2, meetings[1].Lines);
        Assert.Equal("customer-a", meetings[1].Profile);
        Assert.Equal(30, (int)Math.Round(meetings[1].Length.TotalMinutes));
    }

    [Fact]
    public async Task Data_written_in_one_process_is_there_for_the_next()
    {
        var id = Guid.NewGuid();

        await using (var writing = SessionRepository.Open(DbPath))
        {
            writing.StartSession(id, "default", DateTimeOffset.UtcNow);
            writing.RecordSegment(id, Segment(AudioChannelKind.Loopback, 0, 3, "survives a restart"));
            writing.EndSession(id, DateTimeOffset.UtcNow);
        }

        await using var reopened = SessionRepository.Open(DbPath);
        Assert.Equal("survives a restart", Assert.Single(reopened.ReadSegments(id)).Text);
    }

    [Fact]
    public async Task An_unopenable_database_degrades_instead_of_throwing()
    {
        // A directory where the file should be: the open fails for a reason no retry fixes.
        var occupied = Path.Combine(_folder, "occupied.db");
        Directory.CreateDirectory(occupied);

        await using var repository = SessionRepository.Open(occupied);

        Assert.False(repository.Available);

        // Every write must still be safe to call — the session does not know or care.
        var id = Guid.NewGuid();
        repository.StartSession(id, "default", DateTimeOffset.UtcNow);
        repository.RecordSegment(id, Segment(AudioChannelKind.Mic, 0, 1, "lost, but not fatal"));
        repository.EndSession(id, DateTimeOffset.UtcNow);
        await repository.FlushAsync();

        Assert.Empty(repository.ReadSegments(id));
        Assert.Empty(repository.ReadSessions());
    }

    [Fact]
    public async Task Concurrent_writers_all_land()
    {
        // The two channel pipelines write from different threads. The queue is what keeps
        // SQLite single-threaded; this asserts nothing is dropped on the way in.
        var id = Guid.NewGuid();

        await using var repository = SessionRepository.Open(DbPath);
        repository.StartSession(id, "default", DateTimeOffset.UtcNow);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                var at = worker * 25 + i;
                repository.RecordSegment(id, Segment(
                    worker % 2 == 0 ? AudioChannelKind.Mic : AudioChannelKind.Loopback,
                    at, at + 1, $"segment {at}"));
            }
        })));

        await repository.FlushAsync();
        Assert.Equal(200, repository.ReadSegments(id).Count);
    }

    [Fact]
    public async Task Deleting_a_meeting_removes_it_and_its_lines_and_nothing_else()
    {
        var kept = Guid.NewGuid();
        var deleted = Guid.NewGuid();

        await using var repository = SessionRepository.Open(DbPath);
        repository.StartSession(kept, "default", DateTimeOffset.UtcNow.AddHours(-1));
        repository.RecordSegment(kept, Segment(AudioChannelKind.Mic, 0, 2, "stays"));
        repository.StartSession(deleted, "default", DateTimeOffset.UtcNow);
        repository.RecordSegment(deleted, Segment(AudioChannelKind.Mic, 0, 2, "goes"));
        repository.RecordSegment(deleted, Segment(AudioChannelKind.Loopback, 2, 4, "goes too"));
        await repository.FlushAsync();

        repository.DeleteSession(deleted);
        await repository.FlushAsync();

        Assert.Equal(0, repository.FailedWrites);
        Assert.Equal(kept, Assert.Single(repository.ReadMeetings()).Id);
        Assert.Empty(repository.ReadSegments(deleted));
        Assert.Equal("stays", Assert.Single(repository.ReadSegments(kept)).Text);
    }

    [Theory]
    [InlineData(MeetingKind.Meeting)]
    [InlineData(MeetingKind.Rehearsal)]
    [InlineData(MeetingKind.Playback)]
    public async Task A_meeting_keeps_its_kind(MeetingKind kind)
    {
        var id = Guid.NewGuid();

        await using var repository = SessionRepository.Open(DbPath);
        repository.StartSession(id, "default", DateTimeOffset.UtcNow, kind);
        repository.RecordSegment(id, Segment(AudioChannelKind.Mic, 0, 2, "hello"));
        await repository.FlushAsync();

        Assert.Equal(kind, Assert.Single(repository.ReadMeetings()).Kind);
    }

    [Fact]
    public async Task A_database_from_before_the_kind_was_recorded_still_opens_and_reads_it_as_unknown()
    {
        // The schema as it shipped up to 2026-10-08, with one meeting in it.
        Directory.CreateDirectory(_folder);
        var id = Guid.NewGuid();
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE sessions (id TEXT PRIMARY KEY, profile TEXT NOT NULL,
                                       started_utc TEXT NOT NULL, ended_utc TEXT);
                CREATE TABLE segments (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
                                       channel TEXT NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
                                       text TEXT NOT NULL, cut_reason TEXT NOT NULL);
                INSERT INTO sessions VALUES ('{id:D}', 'default', '{DateTimeOffset.UtcNow:O}', NULL);
                INSERT INTO segments VALUES ('{Guid.NewGuid():D}', '{id:D}', 'Mic', 0, 2000, 'old line', 'Silence');
                """;
            command.ExecuteNonQuery();
        }

        await using var repository = SessionRepository.Open(DbPath);
        Assert.True(repository.Available);

        var meeting = Assert.Single(repository.ReadMeetings());
        Assert.Equal(id, meeting.Id);
        Assert.Null(meeting.Kind);

        // And new meetings record theirs alongside it.
        var rehearsal = Guid.NewGuid();
        repository.StartSession(rehearsal, "default", DateTimeOffset.UtcNow.AddMinutes(1), MeetingKind.Rehearsal);
        repository.RecordSegment(rehearsal, Segment(AudioChannelKind.Mic, 0, 2, "new line"));
        await repository.FlushAsync();
        Assert.Equal(MeetingKind.Rehearsal, repository.ReadMeetings()[0].Kind);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test run over.
        }
    }
}
