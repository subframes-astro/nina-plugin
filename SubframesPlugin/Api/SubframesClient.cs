using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Core.Utility;

namespace Subframes.NinaPlugin.Api;

/// <summary>
/// HTTP client for the Subframes ingest API.
///
/// All methods return null on failure and swallow exceptions — the caller must
/// treat a null result as "API unreachable, data not recorded" without
/// propagating an exception to the NINA sequence engine.
///
/// Authentication uses a Bearer token (API key with prefix astk_live_).
/// </summary>
public sealed class SubframesClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly HttpClient _http;
    private readonly PluginOptions _options;

    public SubframesClient(PluginOptions options)
    {
        _options = options;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private string BaseUrl => _options.ApiBaseUrl.TrimEnd('/');

    private void SetAuthHeader()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    // ── Session Start ────────────────────────────────────────────────────────

    /// <summary>
    /// Start a new imaging session.
    /// Returns the server-assigned session ID, or null if the call failed.
    /// </summary>
    public async Task<string?> StartSessionAsync(
        StartSessionRequest request,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return null;

        try
        {
            SetAuthHeader();
            var url = $"{BaseUrl}/api/v1/ingest/session/start";
            if (_options.IsDebugEnabled)
                Logger.Debug($"[Subframes] POST {url} body={JsonSerializer.Serialize(request, JsonOptions)}");
            using var response = await _http.PostAsJsonAsync(url, request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var envelope = await response.Content
                .ReadFromJsonAsync<ApiEnvelope<StartSessionData>>(JsonOptions, ct);
            var sessionId = envelope?.Data?.SessionId;
            Logger.Info($"[Subframes] Session started: {sessionId} for target '{request.TargetName}'");
            return sessionId;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] StartSession failed: {ex.Message}");
            return null;
        }
    }

    // ── Session End ──────────────────────────────────────────────────────────

    /// <summary>
    /// End an active imaging session.
    /// </summary>
    public async Task EndSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        try
        {
            SetAuthHeader();
            var url = $"{BaseUrl}/api/v1/ingest/session/end";
            var body = new EndSessionRequest
            {
                SessionId = sessionId,
                EndTime = DateTime.UtcNow.ToString("o")
            };
            if (_options.IsDebugEnabled)
                Logger.Debug($"[Subframes] POST {url} body={JsonSerializer.Serialize(body, JsonOptions)}");
            using var response = await _http.PostAsJsonAsync(url, body, JsonOptions, ct);
            response.EnsureSuccessStatusCode();
            Logger.Info($"[Subframes] Session ended: {sessionId}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] EndSession failed (session={sessionId}): {ex.Message}");
        }
    }

    // ── Heartbeat ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget session heartbeat. Uses a dedicated 5-second timeout
    /// so a slow server never blocks the caller.
    /// </summary>
    public async Task SendHeartbeatAsync(
        HeartbeatRequest request,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            SetAuthHeader();
            var url = $"{BaseUrl}/api/v1/ingest/heartbeat";
            if (_options.IsDebugEnabled)
                Logger.Debug($"[Subframes] POST {url} body={JsonSerializer.Serialize(request, JsonOptions)}");
            using var response = await _http.PostAsJsonAsync(url, request, JsonOptions, cts.Token);
            response.EnsureSuccessStatusCode();
            Logger.Debug($"[Subframes] Heartbeat sent for session {request.SessionId}");
        }
        catch (OperationCanceledException)
        {
            Logger.Warning($"[Subframes] Heartbeat timed out (session={request.SessionId})");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Heartbeat failed (session={request.SessionId}): {ex.Message}");
        }
    }

    // ── Station Heartbeat ────────────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget station-level heartbeat. Independent of any imaging session.
    /// Uses a dedicated 5-second timeout so a slow server never blocks the caller.
    /// </summary>
    public async Task SendStationHeartbeatAsync(
        StationHeartbeatRequest request,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            SetAuthHeader();
            var url = $"{BaseUrl}/api/v1/ingest/station/heartbeat";
            if (_options.IsDebugEnabled)
                Logger.Debug($"[Subframes] POST {url} body={JsonSerializer.Serialize(request, JsonOptions)}");
            using var response = await _http.PostAsJsonAsync(url, request, JsonOptions, cts.Token);
            response.EnsureSuccessStatusCode();
            Logger.Debug($"[Subframes] Station heartbeat sent (status={request.Status})");
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("[Subframes] Station heartbeat timed out");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Station heartbeat failed: {ex.Message}");
        }
    }

    // ── Frame Ingest ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ingest a batch of frames for an active session.
    /// Returns accepted count, or null on failure.
    /// </summary>
    public async Task<IngestFramesData?> IngestFramesAsync(
        string sessionId,
        List<FrameInput> frames,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return null;

        try
        {
            SetAuthHeader();
            var url = $"{BaseUrl}/api/v1/ingest/frame";
            var body = new IngestFramesRequest
            {
                SessionId = sessionId,
                Frames = frames
            };
            if (_options.IsDebugEnabled)
                Logger.Debug($"[Subframes] POST {url} body={JsonSerializer.Serialize(body, JsonOptions)}");
            using var response = await _http.PostAsJsonAsync(url, body, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var envelope = await response.Content
                .ReadFromJsonAsync<ApiEnvelope<IngestFramesData>>(JsonOptions, ct);
            var data = envelope?.Data;
            Logger.Debug($"[Subframes] Frames ingested: accepted={data?.Accepted} rejected={data?.Rejected}");
            return data;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] IngestFrames failed (session={sessionId}): {ex.Message}");
            return null;
        }
    }

    // ── Health Check ───────────────────────────────────────────────────────

    /// <summary>
    /// Check API connectivity by hitting GET /healthz.
    /// Uses the provided URL and API key so the user can test before saving.
    /// Returns (true, null) on success, or (false, detail) with a diagnostic message on failure.
    /// </summary>
    public static async Task<(bool Connected, string? Detail)> CheckHealthAsync(
        string baseUrl,
        string apiKey,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/healthz";
            using var response = await http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
                return (true, null);

            return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Request timed out after 5 s");
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── API Key Validation ─────────────────────────────────────────────────

    /// <summary>
    /// Validate an API key against the backend.
    /// Uses a dedicated 5-second timeout. Returns (true, null) on 200 OK,
    /// (false, "Invalid API key") on 401, or (false, detail) on other failures.
    /// </summary>
    public static async Task<(bool Valid, string? Detail)> ValidateApiKeyAsync(
        string baseUrl,
        string apiKey,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/api/v1/auth/validate-key";
            using var response = await http.GetAsync(url, ct);

            if (response.IsSuccessStatusCode)
                return (true, null);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (false, "Invalid API key");

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                return (true, "Endpoint not available");

            return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Request timed out after 5 s");
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
