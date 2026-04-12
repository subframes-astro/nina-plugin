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
    private const int MaxEntries = 500;

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
            var dbPath = TsHelper.GetTsDbPath();
            if (dbPath is null || !File.Exists(dbPath))
            {
                Logger.Info($"[Subframes] Target Scheduler not detected (no database at {dbPath})");
                return null;
            }

            var entries = QueryGradingEntries(dbPath, sessionStart, sessionEnd);
            Logger.Info($"[Subframes] TS grading: found {entries.Count} entry/entries in session window.");
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] TS grading: read skipped ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
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

        // TS stores acquireddate as an INTEGER. Detect the format (ticks vs epoch)
        // by sampling the first row so we can build correct range parameters.
        var format = DetectDateFormat(conn);
        if (format == DateFormat.Unknown)
        {
            Logger.Info("[Subframes] TS grading: no rows in acquiredimage — nothing to query.");
            return [];
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT filtername, acquireddate, gradingStatus, rejectreason
            FROM acquiredimage
            WHERE acquireddate >= @start
              AND acquireddate <= @end
            LIMIT @limit
            """;

        // Add a 60-second buffer on each side of the session window to account
        // for minor clock skew between the plugin's captured-at timestamp and
        // the timestamp stored by Target Scheduler.
        var start = sessionStart.AddSeconds(-60);
        var end   = sessionEnd.AddSeconds(60);
        cmd.Parameters.AddWithValue("@start", ToInteger(start, format));
        cmd.Parameters.AddWithValue("@end",   ToInteger(end, format));
        cmd.Parameters.AddWithValue("@limit", MaxEntries);

        var entries = new List<TsGradingInput>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var filterName    = reader.IsDBNull(0) ? null : reader.GetString(0);
            var rawDate       = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var gradingStatus = reader.IsDBNull(2) ? -1   : reader.GetInt32(2);
            var rejectReason  = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (string.IsNullOrWhiteSpace(filterName) || rawDate is null)
                continue;

            var timestamp = IntegerToUtcIso(rawDate.Value, format);
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

    // ── Date format detection & conversion ──────────────────────────────────

    private enum DateFormat { Unknown, Ticks, UnixSeconds }

    /// <summary>
    /// Samples the first acquireddate value to determine whether TS stores dates
    /// as .NET ticks (~10^17 for 2020s dates) or Unix epoch seconds (~10^9).
    /// </summary>
    private static DateFormat DetectDateFormat(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT acquireddate FROM acquiredimage LIMIT 1";
        var sample = probe.ExecuteScalar();
        if (sample is null or DBNull)
            return DateFormat.Unknown;

        var value = Convert.ToInt64(sample);
        Logger.Info($"[Subframes] TS grading: acquireddate sample value = {value}");

        // .NET ticks for year 2000 ≈ 630,822,816,000,000,000 (10^17 order)
        // Unix epoch seconds for year 2000 ≈ 946,684,800 (10^9 order)
        return value > 1_000_000_000_000L ? DateFormat.Ticks : DateFormat.UnixSeconds;
    }

    /// <summary>
    /// Converts a UTC DateTime to the integer format used by TS in acquireddate.
    /// </summary>
    private static long ToInteger(DateTime utc, DateFormat format) => format switch
    {
        DateFormat.Ticks        => utc.ToLocalTime().Ticks,
        DateFormat.UnixSeconds  => new DateTimeOffset(utc).ToUnixTimeSeconds(),
        _                       => 0,
    };

    /// <summary>
    /// Converts an integer acquireddate value to an ISO 8601 UTC string.
    /// </summary>
    private static string? IntegerToUtcIso(long value, DateFormat format)
    {
        try
        {
            return format switch
            {
                DateFormat.Ticks => new DateTime(value, DateTimeKind.Local)
                                        .ToUniversalTime()
                                        .ToString("o"),
                DateFormat.UnixSeconds => DateTimeOffset.FromUnixTimeSeconds(value)
                                             .UtcDateTime
                                             .ToString("o"),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
