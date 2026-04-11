using System.IO;
using Microsoft.Data.Sqlite;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Reads per-frame grading results from the Target Scheduler SQLite database.
/// All access is best-effort: any error returns null so session end is never
/// blocked by TS availability or schema changes.
/// </summary>
internal static class TsGradingReader
{
    private const int MaxEntries = 2000;

    /// <summary>
    /// Reads grading results for frames acquired within the session time window.
    /// Returns null if TS is not installed, the DB is unreadable, or no rows match.
    /// </summary>
    /// <param name="sessionStart">UTC start of the session (used to scope the query).</param>
    /// <param name="sessionEnd">UTC end of the session.</param>
    public static List<TsGradingInput>? ReadGradingResults(DateTime sessionStart, DateTime sessionEnd)
    {
        try
        {
            var dbPath = GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
                return null;

            var entries = QueryGradingEntries(dbPath, sessionStart, sessionEnd);
            Logger.Debug($"[Subframes] TS grading: found {entries.Count} entry/entries in session window.");
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] TS grading: read skipped ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private static string GetTsDbPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "NINA", "SchedulerPlugin", "schedulerdb.sqlite");
    }

    private static List<TsGradingInput> QueryGradingEntries(
        string dbPath,
        DateTime sessionStart,
        DateTime sessionEnd)
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
            SELECT FilterName, AcquiredDate, GradingStatus, RejectReason
            FROM acquiredimage
            WHERE AcquiredDate >= @start
              AND AcquiredDate <= @end
            LIMIT @limit
            """;

        // Add a 60-second buffer on each side of the session window to account
        // for minor clock skew between the plugin's captured-at timestamp and
        // the timestamp stored by Target Scheduler.
        cmd.Parameters.AddWithValue("@start", ToSqliteString(sessionStart.AddSeconds(-60)));
        cmd.Parameters.AddWithValue("@end",   ToSqliteString(sessionEnd.AddSeconds(60)));
        cmd.Parameters.AddWithValue("@limit", MaxEntries);

        var entries = new List<TsGradingInput>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var filterName    = reader.IsDBNull(0) ? null : reader.GetString(0);
            var acquiredDate  = reader.IsDBNull(1) ? null : reader.GetString(1);
            var gradingStatus = reader.IsDBNull(2) ? -1   : reader.GetInt32(2);
            var rejectReason  = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (string.IsNullOrWhiteSpace(filterName) || string.IsNullOrWhiteSpace(acquiredDate))
                continue;

            var timestamp = ParseToUtcIso(acquiredDate);
            if (timestamp is null) continue;

            entries.Add(new TsGradingInput
            {
                FilterName    = filterName,
                Timestamp     = timestamp,
                GradingStatus = gradingStatus,
                RejectReason  = string.IsNullOrEmpty(rejectReason) ? null : rejectReason,
            });
        }

        return entries;
    }

    /// <summary>
    /// Converts a UTC DateTime to local time and formats it as "yyyy-MM-dd HH:mm:ss"
    /// for SQLite boundary comparisons. TS stores AcquiredDate in local time.
    /// </summary>
    private static string ToSqliteString(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Parses a SQLite date string (assumed local time) and returns an ISO 8601 UTC string.
    /// Supports "yyyy-MM-dd HH:mm:ss" and "yyyy-MM-ddTHH:mm:ss" variants.
    /// Returns null if parsing fails.
    /// </summary>
    private static string? ParseToUtcIso(string raw)
    {
        // Normalise the separator: SQLite DATETIME can use 'T' or ' '
        var normalised = raw.Replace('T', ' ');
        if (DateTime.TryParseExact(
                normalised,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
        {
            return DateTime.SpecifyKind(local, DateTimeKind.Local)
                           .ToUniversalTime()
                           .ToString("o");
        }

        // Fallback: let the runtime try anything parseable
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                              System.Globalization.DateTimeStyles.AssumeLocal,
                              out var fallback))
        {
            return fallback.ToUniversalTime().ToString("o");
        }

        return null;
    }
}
