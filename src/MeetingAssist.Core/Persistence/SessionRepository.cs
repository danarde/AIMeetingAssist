using System.Globalization;
using System.Threading.Channels;
using MeetingAssist.Core.Audio;
using MeetingAssist.Core.Transcription;
using Microsoft.Data.Sqlite;
using Serilog;

namespace MeetingAssist.Core.Persistence;

/// <summary>One stored meeting, as written: a round-trip is how persistence is tested.</summary>
public sealed record StoredSession(
    Guid Id, string Profile, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc);

/// <summary>
/// A meeting as the main window lists it: when, with which profile, and how much was said.
/// <see cref="LastSpeech"/> stands in for the length of a meeting that never recorded its end,
/// which is what a crash or a killed process leaves behind.
/// </summary>
public sealed record MeetingSummary(
    Guid Id, string Profile, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, int Lines, TimeSpan LastSpeech,
    MeetingKind? Kind = null)
{
    public TimeSpan Length => EndedUtc is { } ended ? ended - StartedUtc : LastSpeech;
}

/// <summary>
/// What a session was. Null when it is not known: meetings stored before the kind was recorded
/// (2026-10-08).
/// </summary>
public enum MeetingKind
{
    /// <summary>A real meeting, on live audio.</summary>
    Meeting,

    /// <summary>A mock call, with an AI playing the other party.</summary>
    Rehearsal,

    /// <summary>A test run on recorded WAV files instead of live audio.</summary>
    Playback
}

/// <summary>
/// Durable transcript storage (spec FR-5.2, FR-5.4).
///
/// Two properties drive the design:
///
/// 1. Segments are written as they finalize, not batched at stop. A crash forty minutes into a
///    meeting must not cost the transcript, which is the only artefact of the meeting that
///    outlives it.
/// 2. Storage failure is never fatal. A locked file or a full disk degrades to "this meeting is
///    not being saved" — it does not end a live session. <see cref="Open"/> therefore never
///    throws, and <see cref="Available"/> reports what happened.
///
/// All writes are queued and applied by a single consumer task, so the two channel pipelines
/// never touch SQLite concurrently and never pay disk latency on the audio path. Reads open
/// their own short-lived connection rather than sharing the writer's.
/// </summary>
public sealed class SessionRepository : IAsyncDisposable
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;

        CREATE TABLE IF NOT EXISTS sessions (
            id          TEXT PRIMARY KEY,
            profile     TEXT NOT NULL,
            started_utc TEXT NOT NULL,
            ended_utc   TEXT
        );

        CREATE TABLE IF NOT EXISTS segments (
            id         TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            channel    TEXT NOT NULL,
            start_ms   INTEGER NOT NULL,
            end_ms     INTEGER NOT NULL,
            text       TEXT NOT NULL,
            cut_reason TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_segments_session ON segments (session_id, start_ms);
        """;

    /// <summary>
    /// Columns added after the first release. CREATE TABLE IF NOT EXISTS leaves an existing table
    /// as it is, so a database from before a column existed gets it here, empty for old rows.
    /// </summary>
    private static readonly (string Table, string Column, string Type)[] AddedColumns =
    [
        ("sessions", "kind", "TEXT")
    ];

    private readonly SqliteConnection? _connection;
    private readonly Channel<Op>? _queue;
    private readonly Task _writer = Task.CompletedTask;
    private int _disposed;

    private SessionRepository(SqliteConnection? connection, string path)
    {
        Path = path;
        _connection = connection;

        if (connection is null) return;

        _queue = Channel.CreateUnbounded<Op>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Path { get; }

    /// <summary>False when the database could not be opened. Every write then silently no-ops.</summary>
    public bool Available => _connection is not null;

    private int _failedWrites;

    /// <summary>
    /// Writes that were dropped or failed. Non-zero means the transcript on disk is incomplete.
    /// Incremented from both the calling threads and the writer task, hence the interlocked read.
    /// </summary>
    public int FailedWrites => Volatile.Read(ref _failedWrites);

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingAssist", "sessions.db");

    /// <summary>
    /// Opens (creating if needed) the transcript database. Never throws: a session that cannot
    /// be stored is still a session worth having.
    /// </summary>
    public static SessionRepository Open(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = target,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Pooling would keep the file handle alive past disposal, holding a lock on a
                // database the user (or a test) has every reason to expect is released.
                Pooling = false
            }.ToString());

            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = Schema;
            command.ExecuteNonQuery();
            AddMissingColumns(connection);

            Log.Information("Transcripts are being stored at {Path}", target);
            return new SessionRepository(connection, target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Transcript storage unavailable at {Path}; this meeting will not be saved", target);
            return new SessionRepository(null, target);
        }
    }

    private static void AddMissingColumns(SqliteConnection connection)
    {
        foreach (var (table, column, type) in AddedColumns)
        {
            using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if ((long)check.ExecuteScalar()! > 0) continue;

            using var add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            add.ExecuteNonQuery();
            Log.Information("Added column {Table}.{Column} to the transcript database", table, column);
        }
    }

    public void StartSession(Guid id, string profile, DateTimeOffset startedUtc, MeetingKind kind = MeetingKind.Meeting) =>
        Enqueue(new OpenSession(id, profile, startedUtc, kind));

    public void RecordSegment(Guid sessionId, TranscriptSegment segment) =>
        Enqueue(new AddSegment(sessionId, segment));

    public void EndSession(Guid id, DateTimeOffset endedUtc) =>
        Enqueue(new CloseSession(id, endedUtc));

    /// <summary>
    /// Removes a meeting and its transcript for good. Queued behind the writes like any other, so
    /// it cannot interleave with a segment still being saved; await <see cref="FlushAsync"/> to
    /// know it is done, and compare <see cref="FailedWrites"/> to know it worked.
    /// </summary>
    public void DeleteSession(Guid id) =>
        Enqueue(new RemoveSession(id));

    /// <summary>
    /// Completes once every write queued before this call has been applied. Reads go straight to
    /// the file and do not see queued writes, so callers that need read-your-writes must await
    /// this first.
    /// </summary>
    public Task FlushAsync()
    {
        if (_queue is null) return Task.CompletedTask;

        var checkpoint = new Checkpoint(
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        return _queue.Writer.TryWrite(checkpoint) ? checkpoint.Signal.Task : Task.CompletedTask;
    }

    private void Enqueue(Op op)
    {
        // TryWrite on an unbounded channel only fails once the channel is completed, i.e. after
        // disposal. Dropping the write is correct then, but it must still be counted.
        if (_queue is not null && !_queue.Writer.TryWrite(op)) Interlocked.Increment(ref _failedWrites);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var op in _queue!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (op is Checkpoint checkpoint)
            {
                checkpoint.Signal.TrySetResult();
                continue;
            }

            try
            {
                Apply(op);
            }
            catch (Exception ex)
            {
                // One bad write must not kill the writer: the next segment may well succeed, and
                // a dead writer would silently stop saving the rest of the meeting.
                Interlocked.Increment(ref _failedWrites);
                Log.Warning(ex, "Could not persist {Op}", op.GetType().Name);
            }
        }
    }

    private void Apply(Op op)
    {
        if (op is RemoveSession(var removed))
        {
            Remove(removed);
            return;
        }

        using var command = _connection!.CreateCommand();

        switch (op)
        {
            case OpenSession(var id, var profile, var at, var kind):
                command.CommandText =
                    "INSERT OR REPLACE INTO sessions (id, profile, started_utc, kind) "
                    + "VALUES ($id, $profile, $started, $kind)";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$profile", profile);
                command.Parameters.AddWithValue("$started", Iso(at));
                command.Parameters.AddWithValue("$kind", kind.ToString());
                break;

            case AddSegment(var sessionId, var segment):
                command.CommandText =
                    "INSERT OR REPLACE INTO segments "
                    + "(id, session_id, channel, start_ms, end_ms, text, cut_reason) "
                    + "VALUES ($id, $session, $channel, $start, $end, $text, $reason)";
                command.Parameters.AddWithValue("$id", segment.Id.ToString("D"));
                command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
                command.Parameters.AddWithValue("$channel", segment.Channel.ToString());
                command.Parameters.AddWithValue("$start", (long)segment.Start.TotalMilliseconds);
                command.Parameters.AddWithValue("$end", (long)segment.End.TotalMilliseconds);
                command.Parameters.AddWithValue("$text", segment.Text);
                command.Parameters.AddWithValue("$reason", segment.CutReason.ToString());
                break;

            case CloseSession(var id, var at):
                command.CommandText = "UPDATE sessions SET ended_utc = $ended WHERE id = $id";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$ended", Iso(at));
                break;

            default:
                return;
        }

        command.ExecuteNonQuery();
    }

    /// <summary>The meeting and its lines go together or not at all.</summary>
    private void Remove(Guid id)
    {
        using var transaction = _connection!.BeginTransaction();
        foreach (var sql in new[]
                 {
                     "DELETE FROM segments WHERE session_id = $id",
                     "DELETE FROM sessions WHERE id = $id"
                 })
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Round-trip format, so a stored timestamp reads back as the value written.</summary>
    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("O");

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public IReadOnlyList<StoredSession> ReadSessions() => Query(
        "SELECT id, profile, started_utc, ended_utc FROM sessions ORDER BY started_utc",
        reader => new StoredSession(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            ParseIso(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseIso(reader.GetString(3))));

    /// <summary>
    /// Every meeting that transcribed at least one line, newest first. A session started and
    /// stopped without a word said is noise in a list of meetings, so it is left out.
    /// </summary>
    public IReadOnlyList<MeetingSummary> ReadMeetings() => Query(
        "SELECT s.id, s.profile, s.started_utc, s.ended_utc, COUNT(g.id), MAX(g.end_ms), s.kind "
        + "FROM sessions s JOIN segments g ON g.session_id = s.id "
        + "GROUP BY s.id ORDER BY s.started_utc DESC",
        reader => new MeetingSummary(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            ParseIso(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseIso(reader.GetString(3)),
            reader.GetInt32(4),
            TimeSpan.FromMilliseconds(reader.GetInt64(5)),
            Enum.TryParse<MeetingKind>(reader.IsDBNull(6) ? null : reader.GetString(6), out var kind) ? kind : null));

    public IReadOnlyList<TranscriptSegment> ReadSegments(Guid sessionId) => Query(
        "SELECT id, channel, start_ms, end_ms, text, cut_reason FROM segments "
        + "WHERE session_id = $session ORDER BY start_ms",
        reader => new TranscriptSegment(
            Guid.Parse(reader.GetString(0)),
            Enum.Parse<AudioChannelKind>(reader.GetString(1)),
            TimeSpan.FromMilliseconds(reader.GetInt64(2)),
            TimeSpan.FromMilliseconds(reader.GetInt64(3)),
            reader.GetString(4),
            Enum.Parse<SegmentCutReason>(reader.GetString(5))),
        ("$session", sessionId.ToString("D")));

    /// <summary>
    /// Reads run on their own connection. The writer owns its connection outright, so sharing it
    /// would mean a reader on the caller's thread racing a write on the writer's.
    /// </summary>
    private List<T> Query<T>(
        string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        var rows = new List<T>();
        if (!Available) return rows;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());

            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(map(reader));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read stored transcripts from {Path}", Path);
        }

        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _queue?.Writer.TryComplete();

        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Transcript writer faulted during shutdown");
        }

        _connection?.Dispose();
    }

    private abstract record Op;
    private sealed record OpenSession(Guid Id, string Profile, DateTimeOffset At, MeetingKind Kind) : Op;
    private sealed record AddSegment(Guid SessionId, TranscriptSegment Segment) : Op;
    private sealed record CloseSession(Guid Id, DateTimeOffset At) : Op;
    private sealed record RemoveSession(Guid Id) : Op;
    private sealed record Checkpoint(TaskCompletionSource Signal) : Op;
}
