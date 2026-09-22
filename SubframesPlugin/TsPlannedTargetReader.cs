using System.IO;
using Microsoft.Data.Sqlite;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Reads tonight's planned targets from the Target Scheduler SQLite database.
/// All access is best-effort: this never throws and never blocks session start,
/// but unlike a plain nullable return, callers get a typed status so a locked or
/// schema-changed TS DB is distinguishable from "no TS data" downstream.
/// </summary>
internal static class TsPlannedTargetReader
{
    private const int MaxTargets = 50;

    /// <summary>
    /// Attempts to read active planned targets from Target Scheduler.
    /// Never throws. Returns <see cref="TsReadStatus.NotInstalled"/> when TS's DB
    /// file is absent, <see cref="TsReadStatus.Error"/> when the DB exists but
    /// could not be read (locked/corrupt/schema change), otherwise <see cref="TsReadStatus.Ok"/>.
    /// </summary>
    public static TsReadResult<List<PlannedTargetInput>> ReadPlannedTargetsResult()
    {
        try
        {
            var dbPath = TsHelper.GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
            {
                SubframesLogger.Info($"Target Scheduler not detected (no database at {dbPath})");
                return TsReadResult<List<PlannedTargetInput>>.NotInstalled();
            }

            SubframesLogger.Info($"Target Scheduler database found at {dbPath}");
            var targets = QueryTargets(dbPath);
            SubframesLogger.Info($"TS planned targets: found {targets.Count} target(s).");
            return TsReadResult<List<PlannedTargetInput>>.Ok(targets.Count > 0 ? targets : null);
        }
        catch (Exception ex)
        {
            SubframesLogger.Warning($"TS planned targets: read failed ({ex.GetType().Name}: {ex.Message})");
            return TsReadResult<List<PlannedTargetInput>>.Error(ex);
        }
    }

    /// <summary>
    /// Convenience wrapper for callers that only need the data and don't (yet)
    /// propagate read-failure status. Prefer <see cref="ReadPlannedTargetsResult"/>
    /// for anything reported to the backend.
    /// </summary>
    public static List<PlannedTargetInput>? ReadPlannedTargets() => ReadPlannedTargetsResult().Data;

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
                t.Name          AS targetName,
                p.Name          AS projectName,
                et.filtername   AS filterName,
                ep.exposure     AS exposureSec,
                t.rotation      AS rotation,
                t.Id            AS targetId,
                p.Id            AS projectId
            FROM Target t
            JOIN Project          p  ON p.Id  = t.projectid
            LEFT JOIN ExposurePlan ep ON ep.targetid = t.Id
            LEFT JOIN exposuretemplate et ON et.Id = ep.exposureTemplateId
            WHERE p.State  = 1
              AND t.active = 1
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
            var rotation    = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4);
            var targetId    = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5);
            var projectId   = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);

            if (string.IsNullOrWhiteSpace(targetName)) continue;

            var key = $"{projectName ?? ""}|{targetName}";
            if (!grouped.TryGetValue(key, out var acc))
            {
                acc = new TargetAccumulator(targetName, projectName, rotation, targetId, projectId);
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

    private sealed class TargetAccumulator(string targetName, string? projectName, double? rotation, long? targetId, long? projectId)
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
            CameraRotation       = rotation,
            TsTargetId           = targetId,
            TsProjectId          = projectId,
        };
    }
}
