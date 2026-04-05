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
    /// Returns tuples of (id, sessionId, frameJson).
    /// </summary>
    public List<(long Id, string SessionId, string FrameJson)> GetPendingFrames(int limit = 50)
    {
        var results = new List<(long, string, string)>();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, frame_json
                FROM cached_frames
                WHERE status = 'pending'
                ORDER BY id ASC
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
}
