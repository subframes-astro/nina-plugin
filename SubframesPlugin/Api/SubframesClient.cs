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
/// Data-bearing calls (session start/end, frame ingest) are retried up to
/// 3 times with 1 s / 2 s / 4 s exponential backoff on 5xx and network errors.
/// 4xx errors (except 429) are not retried.
/// Heartbeats are fire-and-forget and are not retried.
///
/// Thread safety: the auth header is injected per-request via
/// <see cref="ApiKeyHandler"/> rather than written to
/// <see cref="HttpClient.DefaultRequestHeaders"/>, which is not safe for
/// concurrent writes from multiple heartbeat threads.
/// </summary>
public sealed class SubframesClient : IDisposable
{
    /// <summary>
    /// Delegating handler that reads the current API key at send time and
    /// injects it as a Bearer token on every outgoing <see cref="HttpRequestMessage"/>.
    ///
    /// Setting the header on the per-request message object is thread-safe
    /// (each message is distinct).  This avoids the race condition that arises
    /// when multiple concurrent callers write to the shared
    /// <see cref="HttpClient.DefaultRequestHeaders"/> collection.
    /// </summary>
    private sealed class ApiKeyHandler : DelegatingHandler
    {
        private readonly PluginOptions _options;

        public ApiKeyHandler(PluginOptions options)
            : base(new HttpClientHandler())
        {
            _options = options;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var key = _options.ApiKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", key);

                if (_options.IsDebugEnabled)
                {
                    var preview = key.Length > 12 ? key[..12] + "..." : "(short key)";
                    Logger.Info($"[Subframes] Auth header set: Bearer {preview}");
                }
            }
            else if (_options.IsDebugEnabled)
            {
                Logger.Info("[Subframes] No API key configured — request will be unauthenticated");
            }

            return base.SendAsync(request, ct);
        }
    }
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
        _http = new HttpClient(new ApiKeyHandler(options))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private string BaseUrl => _options.ApiBaseUrl.TrimEnd('/');

    // ── Retry helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="body"/> to JSON bytes for use with
    /// <see cref="PostWithRetryAsync"/>.
    /// </summary>
    private static byte[] SerializeJson<T>(T body) =>
        JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);

    /// <summary>
    /// Wraps JSON bytes in an <see cref="HttpContent"/> with
    /// the appropriate Content-Type header.
    /// </summary>
    private static HttpContent CreateJsonContent(byte[] jsonBytes)
    {
        var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
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
                var content = CreateJsonContent(jsonBytes);
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
            var url = $"{BaseUrl}/api/v1/ingest/session/start";
            var jsonBytes = SerializeJson(request);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Logger.Error($"[Subframes] StartSession HTTP {(int)response.StatusCode}: {body}");
                return null;
            }

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
        int? skippedExposures = null,
        int? failedExposures = null,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/session/end";
            var body = new EndSessionRequest
            {
                SessionId = sessionId,
                EndTime = DateTime.UtcNow.ToString("o"),
                SkippedExposures = skippedExposures,
                FailedExposures = failedExposures,
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

    // ── Session Target Start ─────────────────────────────────────────────────

    /// <summary>
    /// Signal that the sequencer has switched to a new target within the active session.
    /// Returns the server-assigned sessionTargetId, or null if the call failed or the
    /// endpoint does not exist on this API version (404 → graceful no-op).
    /// </summary>
    public async Task<string?> StartTargetAsync(
        StartSessionTargetRequest request,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return null;

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/session/target/start";
            var jsonBytes = SerializeJson(request);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);

            // 404 means the API version doesn't support multi-target — fall back silently.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Info("[Subframes] target/start endpoint not found — running single-target mode");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var envelope = await response.Content
                .ReadFromJsonAsync<ApiEnvelope<StartSessionTargetData>>(JsonOptions, ct);
            var targetId = envelope?.Data?.SessionTargetId;
            Logger.Info($"[Subframes] Target started: {targetId} name='{request.TargetName}'");
            return targetId;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] StartTarget failed: {ex.Message}");
            return null;
        }
    }

    // ── Session Target End ───────────────────────────────────────────────────

    /// <summary>
    /// Signal that the current target has completed.
    /// 404 → no-op (older API). All other failures are logged and swallowed.
    /// </summary>
    public async Task EndTargetAsync(
        EndSessionTargetRequest request,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/session/target/end";
            var jsonBytes = SerializeJson(request);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Debug("[Subframes] target/end endpoint not found — no-op");
                return;
            }

            response.EnsureSuccessStatusCode();
            Logger.Info($"[Subframes] Target ended: {request.SessionTargetId}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] EndTarget failed: {ex.Message}");
        }
    }

    // ── Session Status ───────────────────────────────────────────────────────

    /// <summary>
    /// Update the session status (waiting / active / paused).
    /// 404 → no-op (older API). All other failures are logged and swallowed.
    /// </summary>
    public async Task UpdateSessionStatusAsync(
        UpdateSessionStatusRequest request,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/session/status";
            var jsonBytes = SerializeJson(request);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Debug("[Subframes] session/status endpoint not found — no-op");
                return;
            }

            response.EnsureSuccessStatusCode();
            Logger.Info($"[Subframes] Session status updated: {request.Status}" +
                        (request.WaitReason is not null ? $" ({request.WaitReason})" : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] UpdateSessionStatus failed: {ex.Message}");
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
            var url = $"{BaseUrl}/api/v1/ingest/station/heartbeat";
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={JsonSerializer.Serialize(request, JsonOptions)}");
            using var response = await _http.PostAsJsonAsync(url, request, JsonOptions, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                Logger.Warning($"[Subframes] Station heartbeat failed: {(int)response.StatusCode} {response.ReasonPhrase} — {body}");
                return;
            }
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] Station heartbeat accepted: {(int)response.StatusCode} (status={request.Status})");
            else
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

    // ── Thumbnail Upload ─────────────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget thumbnail upload. Posts JPEG bytes as multipart form data to
    /// /api/v1/ingest/frame/thumbnail. Uses a 10-second timeout. Not retried.
    /// 404 → no-op (endpoint not yet deployed). All exceptions are caught and logged.
    /// </summary>
    public async Task UploadThumbnailAsync(
        string sessionId,
        int frameNumber,
        byte[] jpeg,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/frame/thumbnail";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(sessionId), "sessionId");
            form.Add(new StringContent(frameNumber.ToString()), "frameNumber");
            var imageContent = new ByteArrayContent(jpeg);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(imageContent, "thumbnail", "thumbnail.jpg");

            using var response = await _http.PostAsync(url, form, cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Info("[Subframes] Thumbnail endpoint not found — skipping (endpoint not yet deployed)");
                return;
            }

            if (!response.IsSuccessStatusCode)
                Logger.Warning($"[Subframes] UploadThumbnail HTTP {(int)response.StatusCode}");
            else if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] Thumbnail uploaded: sessionId={sessionId} frameNumber={frameNumber} size={jpeg.Length}B");
        }
        catch (OperationCanceledException)
        {
            Logger.Warning($"[Subframes] Thumbnail upload timed out (session={sessionId} frame={frameNumber})");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] UploadThumbnail failed (session={sessionId} frame={frameNumber}): {ex.Message}");
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

    // ── TS Grading ───────────────────────────────────────────────────────────

    /// <summary>
    /// POST Target Scheduler grading results for frames captured during a session.
    /// 404 → no-op (older API). All other failures are logged and swallowed.
    /// </summary>
    public async Task PostTsGradingAsync(
        string sessionId,
        List<TsGradingInput> entries,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/session/{sessionId}/ts-grading";
            var body = new TsGradingRequest { Entries = entries };
            var jsonBytes = SerializeJson(body);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} entries={entries.Count}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Debug("[Subframes] ts-grading endpoint not found — no-op");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Logger.Warning($"[Subframes] PostTsGrading HTTP {(int)response.StatusCode}: {errorBody}");
                return;
            }

            Logger.Info($"[Subframes] TS grading sent: {entries.Count} entry/entries for session {sessionId}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] PostTsGrading failed (session={sessionId}): {ex.Message}");
        }
    }

    /// <summary>
    /// POST all-time Target Scheduler project/target progress at session end.
    /// 404 → no-op (older API). All other failures are logged and swallowed.
    /// </summary>
    public async Task PostTsProgressAsync(
        string sessionId,
        List<TsProgressInput> entries,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/session/{sessionId}/ts-progress";
            var body = new TsProgressRequest { Entries = entries };
            var jsonBytes = SerializeJson(body);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} entries={entries.Count}");

            using var response = await PostWithRetryAsync(url, jsonBytes, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Debug("[Subframes] ts-progress endpoint not found — no-op");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Logger.Warning($"[Subframes] PostTsProgress HTTP {(int)response.StatusCode}: {errorBody}");
                return;
            }

            Logger.Info($"[Subframes] TS progress sent: {entries.Count} row(s) for session {sessionId}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] PostTsProgress failed (session={sessionId}): {ex.Message}");
        }
    }

    // ── Session Event ────────────────────────────────────────────────────────

    /// <summary>
    /// Post a discrete session event (e.g. autofocus completion, meridian flip).
    /// Fire-and-forget from the caller's perspective — uses a 5-second timeout,
    /// no retry. Failures are logged and swallowed so event losses never interrupt
    /// imaging.
    /// </summary>
    public async Task PostEventAsync(
        EventRequest request,
        CancellationToken ct = default)
    {
        if (!_options.IsEnabled) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var url = $"{BaseUrl}/api/v1/ingest/event";
            var jsonBytes = SerializeJson(request);
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] POST {url} body={System.Text.Encoding.UTF8.GetString(jsonBytes)}");

            using var response = await _http.PostAsync(url, CreateJsonContent(jsonBytes), cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                Logger.Warning($"[Subframes] PostEvent HTTP {(int)response.StatusCode}: {body}");
                return;
            }

            Logger.Debug($"[Subframes] Event posted: type={request.EventType} session={request.SessionId}");
        }
        catch (OperationCanceledException)
        {
            Logger.Warning($"[Subframes] PostEvent timed out (type={request.EventType} session={request.SessionId})");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] PostEvent failed (type={request.EventType}): {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
