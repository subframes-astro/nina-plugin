using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <param name="port">TS local HTTP API port (default 60555).</param>
    public TsPreviewClient(int port = 60555)
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

            return profiles
                .Select(p => new TsProfileInfo { Id = p.Id ?? string.Empty, Name = p.Name ?? string.Empty, Active = p.Active })
                .Where(p => !string.IsNullOrEmpty(p.Id))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] TsPreviewClient.FetchProfilesAsync error: {ex.GetType().Name}: {ex.Message}");
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

            var blocks = rawBlocks.Select(b => new TsPreviewBlockDto
            {
                TargetId = string.IsNullOrEmpty(b.Id) ? null : b.Id,
                TargetName = b.Name ?? string.Empty,
                WaitPeriod = b.WaitPeriod,
                StartTime = b.StartTime,
                EndTime = b.EndTime,
                ExposurePlans = b.ExposurePlan is { Count: > 0 }
                    ? b.ExposurePlan.Select(ep => new TsPreviewExposurePlanDto
                    {
                        FilterName = ep.FilterName ?? string.Empty,
                        Exposure = ep.Exposure,
                        Count = ep.Count,
                    }).ToList()
                    : null,
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
            Logger.Debug($"[Subframes] TsPreviewClient.FetchPreviewAsync error: {ex.GetType().Name}: {ex.Message}");
            return null;
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
internal sealed class TsProfileInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Active { get; init; }
}
