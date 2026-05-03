using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin.Data;

/// <summary>
/// SQLite-backed offline cache for frame data.
/// All frames are written here first, then synced to the cloud by <see cref="SyncEngine"/>.
/// Uses WAL mode for concurrent read/write without blocking the imaging sequence.
/// </summary>
public sealed class FrameCache : IDisposable
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Subframes", "nina-plugin", "frame-cache.db");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly SqliteConnection _conn;

    public FrameCache()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cached_frames (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id   TEXT    NOT NULL,
                frame_json   TEXT    NOT NULL,
                status       TEXT    NOT NULL DEFAULT 'pending',
                created_at   TEXT    NOT NULL DEFAULT (datetime('now')),
                synced_at    TEXT,
                sync_attempts INTEGER NOT NULL DEFAULT 0,
                last_error   TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_cached_frames_status
            ON cached_frames (status, id);
            """;
        cmd.ExecuteNonQuery();

        // ── Session replay tables ────────────────────────────────────────────────
        //
        // cached_sessions tracks whether StartSession was acknowledged by the server.
        // Frames whose session_id matches a local_id with server_ack=0 are held back
        // by GetPendingFrames until CacheReplayEngine promotes them.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cached_sessions (
                local_id          TEXT PRIMARY KEY,
                server_id         TEXT,
                idempotency_key   TEXT NOT NULL,
                start_json        TEXT NOT NULL,
                server_ack        INTEGER NOT NULL DEFAULT 0,
                ended_locally     INTEGER NOT NULL DEFAULT 0,
                end_time          TEXT,
                skipped_exposures INTEGER,
                failed_exposures  INTEGER,
                status            TEXT NOT NULL DEFAULT 'pending',
                created_at        TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cached_targets (
                local_id          TEXT PRIMARY KEY,
                local_session_id  TEXT NOT NULL,
                server_id         TEXT,
                start_json        TEXT NOT NULL,
                server_ack        INTEGER NOT NULL DEFAULT 0,
                ended_locally     INTEGER NOT NULL DEFAULT 0,
                end_time          TEXT,
                created_at        TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cached_events (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                local_session_id TEXT NOT NULL,
                event_json       TEXT NOT NULL,
                status           TEXT NOT NULL DEFAULT 'pending',
                created_at       TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Insert a frame into the local cache as pending sync.
    /// Returns the row ID. This must never throw — errors are logged and swallowed.
    /// </summary>
    public long InsertFrame(string sessionId, FrameInput frame)
    {
        try
        {
            var json = JsonSerializer.Serialize(frame, JsonOptions);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cached_frames (session_id, frame_json)
                VALUES ($sessionId, $frameJson);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$frameJson", json);
            var id = (long)cmd.ExecuteScalar()!;
            Logger.Debug($"[Subframes] Frame cached: id={id} session={sessionId}");
            return id;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to cache frame: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Fetch up to <paramref name="limit"/> pending frames, oldest first.
    /// Excludes frames whose session_id is a local (unacked) session — those
    /// are held back until <see cref="CacheReplayEngine"/> promotes them.
    /// Returns tuples of (id, sessionId, frameJson).
    /// </summary>
    public List<(long Id, string SessionId, string FrameJson)> GetPendingFrames(int limit = 50)
    {
        var results = new List<(long, string, string)>();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT f.id, f.session_id, f.frame_json
                FROM cached_frames f
                WHERE f.status = 'pending'
                  AND f.session_id NOT IN (
                      SELECT local_id FROM cached_sessions WHERE server_ack = 0
                  )
                ORDER BY f.id ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to read pending frames: {ex.Message}");
        }
        return results;
    }

    /// <summary>Mark the given frame IDs as successfully synced.</summary>
    public void MarkSynced(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        try
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE cached_frames
                SET status = 'synced', synced_at = datetime('now')
                WHERE id = $id;
                """;
            var param = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids)
            {
                param.Value = id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to mark frames synced: {ex.Message}");
        }
    }

    /// <summary>Increment attempt count and record the error for failed frames.</summary>
    public void MarkFailed(IReadOnlyList<long> ids, string error)
    {
        if (ids.Count == 0) return;
        try
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE cached_frames
                SET sync_attempts = sync_attempts + 1,
                    last_error = $error
                WHERE id = $id;
                """;
            var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$error", error);
            foreach (var id in ids)
            {
                idParam.Value = id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to mark frames failed: {ex.Message}");
        }
    }

    /// <summary>Delete synced frames older than the retention period.</summary>
    public int PruneSynced(int retentionHours)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM cached_frames
                WHERE status = 'synced'
                  AND synced_at < datetime('now', $offset);
                """;
            cmd.Parameters.AddWithValue("$offset", $"-{retentionHours} hours");
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                Logger.Info($"[Subframes] Pruned {deleted} synced frames older than {retentionHours}h");
            return deleted;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to prune synced frames: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Get the count of pending (unsynced) frames.</summary>
    public int GetPendingCount()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cached_frames WHERE status = 'pending';";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to count pending frames: {ex.Message}");
            return 0;
        }
    }

    public void Dispose()
    {
        try { _conn.Dispose(); }
        catch (Exception ex) { Logger.Warning($"[Subframes] FrameCache dispose error: {ex.Message}"); }
    }

    // ── Session reconciliation ────────────────────────────────────────────────

    /// <summary>
    /// Record a new session in the local cache with <c>server_ack = false</c> before
    /// attempting the API call.  The session's <paramref name="localId"/> is used as
    /// the <c>session_id</c> for any frames cached while the server is unreachable.
    /// </summary>
    public void InsertSession(string localId, string idempotencyKey, string startJson)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO cached_sessions
                    (local_id, idempotency_key, start_json)
                VALUES ($localId, $ikey, $json);
                """;
            cmd.Parameters.AddWithValue("$localId", localId);
            cmd.Parameters.AddWithValue("$ikey",    idempotencyKey);
            cmd.Parameters.AddWithValue("$json",    startJson);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] InsertSession failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Mark a session as server-acknowledged and remap its cached frames to use
    /// the server-assigned ID so <see cref="SyncEngine"/> can pick them up.
    /// </summary>
    public void MarkSessionAcked(string localId, string serverId)
    {
        try
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;

            cmd.CommandText = """
                UPDATE cached_sessions
                SET server_id = $serverId, server_ack = 1
                WHERE local_id = $localId;
                """;
            cmd.Parameters.AddWithValue("$serverId", serverId);
            cmd.Parameters.AddWithValue("$localId",  localId);
            cmd.ExecuteNonQuery();

            // Remap frames: replace local placeholder ID with real server ID
            // so SyncEngine can upload them with the correct session reference.
            cmd.CommandText = """
                UPDATE cached_frames
                SET session_id = $serverId
                WHERE session_id = $localId AND status = 'pending';
                """;
            cmd.ExecuteNonQuery();

            tx.Commit();
            Logger.Debug($"[Subframes] Session acked: local={localId} server={serverId}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] MarkSessionAcked failed: {ex.Message}");
        }
    }

    /// <summary>Record that the session was ended locally (so replay can send EndSession).</summary>
    public void MarkSessionEnded(string localId, string endTime, int? skipped, int? failed)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE cached_sessions
                SET ended_locally = 1, end_time = $endTime,
                    skipped_exposures = $skipped, failed_exposures = $failed
                WHERE local_id = $localId;
                """;
            cmd.Parameters.AddWithValue("$localId",  localId);
            cmd.Parameters.AddWithValue("$endTime",  endTime);
            cmd.Parameters.AddWithValue("$skipped",  (object?)skipped ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$failed",   (object?)failed  ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] MarkSessionEnded failed: {ex.Message}");
        }
    }

    /// <summary>Mark a fully-replayed session as done so it is not re-attempted.</summary>
    public void MarkSessionReplayed(string localId)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE cached_sessions SET status = 'synced' WHERE local_id = $localId;
                """;
            cmd.Parameters.AddWithValue("$localId", localId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] MarkSessionReplayed failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns all sessions that need replay (i.e. <c>status = 'pending'</c>),
    /// ordered by <c>created_at ASC</c> (oldest first).
    /// </summary>
    public List<CachedSessionRecord> GetPendingReplaySessions()
    {
        var results = new List<CachedSessionRecord>();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT local_id, server_id, idempotency_key, start_json,
                       server_ack, ended_locally, end_time,
                       skipped_exposures, failed_exposures
                FROM cached_sessions
                WHERE status = 'pending'
                ORDER BY created_at ASC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new CachedSessionRecord(
                    LocalId:         reader.GetString(0),
                    ServerId:        reader.IsDBNull(1) ? null : reader.GetString(1),
                    IdempotencyKey:  reader.GetString(2),
                    StartJson:       reader.GetString(3),
                    ServerAck:       reader.GetInt32(4) == 1,
                    EndedLocally:    reader.GetInt32(5) == 1,
                    EndTime:         reader.IsDBNull(6) ? null : reader.GetString(6),
                    SkippedExposures:reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    FailedExposures: reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] GetPendingReplaySessions failed: {ex.Message}");
        }
        return results;
    }

    // ── Target reconciliation ──────────────────────────────────────────────

    /// <summary>Cache a session target with <c>server_ack = false</c>.</summary>
    public void InsertTarget(string localTargetId, string localSessionId, string startJson)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO cached_targets
                    (local_id, local_session_id, start_json)
                VALUES ($localId, $sessionId, $json);
                """;
            cmd.Parameters.AddWithValue("$localId",   localTargetId);
            cmd.Parameters.AddWithValue("$sessionId", localSessionId);
            cmd.Parameters.AddWithValue("$json",      startJson);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] InsertTarget failed: {ex.Message}");
        }
    }

    /// <summary>Mark a target as server-acked and store the server-assigned ID.</summary>
    public void MarkTargetAcked(string localTargetId, string serverId)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE cached_targets
                SET server_id = $serverId, server_ack = 1
                WHERE local_id = $localId;
                """;
            cmd.Parameters.AddWithValue("$serverId", serverId);
            cmd.Parameters.AddWithValue("$localId",  localTargetId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] MarkTargetAcked failed: {ex.Message}");
        }
    }

    /// <summary>Record that a target was ended locally.</summary>
    public void MarkTargetEnded(string localTargetId, string endTime)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE cached_targets
                SET ended_locally = 1, end_time = $endTime
                WHERE local_id = $localId;
                """;
            cmd.Parameters.AddWithValue("$localId", localTargetId);
            cmd.Parameters.AddWithValue("$endTime", endTime);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] MarkTargetEnded failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns all targets for a given local session, ordered by creation time.
    /// </summary>
    public List<CachedTargetRecord> GetTargetsForSession(string localSessionId)
    {
        var results = new List<CachedTargetRecord>();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT local_id, server_id, start_json, server_ack, ended_locally, end_time
                FROM cached_targets
                WHERE local_session_id = $sessionId
                ORDER BY created_at ASC;
                """;
            cmd.Parameters.AddWithValue("$sessionId", localSessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new CachedTargetRecord(
                    LocalId:      reader.GetString(0),
                    ServerId:     reader.IsDBNull(1) ? null : reader.GetString(1),
                    StartJson:    reader.GetString(2),
                    ServerAck:    reader.GetInt32(3) == 1,
                    EndedLocally: reader.GetInt32(4) == 1,
                    EndTime:      reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] GetTargetsForSession failed: {ex.Message}");
        }
        return results;
    }

    // ── Event caching (offline sessions only) ───────────────────────────

    /// <summary>
    /// Cache a session event for an offline session (server not yet acked).
    /// Called instead of firing the event live.
    /// </summary>
    public void InsertEvent(string localSessionId, string eventJson)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cached_events (local_session_id, event_json)
                VALUES ($sessionId, $json);
                """;
            cmd.Parameters.AddWithValue("$sessionId", localSessionId);
            cmd.Parameters.AddWithValue("$json",      eventJson);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] InsertEvent failed: {ex.Message}");
        }
    }

    /// <summary>Returns all pending events for a local session, oldest first.</summary>
    public List<(long Id, string EventJson)> GetPendingEventsForSession(string localSessionId)
    {
        var results = new List<(long, string)>();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, event_json
                FROM cached_events
                WHERE local_session_id = $sessionId AND status = 'pending'
                ORDER BY id ASC;
                """;
            cmd.Parameters.AddWithValue("$sessionId", localSessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] GetPendingEventsForSession failed: {ex.Message}");
        }
        return results;
    }

    /// <summary>Mark a batch of cached events as synced.</summary>
    public void MarkEventsSynced(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        try
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE cached_events SET status = 'synced' WHERE id = $id;
                """;
            var p = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var id in ids) { p.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] MarkEventsSynced failed: {ex.Message}");
        }
    }
}

// ── Value types for session / target replay ──────────────────────────────

/// <summary>A row from <c>cached_sessions</c> returned by <see cref="FrameCache.GetPendingReplaySessions"/>.</summary>
public sealed record CachedSessionRecord(
    string  LocalId,
    string? ServerId,
    string  IdempotencyKey,
    string  StartJson,
    bool    ServerAck,
    bool    EndedLocally,
    string? EndTime,
    int?    SkippedExposures,
    int?    FailedExposures);

/// <summary>A row from <c>cached_targets</c> returned by <see cref="FrameCache.GetTargetsForSession"/>.</summary>
public sealed record CachedTargetRecord(
    string  LocalId,
    string? ServerId,
    string  StartJson,
    bool    ServerAck,
    bool    EndedLocally,
    string? EndTime);
