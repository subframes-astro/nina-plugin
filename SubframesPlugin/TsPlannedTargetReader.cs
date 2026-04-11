using System.IO;
using Microsoft.Data.Sqlite;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Reads tonight's planned targets from the Target Scheduler SQLite database.
/// All access is best-effort: any error returns null so the session start is
/// never blocked by TS availability or schema changes.
/// </summary>
internal static class TsPlannedTargetReader
{
    private const int MaxTargets = 50;

    /// <summary>
    /// Attempts to read active planned targets from Target Scheduler.
    /// Returns null (silently) if TS is not installed or the DB is unreadable.
    /// </summary>
    public static List<PlannedTargetInput>? ReadPlannedTargets()
    {
        try
        {
            var dbPath = GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
                return null;

            var targets = QueryTargets(dbPath);
            Logger.Debug($"[Subframes] TS planned targets: found {targets.Count} target(s).");
            return targets.Count > 0 ? targets : null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] TS planned targets: read skipped ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private static string GetTsDbPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "NINA", "SchedulerPlugin", "schedulerdb.sqlite");
    }

    private static List<PlannedTargetInput> QueryTargets(string dbPath)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode        = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // Fetch target rows from active projects with incomplete exposure plans.
        // Each row is one (target, filter) combination; we group in-process.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                t.Name        AS targetName,
                p.Name        AS projectName,
                ep.FilterName AS filterName,
                ep.ExposureLength AS exposureSec
            FROM Target t
            JOIN Project     p  ON p.Id  = t.ProjectId
            LEFT JOIN ExposurePlan ep ON ep.TargetId = t.Id
            WHERE p.State  = 1
              AND t.Enabled = 1
              AND (ep.Desired IS NULL OR ep.Desired < 0 OR ep.Accepted < ep.Desired)
            ORDER BY p.Name, t.Name
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", MaxTargets * 20);

        // key = "projectName|targetName", value = accumulated data
        var grouped = new Dictionary<string, TargetAccumulator>(StringComparer.Ordinal);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (grouped.Count >= MaxTargets) break;

            var targetName  = reader.IsDBNull(0) ? null : reader.GetString(0);
            var projectName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var filterName  = reader.IsDBNull(2) ? null : reader.GetString(2);
            var expSec      = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3);

            if (string.IsNullOrWhiteSpace(targetName)) continue;

            var key = $"{projectName ?? ""}|{targetName}";
            if (!grouped.TryGetValue(key, out var acc))
            {
                acc = new TargetAccumulator(targetName, projectName);
                grouped[key] = acc;
            }

            if (!string.IsNullOrWhiteSpace(filterName))
                acc.AddFilter(filterName);
            if (expSec is > 0)
                acc.AddExpSec(expSec.Value);
        }

        return grouped.Values
            .Select(a => a.ToDto())
            .ToList();
    }

    private sealed class TargetAccumulator(string targetName, string? projectName)
    {
        private readonly List<string> _filters = [];
        private readonly List<double> _expSecs  = [];

        public void AddFilter(string filter)
        {
            if (!_filters.Contains(filter, StringComparer.OrdinalIgnoreCase))
                _filters.Add(filter);
        }

        public void AddExpSec(double sec) => _expSecs.Add(sec);

        public PlannedTargetInput ToDto() => new()
        {
            TargetName           = targetName,
            ProjectName          = string.IsNullOrEmpty(projectName) ? null : projectName,
            PlannedFilters       = _filters.Count > 0 ? [.. _filters] : null,
            EstimatedExposureSec = _expSecs.Count > 0 ? _expSecs.Average() : null,
        };
    }
}
