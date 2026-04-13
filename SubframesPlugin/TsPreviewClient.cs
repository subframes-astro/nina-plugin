using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Fetches Target Scheduler profile list and tonight's scheduling preview via the TS local HTTP API.
///
/// Endpoints used:
///   GET /ts/v0/profiles          → list of available profiles
///   GET /ts/v0/profiles/{id}/preview → tonight's scheduling blocks for one profile
/// </summary>
internal sealed class TsPreviewClient
{
    // Shared HttpClient with a 5-second timeout — preview calls are low-priority
    // and should not block the imaging loop.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // TS API returns PascalCase field names; accept any casing to be forward-compatible.
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly int _port;

    /// <param name="port">TS local HTTP API port (default 8188).</param>
    public TsPreviewClient(int port = 8188)
    {
        _port = port;
    }

    /// <summary>
    /// Fetches the list of available TS profiles.
    /// Returns an empty list on any error — never throws.
    /// </summary>
    public async Task<List<TsProfileInfo>> FetchProfilesAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"http://localhost:{_port}/ts/v0/profiles";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var profiles = JsonSerializer.Deserialize<List<TsProfileInfoRaw>>(json, _jsonOptions);
            if (profiles is null) return [];

            var result = profiles
                .Select(p => new TsProfileInfo { Id = p.Id ?? string.Empty, Name = p.Name ?? string.Empty, Active = p.Active })
                .Where(p => !string.IsNullOrEmpty(p.Id))
                .ToList();

            if (result.Count == 0)
                Logger.Warning($"[Subframes] TsPreviewClient.FetchProfilesAsync: TS returned an empty profiles list (port={_port}).");

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] TsPreviewClient.FetchProfilesAsync failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetches tonight's scheduling preview for the given profile.
    /// Returns null on any error — never throws.
    /// </summary>
    public async Task<TsPreviewDto?> FetchPreviewAsync(string profileId, string profileName, CancellationToken ct = default)
    {
        try
        {
            var url = $"http://localhost:{_port}/ts/v0/profiles/{profileId}/preview";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var rawBlocks = JsonSerializer.Deserialize<List<TsPreviewBlockRaw>>(json, _jsonOptions);
            if (rawBlocks is null) return null;

            // Collect distinct target names from non-wait-period blocks for coordinate enrichment.
            // We match by name (not ID) because the TS preview HTTP API returns runtime GUIDs
            // that do not correspond to the integer PKs in the TS SQLite Target table.
            var targetNames = rawBlocks
                .Where(b => !b.WaitPeriod && !string.IsNullOrEmpty(b.Name))
                .Select(b => b.Name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Look up RA/Dec/Rotation from the TS SQLite database keyed by target name.
            var coordsByName = targetNames.Count > 0 ? LookupCoordinatesByName(targetNames) : [];

            // Filter out any blocks missing StartTime or EndTime — the backend requires both fields.
            // Well-behaved TS responses always include times for every block (including wait periods),
            // so this guard is purely defensive against malformed data.
            var blocks = rawBlocks
                .Where(b => b.StartTime is not null && b.EndTime is not null)
                .Select(b =>
                {
                    // Prefer RA/Dec from the HTTP API response if present (future-proofing),
                    // otherwise fall back to the coordinate lookup from the TS SQLite database.
                    double? ra = null, dec = null, rotation = null;
                    if (!b.WaitPeriod && !string.IsNullOrEmpty(b.Name)
                        && coordsByName.TryGetValue(b.Name, out var c))
                    {
                        ra = c.Ra;
                        dec = c.Dec;
                        rotation = c.Rotation;
                    }

                    return new TsPreviewBlockDto
                    {
                        TargetId = string.IsNullOrEmpty(b.Id) ? null : b.Id,
                        TargetName = b.Name ?? string.Empty,
                        WaitPeriod = b.WaitPeriod,
                        StartTime = b.StartTime!,
                        EndTime = b.EndTime!,
                        Ra = b.WaitPeriod ? null : (b.Ra ?? ra),
                        Dec = b.WaitPeriod ? null : (b.Dec ?? dec),
                        AngularSizeDeg = b.WaitPeriod ? null : b.AngularSizeDeg,
                        Rotation = b.WaitPeriod ? null : rotation,
                        ExposurePlans = b.ExposurePlan is { Count: > 0 }
                            ? b.ExposurePlan.Select(ep => new TsPreviewExposurePlanDto
                            {
                                FilterName = ep.FilterName ?? string.Empty,
                                Exposure = ep.Exposure,
                                Count = ep.Count,
                            }).ToList()
                            : null,
                    };
                }).ToList();

            return new TsPreviewDto
            {
                ProfileId = profileId,
                ProfileName = profileName,
                Blocks = blocks,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] TsPreviewClient.FetchPreviewAsync failed for profile '{profileName}' ({profileId}): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Looks up RA (converted from hours to degrees), Dec (degrees), and Rotation (degrees) for a
    /// set of target names from the TS SQLite database. Matches by name because the TS preview HTTP
    /// API returns runtime GUIDs that do not correspond to the integer PKs in the Target table.
    /// Returns an empty dictionary if the database is not found or any error occurs — never throws.
    /// </summary>
    private static Dictionary<string, (double Ra, double Dec, double? Rotation)> LookupCoordinatesByName(IReadOnlyList<string> targetNames)
    {
        try
        {
            var dbPath = TsHelper.GetTsDbPath();
            if (!File.Exists(dbPath))
            {
                Logger.Info($"[Subframes] TsPreviewClient.LookupCoordinatesByName: TS database not found at {dbPath}");
                return [];
            }

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode       = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            // Build parameterized IN clause to avoid SQL injection.
            var paramNames = targetNames.Select((_, i) => $"@p{i}").ToList();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Name, ra, dec, rotation FROM target WHERE Name IN ({string.Join(", ", paramNames)})";

            for (var i = 0; i < targetNames.Count; i++)
                cmd.Parameters.AddWithValue(paramNames[i], targetNames[i]);

            var result = new Dictionary<string, (double Ra, double Dec, double? Rotation)>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
                var name     = reader.GetString(0);
                var raHours  = reader.GetDouble(1);
                var decDeg   = reader.GetDouble(2);
                var rotation = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3);
                // TS stores RA in hours (0–24); convert to degrees for the API/frontend.
                result[name] = (raHours * 15.0, decDeg, rotation);
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] TsPreviewClient.LookupCoordinatesByName failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    // ── Raw TS API response models (PascalCase as returned by the TS HTTP API) ──

    private sealed class TsProfileInfoRaw
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public bool Active { get; init; }
    }

    private sealed class TsPreviewBlockRaw
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public bool WaitPeriod { get; init; }
        public string? StartTime { get; init; }
        public string? EndTime { get; init; }
        public double? Ra { get; init; }
        public double? Dec { get; init; }
        public double? AngularSizeDeg { get; init; }
        public List<TsPreviewExposurePlanRaw>? ExposurePlan { get; init; }
    }

    private sealed class TsPreviewExposurePlanRaw
    {
        public string? FilterName { get; init; }
        public double Exposure { get; init; }
        public int Count { get; init; }
    }
}

/// <summary>A Target Scheduler profile as returned by GET /ts/v0/profiles.</summary>
public sealed class TsProfileInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Active { get; init; }
}
