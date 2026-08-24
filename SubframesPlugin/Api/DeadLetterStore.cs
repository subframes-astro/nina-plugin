using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Subframes.NinaPlugin.Api;

/// <summary>
/// SQLite-backed dead-letter store for ingest payloads that have exhausted all retry attempts.
///
/// Provides durable storage so that no telemetry data is silently lost — failed
/// batches can be inspected and re-submitted manually or by a future recovery path.
///
/// Schema: a single <c>dead_letters</c> table with columns:
///   id          INTEGER PRIMARY KEY AUTOINCREMENT
///   endpoint    TEXT NOT NULL
///   payload     TEXT NOT NULL   (JSON-serialised batch)
///   failed_at   TEXT NOT NULL   (ISO-8601 UTC)
///   attempts    INTEGER NOT NULL
///
/// Auto-cleanup: rows beyond <see cref="MaxRows"/> are pruned oldest-first to cap disk use.
/// </summary>
public sealed class DeadLetterStore : IDisposable
{
    private const int MaxRows = 10_000;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

    private readonly SqliteConnection _db;

    // -------------------------------------------------------------------------
    // Construction / schema
    // -------------------------------------------------------------------------

    /// <param name="dbPath">
    /// Path to the SQLite file. Pass <c>":memory:"</c> for an in-memory store
    /// (useful in tests / when no writable path is available).
    /// </param>
    public DeadLetterStore(string dbPath)
    {
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS dead_letters (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                endpoint  TEXT    NOT NULL,
                payload   TEXT    NOT NULL,
                failed_at TEXT    NOT NULL,
                attempts  INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists a failed batch payload to the dead-letter store.
    /// Prunes the oldest rows when the store exceeds <see cref="MaxRows"/>.
    /// Never throws — failures are logged and swallowed so the caller is not affected.
    /// </summary>
    public void Write<T>(string endpoint, T payload, int attempts)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var failedAt = DateTimeOffset.UtcNow.ToString("O");

            using (var insert = _db.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO dead_letters (endpoint, payload, failed_at, attempts)
                    VALUES (@endpoint, @payload, @failed_at, @attempts);
                    """;
                insert.Parameters.AddWithValue("@endpoint", endpoint);
                insert.Parameters.AddWithValue("@payload", json);
                insert.Parameters.AddWithValue("@failed_at", failedAt);
                insert.Parameters.AddWithValue("@attempts", attempts);
                insert.ExecuteNonQuery();
            }

            Prune();

            SubframesLogger.Warning(
                $"Dead-lettered ingest payload — endpoint={endpoint} attempts={attempts} " +
                $"store={_db.DataSource}");
        }
        catch (Exception ex)
        {
            SubframesLogger.Error($"Failed to write to dead-letter store (endpoint={endpoint}): {ex.Message}");
        }
    }

    /// <summary>Returns the number of entries currently in the dead-letter store.</summary>
    public long Count()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dead_letters;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    private void Prune()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM dead_letters
            WHERE id IN (
                SELECT id FROM dead_letters
                ORDER BY id ASC
                LIMIT MAX(0, (SELECT COUNT(*) FROM dead_letters) - @maxRows)
            );
            """;
        cmd.Parameters.AddWithValue("@maxRows", MaxRows);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
