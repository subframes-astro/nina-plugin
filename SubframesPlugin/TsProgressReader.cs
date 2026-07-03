using System.IO;
using Microsoft.Data.Sqlite;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Reads all-time project/target progress from the Target Scheduler SQLite database.
/// Supports a full snapshot read (for the first station heartbeat) and an incremental
/// delta read (for subsequent heartbeats) based on SQLite file mtime change detection.
///
/// All access is best-effort: any error returns null so callers are never blocked
/// by TS availability or schema changes.
/// </summary>
internal static class TsProgressReader
{
    private const int MaxRows = 500;

    // ── Snapshot/delta state ─────────────────────────────────────────────────

    // Key: (projectName, targetName, filterName); Value: (desired, acquired, accepted)
    private static Dictionary<(string?, string, string), (int, int, int)>? _lastSnapshot;
    private static DateTime _lastMtime = DateTime.MinValue;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the full TS progress state and caches it for future delta calls.
    /// Returns null if TS is not installed, the DB is unreadable, or no rows exist.
    /// Call this on the first station heartbeat after plugin init or reconnection.
    /// </summary>
    public static TsProgressSnapshotDto? ReadProgressSnapshot()
    {
        try
        {
            var dbPath = TsHelper.GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
            {
                SubframesLogger.Info($"TS progress snapshot: DB not found at {dbPath ?? "(null)"} — Target Scheduler not installed or DB path mismatch.");
                return null;
            }

            var rows = QueryProgress(dbPath);
            if (rows.Count == 0)
                return null;

            var snapshot = ToSnapshotDict(rows);
            _lastSnapshot = snapshot;
            _lastMtime = SafeGetMtime(dbPath);

            SubframesLogger.Info($"TS progress snapshot: {rows.Count} row(s).");
            return new TsProgressSnapshotDto { Rows = ToRowDtos(rows) };
        }
        catch (Exception ex)
        {
            SubframesLogger.Warning($"TS progress snapshot: read skipped ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Checks whether the TS database has changed since the last read (via file mtime).
    /// If unchanged, returns null immediately (no DB read, zero overhead).
    /// If changed, re-reads progress, diffs against the previous snapshot, and returns
    /// the delta (upserts + removals). Returns null if TS is not installed or unreadable.
    /// </summary>
    public static TsProgressDeltaDto? ReadProgressDelta()
    {
        try
        {
            var dbPath = TsHelper.GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
            {
                SubframesLogger.Info($"TS progress delta: DB not found at {dbPath ?? "(null)"} — Target Scheduler not installed or DB path mismatch.");
                return null;
            }

            var currentMtime = SafeGetMtime(dbPath);
            if (currentMtime == DateTime.MinValue)
                return null; // mtime unavailable — skip silently

            if (currentMtime == _lastMtime)
                return null; // no change

            // File has changed — re-read and diff.
            var rows = QueryProgress(dbPath);
            var currentSnapshot = ToSnapshotDict(rows);

            var delta = ComputeDelta(_lastSnapshot, currentSnapshot);

            _lastSnapshot = currentSnapshot;
            _lastMtime = currentMtime;

            if (delta.Upserts.Count == 0 && delta.Removals.Count == 0)
            {
                SubframesLogger.Info("TS progress: mtime changed but no row differences found.");
                return null;
            }

            SubframesLogger.Info($"TS progress delta: {delta.Upserts.Count} upsert(s), {delta.Removals.Count} removal(s).");
            return delta;
        }
        catch (Exception ex)
        {
            SubframesLogger.Warning($"TS progress delta: read skipped ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Resets the mtime and snapshot cache. Call this when the plugin restarts or
    /// reconnects so the next heartbeat sends a full snapshot.
    /// </summary>
    public static void ResetCache()
    {
        _lastSnapshot = null;
        _lastMtime = DateTime.MinValue;
    }

    /// <summary>
    /// Attempts to read all-time progress entries from Target Scheduler.
    /// Returns null (silently) if TS is not installed or the DB is unreadable.
    /// Used at session end — does not update the heartbeat cache.
    /// </summary>
    public static List<TsProgressInput>? ReadProgress()
    {
        try
        {
            var dbPath = TsHelper.GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
            {
                SubframesLogger.Info($"Target Scheduler not detected (no database at {dbPath})");
                return null;
            }

            var entries = QueryProgress(dbPath);
            SubframesLogger.Info($"TS progress: found {entries.Count} row(s).");
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception ex)
        {
            SubframesLogger.Warning($"TS progress: read skipped ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static DateTime SafeGetMtime(string dbPath)
    {
        try { return File.GetLastWriteTimeUtc(dbPath); }
        catch { return DateTime.MinValue; }
    }

    private static Dictionary<(string?, string, string), (int, int, int)> ToSnapshotDict(List<TsProgressInput> rows)
    {
        var dict = new Dictionary<(string?, string, string), (int, int, int)>(rows.Count);
        foreach (var r in rows)
            dict[(r.ProjectName, r.TargetName, r.FilterName)] = (r.Desired, r.Acquired, r.Accepted);
        return dict;
    }

    private static List<TsProgressRowDto> ToRowDtos(List<TsProgressInput> rows)
    {
        var dtos = new List<TsProgressRowDto>(rows.Count);
        foreach (var r in rows)
            dtos.Add(new TsProgressRowDto
            {
                ProjectName = r.ProjectName,
                TargetName  = r.TargetName,
                FilterName  = r.FilterName,
                Desired     = r.Desired,
                Acquired    = r.Acquired,
                Accepted    = r.Accepted,
            });
        return dtos;
    }

    private static TsProgressDeltaDto ComputeDelta(
        Dictionary<(string?, string, string), (int, int, int)>? previous,
        Dictionary<(string?, string, string), (int, int, int)> current)
    {
        var upserts  = new List<TsProgressRowDto>();
        var removals = new List<TsProgressRemovalKeyDto>();

        // Upserts: new or changed rows.
        foreach (var (key, vals) in current)
        {
            if (previous is null || !previous.TryGetValue(key, out var prevVals) || prevVals != vals)
            {
                upserts.Add(new TsProgressRowDto
                {
                    ProjectName = key.Item1,
                    TargetName  = key.Item2,
                    FilterName  = key.Item3,
                    Desired     = vals.Item1,
                    Acquired    = vals.Item2,
                    Accepted    = vals.Item3,
                });
            }
        }

        // Removals: rows present in previous but missing in current.
        if (previous is not null)
        {
            foreach (var key in previous.Keys)
            {
                if (!current.ContainsKey(key))
                {
                    removals.Add(new TsProgressRemovalKeyDto
                    {
                        ProjectName = key.Item1,
                        TargetName  = key.Item2,
                        FilterName  = key.Item3,
                    });
                }
            }
        }

        return new TsProgressDeltaDto { Upserts = upserts, Removals = removals };
    }


    private static List<TsProgressInput> QueryProgress(string dbPath)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode        = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                p.Name            AS projectName,
                t.Name            AS targetName,
                et.filtername     AS filterName,
                COALESCE(ep.Desired,  0) AS desired,
                COALESCE(ep.Accepted, 0) AS accepted,
                COALESCE(ep.Acquired, 0) AS acquired
            FROM ExposurePlan ep
            JOIN Target            t  ON t.Id  = ep.targetid
            JOIN Project           p  ON p.Id  = t.projectid
            JOIN exposuretemplate  et ON et.Id  = ep.exposureTemplateId
            WHERE p.State   = 1
              AND t.active  = 1
            ORDER BY p.Name, t.Name, et.filtername
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", MaxRows);

        var entries = new List<TsProgressInput>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var projectName = reader.IsDBNull(0) ? null : reader.GetString(0);
            var targetName  = reader.IsDBNull(1) ? null : reader.GetString(1);
            var filterName  = reader.IsDBNull(2) ? null : reader.GetString(2);
            var desired     = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var accepted    = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var acquired    = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);

            if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(filterName))
                continue;

            entries.Add(new TsProgressInput
            {
                ProjectName = string.IsNullOrWhiteSpace(projectName) ? null : projectName,
                TargetName  = targetName,
                FilterName  = filterName,
                Desired     = desired,
                Accepted    = accepted,
                Acquired    = acquired,
            });
        }

        return entries;
    }
}
