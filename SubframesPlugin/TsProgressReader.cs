using System.IO;
using Microsoft.Data.Sqlite;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Reads all-time project/target progress from the Target Scheduler SQLite database
/// at session end. Returns one row per (project, target, filter) with desired,
/// acquired, and accepted counts.
///
/// All access is best-effort: any error returns null so session end is never
/// blocked by TS availability or schema changes.
/// </summary>
internal static class TsProgressReader
{
    private const int MaxRows = 500;

    /// <summary>
    /// Attempts to read all-time progress entries from Target Scheduler.
    /// Returns null (silently) if TS is not installed or the DB is unreadable.
    /// </summary>
    public static List<TsProgressInput>? ReadProgress()
    {
        try
        {
            var dbPath = TsHelper.GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
            {
                Logger.Info($"[Subframes] Target Scheduler not detected (no database at {dbPath})");
                return null;
            }

            var entries = QueryProgress(dbPath);
            Logger.Info($"[Subframes] TS progress: found {entries.Count} row(s).");
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] TS progress: read skipped ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
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
                p.Name           AS projectName,
                t.Name           AS targetName,
                ep.FilterName    AS filterName,
                COALESCE(ep.Desired,  0) AS desired,
                COALESCE(ep.Accepted, 0) AS accepted,
                COALESCE((
                    SELECT COUNT(*)
                    FROM acquiredimage ai
                    WHERE ai.ExposurePlanId = ep.Id
                ), 0) AS acquired
            FROM ExposurePlan ep
            JOIN Target  t ON t.Id = ep.TargetId
            JOIN Project p ON p.Id = t.ProjectId
            WHERE p.State   = 1
              AND t.Enabled = 1
            ORDER BY p.Name, t.Name, ep.FilterName
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
