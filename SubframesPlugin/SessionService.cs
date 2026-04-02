using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Manages the active Subframes imaging session and handles the ImageSaved event.
///
/// Lifetime: singleton for the duration of NINA's process.
/// The StartSessionItem sequence item calls <see cref="StartSessionAsync"/> to
/// open a new server-side session before the sequence begins capturing.
/// From that point on every ImageSaved event fires <see cref="OnImageSaved"/>
/// which asynchronously POSTs the frame to the ingest API.
///
/// Thread safety: the active session ID is stored in a volatile field; the
/// event handler fires on NINA's internal thread pool so we must not block.
/// </summary>
public sealed class SessionService : IDisposable
{
    private readonly IImageSaveMediator _imageSaveMediator;
    private readonly SubframesClient _apiClient;
    private readonly PluginOptions _options;

    private volatile string? _activeSessionId;
    private int _frameCounter;

    // Heartbeat timer state
    private volatile string? _currentTarget;
    // Snapshot updated on each ImageSaved; volatile reference guarantees atomic swap.
    private volatile HeartbeatSnapshot _snapshot = new(null, null, null);
    private DateTime _sessionStartTime;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    private sealed record HeartbeatSnapshot(string? Filter, double? LatestHfr, double? LatestRmsTotal);

    public SessionService(
        IImageSaveMediator imageSaveMediator,
        SubframesClient apiClient,
        PluginOptions options)
    {
        _imageSaveMediator = imageSaveMediator;
        _apiClient = apiClient;
        _options = options;

        // Subscribe once; the handler fires for every saved image while NINA runs.
        _imageSaveMediator.BeforeImageSaved += OnImageSaved;
        Logger.Debug("[Subframes] SessionService subscribed to BeforeImageSaved.");
    }

    /// <summary>The server-assigned session ID, or null if no session is active.</summary>
    public string? ActiveSessionId => _activeSessionId;

    /// <summary>
    /// Call this from the StartSessionItem sequence item.
    /// POSTs to /api/v1/ingest/session/start and stores the returned ID.
    /// </summary>
    public async Task<string?> StartSessionAsync(
        StartSessionRequest request,
        CancellationToken ct = default)
    {
        var sessionId = await _apiClient.StartSessionAsync(request, ct);
        _activeSessionId = sessionId;
        Interlocked.Exchange(ref _frameCounter, 0);

        if (sessionId is not null)
        {
            Logger.Info($"[Subframes] Session started: {sessionId} target='{request.TargetName}'");
            _currentTarget = request.TargetName;
            _snapshot = new HeartbeatSnapshot(null, null, null);
            _sessionStartTime = DateTime.UtcNow;
            StartHeartbeatTimer(sessionId);
        }
        else
        {
            Logger.Warning("[Subframes] Failed to start session — exposures will not be recorded.");
        }

        return sessionId;
    }

    /// <summary>End the active session and clear state.</summary>
    public async Task EndSessionAsync(CancellationToken ct = default)
    {
        var sessionId = _activeSessionId;
        if (sessionId is null) return;

        StopHeartbeatTimer();
        _activeSessionId = null;
        await _apiClient.EndSessionAsync(sessionId, ct);
        Logger.Info("[Subframes] Session ended.");
    }

    /// <summary>Clear the active session without notifying the server.</summary>
    public void ClearSession()
    {
        StopHeartbeatTimer();
        _activeSessionId = null;
        Logger.Info("[Subframes] Session cleared.");
    }

    // ── BeforeImageSaved handler ─────────────────────────────────────────────

    private void OnImageSaved(object? sender, BeforeImageSavedEventArgs e)
    {
        var sessionId = _activeSessionId;
        if (sessionId is null) return;

        // Fire-and-forget, but capture exceptions so nothing leaks to NINA.
        _ = PostFrameAsync(sessionId, e);
    }

    private async Task PostFrameAsync(string sessionId, BeforeImageSavedEventArgs e)
    {
        try
        {
            var meta = e.MetaData;
            var frameNumber = Interlocked.Increment(ref _frameCounter);

            var capturedAt = e.Duration > TimeSpan.Zero
                ? DateTime.UtcNow.Subtract(e.Duration).ToString("o")
                : DateTime.UtcNow.ToString("o");

            var filter = meta.FilterWheel?.Filter?.Name;
            var hfr = e.StarDetectionAnalysis?.HFR;

            // Update heartbeat snapshot atomically so the timer always reads consistent state.
            // RMS guiding data is not available from BeforeImageSavedEventArgs; left null for now.
            _snapshot = new HeartbeatSnapshot(filter, hfr, null);

            var frame = new FrameInput
            {
                FrameNumber  = frameNumber,
                ExposureTime = meta.Image?.ExposureTime ?? 0.0,
                CapturedAt   = capturedAt,
                Filter       = filter,
                Gain         = meta.Camera?.Gain is double g ? (int)g : null,
                Offset       = meta.Camera?.Offset is double o ? (int)o : null,
                Binning      = meta.Camera?.BinX is int b ? (short)b : null,
                Hfr          = hfr,
                HfrStdev     = e.StarDetectionAnalysis?.HFRStDev,
                StarCount    = e.StarDetectionAnalysis?.DetectedStars,
                CameraTemp   = meta.Camera?.Temperature,
            };

            await _apiClient.IngestFramesAsync(
                sessionId,
                new List<FrameInput> { frame });
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Unexpected error in PostFrameAsync: {ex.Message}");
        }
    }

    // ── Heartbeat timer ──────────────────────────────────────────────────────

    private void StartHeartbeatTimer(string sessionId)
    {
        StopHeartbeatTimer();
        var cts = new CancellationTokenSource();
        _heartbeatCts = cts;
        _heartbeatTask = RunHeartbeatLoopAsync(sessionId, cts.Token);
    }

    private void StopHeartbeatTimer()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
        _heartbeatTask = null;
    }

    private async Task RunHeartbeatLoopAsync(string sessionId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var snap = _snapshot;
                var payload = new HeartbeatRequest
                {
                    SessionId    = sessionId,
                    Status       = "imaging",
                    CurrentTarget = _currentTarget,
                    CurrentFilter = snap.Filter,
                    ExposureCount = _frameCounter,
                    LatestHfr    = snap.LatestHfr,
                    LatestRmsTotal = snap.LatestRmsTotal,
                    UptimeMinutes = (int)(DateTime.UtcNow - _sessionStartTime).TotalMinutes,
                };
                // Fire-and-forget — never block the timer loop on a slow network.
                _ = _apiClient.SendHeartbeatAsync(payload, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — session ended or plugin disposed.
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Heartbeat loop terminated unexpectedly: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopHeartbeatTimer();
        _imageSaveMediator.BeforeImageSaved -= OnImageSaved;
        _apiClient.Dispose();
        Logger.Debug("[Subframes] SessionService disposed.");
    }
}
