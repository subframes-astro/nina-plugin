using System.IO.Compression;
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
/// Request bodies are gzip-compressed. Data-bearing calls (session start/end,
/// frame ingest) are retried up to 3 times with 1 s / 2 s / 4 s exponential
/// backoff on 5xx and network errors. 4xx errors (except 429) are not retried.
/// Heartbeats are fire-and-forget and are not retried.
/// </summary>
public sealed class SubframesClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    // Delays in ms between retry attempts: attempt 1→2, 2→3, 3→4
    private static readonly int[] RetryDelaysMs = [1000, 2000, 4000];

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
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            if (_options.IsDebugEnabled)
            {
                var preview = _options.ApiKey.Length > 12
                    ? _options.ApiKey[..12] + "..."
                    : "(short key)";
                Logger.Info($"[Subframes] Auth header set: Bearer {preview}");
            }
        }
        else if (_options.IsDebugEnabled)
        {
            Logger.Info("[Subframes] No API key configured — request will be unauthenticated");
        }
    }

    // ── Retry + Gzip helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="body"/> to JSON bytes for use with
    /// <see cref="PostWithRetryAsync"/>.
    /// </summary>
    private static byte[] SerializeJson<T>(T body) =>
        JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);

    /// <summary>
    /// Wraps JSON bytes in a gzip-compressed <see cref="HttpContent"/> with
    /// the appropriate Content-Type and Content-Encoding headers.
    /// </summary>
    private static HttpContent CreateGzipContent(byte[] jsonBytes)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            gz.Write(jsonBytes, 0, jsonBytes.Length);
        ms.Position = 0;

        var content = new StreamContent(ms);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    /// <summary>
    /// POST <paramref name="jsonBytes"/> to <paramref name="url"/> with
    /// exponential backoff retry (up to 4 total attempts: delays 1 s, 2 s, 4 s).
    ///
    /// Retry policy:
    ///  - 5xx responses and network errors → retry with backoff
    ///  - 429 Too Many Requests → honour Retry-After header (capped at 30 s)
    ///  - 4xx (except 429) → return immediately without retry
    ///
    /// The caller is responsible for disposing the returned response.
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/>
    /// is cancelled.
    /// </summary>
    private async Task<HttpResponseMessage> PostWithRetryAsync(
        string url,
        byte[] jsonBytes,
        CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        int totalAttempts = RetryDelaysMs.Length + 1; // 4

        for (int attempt = 0; attempt < totalAttempts; attempt++)
        {
            response?.Dispose();

            try
            {
                var content = CreateGzipContent(jsonBytes);
                response = await _http.PostAsync(url, content, ct);

                if (response.IsSuccessStatusCode)
                    return response;

                var statusCode = (int)response.StatusCode;

                // 4xx except 429: client error, do not retry
                if (statusCode is >= 400 and < 500 and not 429)
                    return response;

                // Final attempt — return whatever we have
                if (attempt == totalAttempts - 1)
                    return response;

                // Determine delay
                int delayMs;
                if (statusCode == 429 &&
                    response.Headers.RetryAfter?.Delta is TimeSpan delta)
                {
                    delayMs = (int)Math.Min(delta.TotalMilliseconds, 30_000);
                    Logger.Info($"[Subframes] Rate limited (429). Retry-After {delayMs} ms. " +
                                $"Attempt {attempt + 1}/{totalAttempts}");
                }
                else
                {
                    delayMs = RetryDelaysMs[attempt];
                    Logger.Info($"[Subframes] HTTP {statusCode} from {url}, " +
                                $"retrying in {delayMs} ms (attempt {attempt + 1}/{totalAttempts})");
                }

                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                throw;
            }
            catch (HttpRequestException ex)
            {
                response?.Dispose();
                response = null;

                if (attempt == totalAttempts - 1)
                    throw;

                Logger.Info($"[Subframes] Network error on attempt {attempt + 1}/{totalAttempts}: " +
                            $"{ex.Message}, retrying in {RetryDelaysMs[attempt]} ms");
                await Task.Delay(RetryDelaysMs[attempt], ct);
            }
        }

        // Should be unreachable, but satisfies the compiler
        throw new InvalidOperationException("Retry loop exited without a response");
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
            var jsonBytes = SerializeJson(request);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);
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
            var jsonBytes = SerializeJson(body);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);
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
    /// so a slow server never blocks the caller. Not retried.
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
                Logger.Info($"[Subframes] POST {url} body={JsonSerializer.Serialize(request, JsonOptions)}");
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
    /// Not retried.
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
                Logger.Info($"[Subframes] POST {url} body={JsonSerializer.Serialize(request, JsonOptions)}");
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
            var jsonBytes = SerializeJson(body);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);
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
