using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using Subframes.NinaPlugin.Api;
using Subframes.NinaPlugin.Data;

namespace Subframes.NinaPlugin;

/// <summary>
/// Manages the active Subframes imaging session and handles the ImageSaved event.
///
/// Lifetime: singleton for the duration of NINA's process.
/// The StartSessionItem sequence item calls <see cref="StartSessionAsync"/> to
/// open a new server-side session before the sequence begins capturing.
/// From that point on every ImageSaved event fires <see cref="OnImageSaved"/>
/// which writes frame data to the local SQLite cache for background sync.
///
/// Thread safety: the active session ID is stored in a volatile field; the
/// event handler fires on NINA's internal thread pool so we must not block.
/// </summary>
public sealed class SessionService : IDisposable
{
    private readonly IImageSaveMediator _imageSaveMediator;
    private readonly SubframesClient _apiClient;
    private readonly PluginOptions _options;
    private readonly FrameCache _frameCache;
    private readonly SyncEngine _syncEngine;

    private volatile string? _activeSessionId;
    private volatile string? _activeSessionTargetId;
    private int _frameCounter;

    // Session status — "active", "waiting", "paused". Volatile for cross-thread visibility.
    private volatile string _sessionStatus = "active";

    // Heartbeat timer state
    private volatile string? _currentTarget;
    // Snapshot updated on each ImageSaved; volatile reference guarantees atomic swap.
    private volatile HeartbeatSnapshot _snapshot = new(null, null, null);
    private DateTime _sessionStartTime;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    // Auto-session detection state
    private volatile bool _isManualSession;
    private DateTime _lastFrameTime;
    private int _autoSessionGuard; // Interlocked: 0 = idle, 1 = auto-start in progress

    private sealed record HeartbeatSnapshot(string? Filter, double? LatestHfr, double? LatestRmsTotal);

    /// <summary>Replace non-finite doubles (NaN, ±Infinity) with null so JSON serialization never throws.</summary>
    private static double? Finite(double? v) => v is double d && double.IsFinite(d) ? v : null;

    public SessionService(
        IImageSaveMediator imageSaveMediator,
        SubframesClient apiClient,
        PluginOptions options,
        FrameCache frameCache,
        SyncEngine syncEngine)
    {
        _imageSaveMediator = imageSaveMediator;
        _apiClient = apiClient;
        _options = options;
        _frameCache = frameCache;
        _syncEngine = syncEngine;

        // Subscribe once; the handler fires for every saved image while NINA runs.
        _imageSaveMediator.ImageSaved += OnImageSaved;
        Logger.Debug("[Subframes] SessionService subscribed to ImageSaved.");
    }

    /// <summary>The server-assigned session ID, or null if no session is active.</summary>
    public string? ActiveSessionId => _activeSessionId;

    /// <summary>The server-assigned session target ID for the current target, or null.</summary>
    public string? ActiveSessionTargetId => _activeSessionTargetId;

    /// <summary>True when an imaging session is currently active.</summary>
    public bool HasActiveSession => _activeSessionId is not null;

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
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] Session start confirmed: sessionId={sessionId} target='{request.TargetName}'");
            _isManualSession = true;
            _currentTarget = request.TargetName;
            _activeSessionTargetId = null;
            _sessionStatus = "active";
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

        if (_options.IsDebugEnabled)
            Logger.Info($"[Subframes] Ending session: sessionId={sessionId} frameCount={_frameCounter}");

        // Flush any remaining cached frames before ending the session.
        try { await _syncEngine.FlushAsync(ct); }
        catch (Exception ex) { Logger.Warning($"[Subframes] Pre-end flush failed: {ex.Message}"); }

        StopHeartbeatTimer();
        _activeSessionId = null;
        _activeSessionTargetId = null;
        _sessionStatus = "active";
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

    // ── Target lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Signal that the sequencer has switched to a new imaging target.
    /// Stores the returned sessionTargetId so subsequent frames are associated with it.
    /// Safe to call when no session is active — returns null with no API call.
    /// </summary>
    public async Task<string?> StartTargetAsync(
        string targetName,
        double targetRa,
        double targetDec,
        string? targetType = null,
        CancellationToken ct = default)
    {
        var sessionId = _activeSessionId;
        if (sessionId is null) return null;

        var request = new StartSessionTargetRequest
        {
            SessionId  = sessionId,
            TargetName = targetName,
            TargetRa   = targetRa,
            TargetDec  = targetDec,
            StartTime  = DateTime.UtcNow.ToString("o"),
            TargetType = targetType,
        };

        var targetId = await _apiClient.StartTargetAsync(request, ct);
        _activeSessionTargetId = targetId;
        _currentTarget = targetName;
        _sessionStatus = "active";

        if (targetId is not null)
            Logger.Info($"[Subframes] Target started: {targetId} name='{targetName}'");

        return targetId;
    }

    /// <summary>
    /// Signal that the current target has completed.
    /// Clears the active sessionTargetId. Safe to call when no target is active.
    /// </summary>
    public async Task EndTargetAsync(CancellationToken ct = default)
    {
        var sessionId = _activeSessionId;
        var targetId  = _activeSessionTargetId;
        if (sessionId is null || targetId is null) return;

        _activeSessionTargetId = null;

        var request = new EndSessionTargetRequest
        {
            SessionId       = sessionId,
            SessionTargetId = targetId,
            EndTime         = DateTime.UtcNow.ToString("o"),
        };

        await _apiClient.EndTargetAsync(request, ct);
    }

    // ── Status transitions ───────────────────────────────────────────────────

    /// <summary>
    /// Update the session status (waiting / active / paused).
    /// For "waiting", supply a human-readable <paramref name="waitReason"/>.
    /// Safe to call when no session is active — returns immediately.
    /// </summary>
    public async Task UpdateStatusAsync(
        string status,
        string? waitReason = null,
        CancellationToken ct = default)
    {
        var sessionId = _activeSessionId;
        if (sessionId is null) return;

        _sessionStatus = status;

        var request = new UpdateSessionStatusRequest
        {
            SessionId  = sessionId,
            Status     = status,
            WaitReason = waitReason,
        };

        await _apiClient.UpdateSessionStatusAsync(request, ct);
    }

    // ── Sequence-start auto-session ─────────────────────────────────────────

    /// <summary>
    /// Called when the NINA sequence starts. If auto-session detection is
    /// enabled and no session is active, opens a session immediately so the
    /// dashboard reflects the run from the moment the user hits "Start".
    /// Target info comes from <see cref="NINA.Sequencer.Interfaces.Mediator.ISequenceMediator.GetAllTargets"/>
    /// when available; otherwise falls back to "Unknown Target" and adopts
    /// the real target name from the first captured image.
    /// </summary>
    public async Task OnSequenceStartedAsync(string? targetName, double targetRa, double targetDec)
    {
        if (_isManualSession || _activeSessionId is not null) return;

        var options = PluginOptions.Load();
        if (!options.IsEnabled || !options.AutoSessionDetection) return;

        if (Interlocked.CompareExchange(ref _autoSessionGuard, 1, 0) != 0) return;
        try
        {
            // Re-check after guard: another path may have started a session.
            if (_activeSessionId is not null) return;

            var hasTarget = !string.IsNullOrWhiteSpace(targetName);
            var resolvedTarget = hasTarget ? targetName! : "Unknown Target";

            var request = new StartSessionRequest
            {
                TargetName   = resolvedTarget,
                TargetRa     = targetRa,
                TargetDec    = targetDec,
                StartTime    = DateTime.UtcNow.ToString("o"),
                InstanceId   = string.IsNullOrWhiteSpace(options.InstanceId) ? null : options.InstanceId,
                InstanceName = string.IsNullOrWhiteSpace(options.InstanceName) ? null : options.InstanceName,
            };

            var sessionId = await _apiClient.StartSessionAsync(request, CancellationToken.None);
            _activeSessionId = sessionId;
            Interlocked.Exchange(ref _frameCounter, 0);

            if (sessionId is not null)
            {
                _isManualSession = false;
                // If we had a real target name, track it. Otherwise leave null
                // so OnImageSaved adopts the target from the first exposure.
                _currentTarget = hasTarget ? resolvedTarget : null;
                _sessionStatus = "active";
                _snapshot = new HeartbeatSnapshot(null, null, null);
                _sessionStartTime = DateTime.UtcNow;
                StartHeartbeatTimer(sessionId);
                Logger.Info($"[Subframes] Auto-session started on sequence start: sessionId={sessionId} target='{resolvedTarget}'");
            }
            else
            {
                Logger.Warning("[Subframes] Auto-session start on sequence start failed — session will start on first exposure instead.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Auto-session start on sequence start error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _autoSessionGuard, 0);
        }
    }

    // ── ImageSaved handler ───────────────────────────────────────────────────

    private void OnImageSaved(object? sender, ImageSavedEventArgs e)
    {
        _lastFrameTime = DateTime.UtcNow;
        var sessionId = _activeSessionId;

        // Auto-detection: start/transition sessions without an explicit StartSessionItem.
        if (!_isManualSession)
        {
            var options = PluginOptions.Load();
            if (options.IsEnabled && options.AutoSessionDetection)
            {
                var rawTarget = e.MetaData?.Target?.Name;
                var targetName = string.IsNullOrWhiteSpace(rawTarget)
                    ? "Unknown Target"
                    : CatalogNameNormalizer.Normalize(rawTarget);

                if (sessionId is null)
                {
                    // No active session — auto-start one; frame is posted inside StartAutoSessionAsync.
                    _ = StartAutoSessionAsync(targetName, e, "first frame");
                    return;
                }

                // Session was started from sequence start without a known target —
                // adopt the real target name from this first exposure.
                if (string.IsNullOrEmpty(_currentTarget))
                    _currentTarget = targetName;

                // Active auto-session — check for target change.
                var normalizedCurrent = string.IsNullOrEmpty(_currentTarget)
                    ? string.Empty
                    : CatalogNameNormalizer.Normalize(_currentTarget);
                if (!string.IsNullOrEmpty(normalizedCurrent)
                    && !string.Equals(normalizedCurrent, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"[Subframes] Auto-session boundary: target changed from '{_currentTarget}' to '{targetName}'");
                    _ = StartAutoSessionAsync(targetName, e, "target change");
                    return;
                }
            }
        }

        if (sessionId is null) return;

        // Fire-and-forget, but capture exceptions so nothing leaks to NINA.
        _ = PostFrameAsync(sessionId, e);
    }

    private Task PostFrameAsync(string sessionId, ImageSavedEventArgs e)
    {
        try
        {
            var meta = e.MetaData;
            var frameNumber = Interlocked.Increment(ref _frameCounter);

            var capturedAt = e.Duration > 0
                ? DateTime.UtcNow.AddSeconds(-e.Duration).ToString("o")
                : DateTime.UtcNow.ToString("o");

            var filter = meta.FilterWheel?.Filter;
            var hfr = e.StarDetectionAnalysis?.HFR;

            // Update heartbeat snapshot atomically so the timer always reads consistent state.
            // RMS guiding data is not available from ImageSavedEventArgs; left null for now.
            _snapshot = new HeartbeatSnapshot(filter, Finite(hfr), null);

            // If the session was in a waiting/paused state, a new exposure means we're active again.
            // Fire-and-forget the status transition; don't block the imaging path.
            if (_sessionStatus != "active")
            {
                _sessionStatus = "active";
                _ = _apiClient.UpdateSessionStatusAsync(
                    new UpdateSessionStatusRequest { SessionId = sessionId, Status = "active" },
                    CancellationToken.None);
            }

            var frame = new FrameInput
            {
                FrameNumber     = frameNumber,
                SessionTargetId = _activeSessionTargetId,
                ExposureTime    = meta.Image?.ExposureTime ?? 0.0,
                CapturedAt      = capturedAt,
                Filter          = filter,
                Gain            = meta.Camera?.Gain,
                Offset          = meta.Camera?.Offset,
                Binning         = meta.Camera?.BinX is int b ? (short)b : null,
                Hfr             = Finite(hfr),
                HfrStdev        = Finite(e.StarDetectionAnalysis?.HFRStDev),
                StarCount       = e.StarDetectionAnalysis?.DetectedStars,
                CameraTemp      = Finite(meta.Camera?.Temperature),
            };

            // Write to local SQLite cache — never blocks, never throws.
            // The SyncEngine will pick this up and batch-upload in the background.
            _frameCache.InsertFrame(sessionId, frame);

            // Fire-and-forget thumbnail — must not delay frame caching or imaging.
            _ = SendThumbnailAsync(sessionId, frameNumber, e);

            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] Frame cached: sessionId={sessionId} frameNumber={frameNumber} targetId={_activeSessionTargetId ?? "none"} filter={filter ?? "none"} hfr={hfr?.ToString("F2") ?? "n/a"}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Unexpected error in PostFrameAsync: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    // ── Thumbnail generation ─────────────────────────────────────────────────

    /// <summary>
    /// Scales the saved image to a 320-px-wide JPEG and uploads it.
    /// Fire-and-forget — never blocks the imaging thread and never throws.
    /// </summary>
    private async Task SendThumbnailAsync(string sessionId, int frameNumber, ImageSavedEventArgs e)
    {
        try
        {
            var bitmap = e.Image?.Image;
            if (bitmap is null)
            {
                Logger.Debug($"[Subframes] SendThumbnail skipped: no bitmap (frame={frameNumber})");
                return;
            }

            // Freeze the BitmapSource so it can be accessed off the UI thread.
            if (!bitmap.IsFrozen)
                bitmap.Freeze();

            // Scale to 320px wide, maintaining aspect ratio.
            const int targetWidth = 320;
            double scale = targetWidth / (double)bitmap.PixelWidth;
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            scaled.Freeze();

            // Encode as JPEG at 75% quality.
            byte[] jpegBytes;
            var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                jpegBytes = ms.ToArray();
            }

            await _apiClient.UploadThumbnailAsync(sessionId, frameNumber, jpegBytes, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] SendThumbnail error (session={sessionId} frame={frameNumber}): {ex.Message}");
        }
    }

    // ── Auto-session detection ───────────────────────────────────────────────

    /// <summary>
    /// Ends any active auto-session, then starts a new one for <paramref name="targetName"/>.
    /// Fires the triggering <paramref name="e"/> as the first frame of the new session.
    /// Re-entrant calls while an auto-start is in progress are silently dropped.
    /// </summary>
    private async Task StartAutoSessionAsync(string targetName, ImageSavedEventArgs e, string reason)
    {
        if (Interlocked.CompareExchange(ref _autoSessionGuard, 1, 0) != 0) return;
        try
        {
            var options = PluginOptions.Load();
            if (!options.IsEnabled || !options.AutoSessionDetection) return;

            // Re-check after acquiring the guard: another task may have already
            // started a session between our null-check in OnImageSaved and now.
            var existing = _activeSessionId;
            if (existing is not null && reason == "first frame")
            {
                Logger.Info($"[Subframes] Auto-session already active ({existing}), posting frame instead of starting duplicate");
                await PostFrameAsync(existing, e);
                return;
            }

            // End existing auto-session before transitioning (target change).
            if (existing is not null)
                await EndSessionAsync(CancellationToken.None);

            var request = new StartSessionRequest
            {
                TargetName   = targetName,
                TargetRa     = e.MetaData?.Target?.Coordinates?.RA ?? 0.0,
                TargetDec    = e.MetaData?.Target?.Coordinates?.Dec ?? 0.0,
                StartTime    = DateTime.UtcNow.ToString("o"),
                InstanceId   = string.IsNullOrWhiteSpace(options.InstanceId) ? null : options.InstanceId,
                InstanceName = string.IsNullOrWhiteSpace(options.InstanceName) ? null : options.InstanceName,
            };

            var sessionId = await _apiClient.StartSessionAsync(request, CancellationToken.None);
            _activeSessionId = sessionId;
            Interlocked.Exchange(ref _frameCounter, 0);

            if (sessionId is not null)
            {
                _isManualSession = false;
                Logger.Info($"[Subframes] Auto-session started: target='{targetName}' (trigger: {reason})");
                _currentTarget = targetName;
                _snapshot = new HeartbeatSnapshot(null, null, null);
                _sessionStartTime = DateTime.UtcNow;
                StartHeartbeatTimer(sessionId);
                await PostFrameAsync(sessionId, e);
            }
            else
            {
                Logger.Warning("[Subframes] Auto-session start failed — API unreachable?");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Auto-session start error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _autoSessionGuard, 0);
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
                // Auto-session inactivity timeout
                if (!_isManualSession && _lastFrameTime != default)
                {
                    var opts = PluginOptions.Load();
                    if (opts.AutoSessionDetection)
                    {
                        var idleMinutes = (DateTime.UtcNow - _lastFrameTime).TotalMinutes;
                        if (idleMinutes >= opts.SessionTimeoutMinutes)
                        {
                            Logger.Info($"[Subframes] Auto-session ended: no frames for {(int)idleMinutes} minutes");
                            await EndSessionAsync(CancellationToken.None);
                            return;
                        }
                    }
                }

                var snap = _snapshot;
                var payload = new HeartbeatRequest
                {
                    SessionId      = sessionId,
                    Status         = "imaging",
                    CurrentTarget  = _currentTarget,
                    CurrentFilter  = snap.Filter,
                    ExposureCount  = _frameCounter,
                    LatestHfr      = snap.LatestHfr,
                    LatestRmsTotal = snap.LatestRmsTotal,
                    UptimeMinutes  = (int)(DateTime.UtcNow - _sessionStartTime).TotalMinutes,
                    InstanceId     = string.IsNullOrWhiteSpace(_options.InstanceId) ? null : _options.InstanceId,
                    InstanceName   = string.IsNullOrWhiteSpace(_options.InstanceName) ? null : _options.InstanceName,
                };
                if (_options.IsDebugEnabled)
                    Logger.Info($"[Subframes] Heartbeat firing: sessionId={sessionId} frameCount={payload.ExposureCount} uptimeMin={payload.UptimeMinutes}");
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
        _imageSaveMediator.ImageSaved -= OnImageSaved;
        _apiClient.Dispose();
        Logger.Debug("[Subframes] SessionService disposed.");
    }
}
