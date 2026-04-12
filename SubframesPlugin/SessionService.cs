using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
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
public sealed class SessionService : IDisposable, IFocuserConsumer
{
    private readonly IImageSaveMediator _imageSaveMediator;
    private readonly SubframesClient _apiClient;
    private readonly PluginOptions _options;
    private readonly FrameCache _frameCache;
    private readonly SyncEngine _syncEngine;
    private readonly ISafetyMonitorMediator? _safetyMonitorMediator;
    private readonly IGuiderMediator? _guiderMediator;
    private readonly IWeatherDataMediator? _weatherDataMediator;
    private readonly IRotatorMediator? _rotatorMediator;
    private readonly ITelescopeMediator? _telescopeMediator;
    private readonly IFocuserMediator? _focuserMediator;

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

    // Exposure yield counters — thread-safe via Interlocked.
    // Remain null until a detection mechanism is wired up; null means "not tracked"
    // so the backend omits these fields rather than showing incorrect zeros.
    private int _skippedExposures;
    private int _failedExposures;
    private volatile bool _trackingExposureYield;

    // Exposure yield polling — 1-second timer polls sequencer for LIGHT frame skip/fail transitions.
    private Timer? _yieldPollTimer;
    private Dictionary<int, string>? _prevItemStatuses;

    private sealed record HeartbeatSnapshot(string? Filter, double? LatestHfr, double? LatestRmsTotal);

    /// <summary>Replace non-finite doubles (NaN, ±Infinity) with null so JSON serialization never throws.</summary>
    private static double? Finite(double? v) => v is double d && double.IsFinite(d) ? v : null;

    // ── Hocus Focus reflection cache ─────────────────────────────────────────
    // FWHM and Eccentricity are not on IStarDetectionAnalysis in stock NINA 3.x;
    // they only exist on concrete implementations (e.g. Hocus Focus plugin).
    // We resolve the PropertyInfo once and cache it to avoid per-frame overhead.
    private static PropertyInfo? _fwhmProp;
    private static PropertyInfo? _eccentricityProp;
    private static bool _fwhmResolved;
    private static bool _eccentricityResolved;

    /// <summary>
    /// Reads a <c>double</c> property from <paramref name="obj"/> by reflection, trying each
    /// candidate <paramref name="names"/> in order. The resolved <see cref="PropertyInfo"/> is
    /// cached in <paramref name="cached"/> after the first call so subsequent calls are O(1).
    /// Returns <c>null</c> when the object is null, when no matching property exists, or when
    /// the value is not a <c>double</c>.
    /// </summary>
    private static double? ReadReflectedDouble(object? obj, ref PropertyInfo? cached, ref bool resolved, params string[] names)
    {
        if (obj == null) return null;
        if (!resolved)
        {
            var type = obj.GetType();
            foreach (var name in names)
            {
                cached = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (cached != null) break;
            }
            resolved = true;
            Logger.Debug(cached != null
                ? $"[Subframes] Reflection: found property '{cached.Name}' on {type.Name} for FWHM/Eccentricity."
                : $"[Subframes] Reflection: no FWHM/Eccentricity property found on {type.Name} — fields will be null.");
        }
        if (cached == null) return null;
        var val = cached.GetValue(obj);
        return val is double d ? d : null;
    }

    /// <summary>Returns the safety monitor's IsSafe value when connected, or null if unavailable.</summary>
    private bool? ReadIsSafe()
    {
        try
        {
            var info = _safetyMonitorMediator?.GetInfo();
            return info is { Connected: true } ? info.IsSafe : null;
        }
        catch { return null; }
    }

    public SessionService(
        IImageSaveMediator imageSaveMediator,
        SubframesClient apiClient,
        PluginOptions options,
        FrameCache frameCache,
        SyncEngine syncEngine,
        ISafetyMonitorMediator? safetyMonitorMediator = null,
        IGuiderMediator? guiderMediator = null,
        IWeatherDataMediator? weatherDataMediator = null,
        IRotatorMediator? rotatorMediator = null,
        ITelescopeMediator? telescopeMediator = null,
        IFocuserMediator? focuserMediator = null)
    {
        _imageSaveMediator = imageSaveMediator;
        _apiClient = apiClient;
        _options = options;
        _frameCache = frameCache;
        _syncEngine = syncEngine;
        _safetyMonitorMediator = safetyMonitorMediator;
        _guiderMediator = guiderMediator;
        _weatherDataMediator = weatherDataMediator;
        _rotatorMediator = rotatorMediator;
        _telescopeMediator = telescopeMediator;
        _focuserMediator = focuserMediator;

        // Subscribe once; the handler fires for every saved image while NINA runs.
        _imageSaveMediator.ImageSaved += OnImageSaved;
        Logger.Debug("[Subframes] SessionService subscribed to ImageSaved.");
    }

    /// <summary>
    /// Set by Plugin.cs to resolve the currently running DSO container on each heartbeat tick.
    /// Returns (normalizedName, ra, dec) for the active target, or null if unavailable.
    /// </summary>
    public Func<(string? name, double ra, double dec)?>? ActiveTargetResolver { get; set; }

    /// <summary>
    /// Raised after a new session is successfully started via <see cref="StartSessionAsync"/>.
    /// Plugin.cs subscribes to this event to fire an immediate station heartbeat so the
    /// website reflects equipment data and imaging status without waiting for the 5-minute timer.
    /// </summary>
    public event EventHandler? SessionStarted;

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
        Interlocked.Exchange(ref _skippedExposures, 0);
        Interlocked.Exchange(ref _failedExposures, 0);
        _trackingExposureYield = false;

        if (sessionId is not null)
        {
            Logger.Info($"[Subframes] Session started: {sessionId} target='{request.TargetName}'");
            if (_options.IsDebugEnabled)
                Logger.Info($"[Subframes] Session start confirmed: sessionId={sessionId} target='{request.TargetName}'");
            _isManualSession = true;
            // Store null when target name is empty or "Unknown Target" so
            // OnTargetDetectedAsync will adopt the real target from the first
            // DSO container start or image save — instead of displaying a
            // bogus "Unknown Target" in heartbeats until then.
            _currentTarget = string.IsNullOrEmpty(request.TargetName)
                || string.Equals(request.TargetName, "Unknown Target", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : request.TargetName;
            _activeSessionTargetId = null;
            _sessionStatus = "active";
            _snapshot = new HeartbeatSnapshot(null, null, null);
            _sessionStartTime = DateTime.UtcNow;
            StartHeartbeatTimer(sessionId);
            StartYieldPollTimer();
            SessionStarted?.Invoke(this, EventArgs.Empty);
            RegisterSessionEventConsumers();
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
        UnregisterSessionEventConsumers();

        // Close any auto-detected target that was never explicitly ended.
        if (_activeSessionTargetId is not null)
        {
            try { await EndTargetAsync(ct); }
            catch (Exception ex) { Logger.Warning($"[Subframes] EndTarget during session end failed: {ex.Message}"); }
        }

        int? skipped = _trackingExposureYield ? _skippedExposures : null;
        int? failed  = _trackingExposureYield ? _failedExposures  : null;

        // Query TS for per-frame grading results and all-time progress before clearing state.
        var sessionEnd = DateTime.UtcNow;
        var tsGrading  = TsGradingReader.ReadGradingResults(_sessionStartTime, sessionEnd);
        Logger.Info($"[Subframes] TS grading results: {tsGrading?.Count ?? 0} entry/entries.");
        var tsProgress = TsProgressReader.ReadProgress();
        Logger.Info($"[Subframes] TS progress results: {tsProgress?.Count ?? 0} row(s).");

        _activeSessionId = null;
        _activeSessionTargetId = null;
        _sessionStatus = "active";
        await _apiClient.EndSessionAsync(sessionId, skipped, failed, ct);

        if (tsGrading is { Count: > 0 })
        {
            Logger.Info($"[Subframes] Sending {tsGrading.Count} TS grading entry/entries to API.");
            await _apiClient.PostTsGradingAsync(sessionId, tsGrading, ct);
        }

        if (tsProgress is { Count: > 0 })
        {
            Logger.Info($"[Subframes] Sending {tsProgress.Count} TS progress row(s) to API.");
            await _apiClient.PostTsProgressAsync(sessionId, tsProgress, ct);
        }

        Logger.Info("[Subframes] Session ended.");
    }

    /// <summary>Clear the active session without notifying the server.</summary>
    public void ClearSession()
    {
        StopHeartbeatTimer();
        UnregisterSessionEventConsumers();
        _activeSessionId = null;
        Logger.Info("[Subframes] Session cleared.");
    }

    /// <summary>
    /// Record one skipped exposure for the current session.
    /// Thread-safe; safe to call from any thread.
    /// Call <see cref="EnableExposureYieldTracking"/> before the first increment.
    /// </summary>
    public void IncrementSkippedExposures()
    {
        Interlocked.Increment(ref _skippedExposures);
    }

    /// <summary>
    /// Record one failed exposure for the current session.
    /// Thread-safe; safe to call from any thread.
    /// Call <see cref="EnableExposureYieldTracking"/> before the first increment.
    /// </summary>
    public void IncrementFailedExposures()
    {
        Interlocked.Increment(ref _failedExposures);
    }

    /// <summary>
    /// Signal that a detection mechanism is active and exposure counts should be
    /// reported at session end. Without this flag the counters are omitted from
    /// the session-end payload (null = not tracked), avoiding false-zero reports.
    /// </summary>
    public void EnableExposureYieldTracking()
    {
        _trackingExposureYield = true;
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

        // RA=0/Dec=0 is the vernal equinox — not a real DSO target.
        // Skip the API call so the backend never receives a bogus 0,0 location.
        if (targetRa == 0 && targetDec == 0)
        {
            Logger.Debug("[Subframes] StartTargetAsync skipped: RA=0/Dec=0 (no valid target coordinates).");
            return null;
        }

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

    /// <summary>
    /// Called when a <c>DeepSkyObjectContainer</c> transitions to RUNNING in the NINA sequence.
    /// Immediately transitions the current target so the heartbeat reports the correct
    /// target name from the moment the container starts executing — without waiting for
    /// the first image to be saved.  Works for both manual and auto sessions.
    /// </summary>
    public async Task OnDSOContainerStartedAsync(string targetName, double targetRa, double targetDec)
    {
        if (_activeSessionId is null) return;
        await OnTargetDetectedAsync(targetName, targetRa, targetDec);
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

    // ── Target detection ───────────────────────────────────────────────────

    /// <summary>
    /// Called when a target is detected externally (from GetAllTargets or ImageSaved metadata).
    /// If the target differs from the current tracked target, ends any active target, registers
    /// the new one via <see cref="StartTargetAsync"/>, and fires an immediate heartbeat so the
    /// web app updates without waiting for the next 60-second tick.
    /// Safe to call from any thread; no-op if no session is active.
    /// </summary>
    public async Task OnTargetDetectedAsync(string targetName, double targetRa, double targetDec)
    {
        var sessionId = _activeSessionId;
        if (sessionId is null) return;

        var normalized = CatalogNameNormalizer.Normalize(targetName);

        // Don't "detect" an unknown target.
        if (string.Equals(normalized, "Unknown Target", StringComparison.OrdinalIgnoreCase))
            return;

        // Same target AND a server-side target already exists — nothing to do.
        // If _activeSessionTargetId is null, we still need to register the target
        // even when the name matches (e.g. session was opened with "M42" but no
        // explicit StartTargetItem was used — the first frame must create it).
        if (_activeSessionTargetId is not null
            && !string.IsNullOrEmpty(_currentTarget)
            && string.Equals(CatalogNameNormalizer.Normalize(_currentTarget), normalized, StringComparison.OrdinalIgnoreCase))
            return;

        Logger.Info($"[Subframes] Target detected: '{normalized}' (was '{_currentTarget ?? "none"}')");

        // End current target if one is active.
        if (_activeSessionTargetId is not null)
        {
            try { await EndTargetAsync(CancellationToken.None); }
            catch (Exception ex) { Logger.Warning($"[Subframes] EndTarget failed during target detection: {ex.Message}"); }
        }

        // Register the new target.
        await StartTargetAsync(normalized, targetRa, targetDec, null, CancellationToken.None);

        // Fire an immediate heartbeat so the web app reflects the change right away.
        FireImmediateHeartbeat(sessionId);
    }

    /// <summary>
    /// Sends a heartbeat immediately (outside the regular 60-second timer) so the web app
    /// reflects state changes like target detection without waiting for the next tick.
    /// </summary>
    private void FireImmediateHeartbeat(string sessionId)
    {
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
            IsSafe         = ReadIsSafe(),
            InstanceId     = string.IsNullOrWhiteSpace(_options.InstanceId) ? null : _options.InstanceId,
            InstanceName   = string.IsNullOrWhiteSpace(_options.InstanceName) ? null : _options.InstanceName,
        };
        if (_options.IsDebugEnabled)
            Logger.Info($"[Subframes] Immediate heartbeat: sessionId={sessionId} target='{_currentTarget}'");
        _ = _apiClient.SendHeartbeatAsync(payload, CancellationToken.None);
    }

    // ── Sequence-start auto-session ─────────────────────────────────────────

    /// <summary>
    /// Called when the NINA sequence starts. If auto-session detection is
    /// enabled and no session is active, opens a session immediately so the
    /// dashboard reflects the run from the moment the user hits "Start".
    /// Target info comes from <see cref="NINA.Sequencer.Interfaces.Mediator.ISequenceMediator.GetAllTargets"/>
    /// when available; otherwise sends an empty target name (the backend skips
    /// auto-creation for empty names) and adopts the real target from the first captured image.
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

            // RA=0/Dec=0 means no valid target is known yet (vernal equinox is not a real DSO).
            var hasTarget = !string.IsNullOrWhiteSpace(targetName) && !(targetRa == 0 && targetDec == 0);
            var resolvedTarget = hasTarget ? targetName! : string.Empty;

            var plannedTargets = TsPlannedTargetReader.ReadPlannedTargets();

            var request = new StartSessionRequest
            {
                TargetName     = resolvedTarget,
                TargetRa       = targetRa,
                TargetDec      = targetDec,
                StartTime      = DateTime.UtcNow.ToString("o"),
                InstanceId     = string.IsNullOrWhiteSpace(options.InstanceId) ? null : options.InstanceId,
                InstanceName   = string.IsNullOrWhiteSpace(options.InstanceName) ? null : options.InstanceName,
                PlannedTargets = plannedTargets,
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
                StartYieldPollTimer();
                RegisterSessionEventConsumers();
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
                    ? string.Empty
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
        else if (sessionId is not null)
        {
            // Manual session — detect and register target changes automatically
            // so the web app updates when the sequencer moves to a new target.
            var rawTarget = e.MetaData?.Target?.Name;
            if (!string.IsNullOrWhiteSpace(rawTarget))
            {
                var targetRa = e.MetaData?.Target?.Coordinates?.RA ?? 0.0;
                var targetDec = e.MetaData?.Target?.Coordinates?.Dec ?? 0.0;
                _ = OnTargetDetectedAsync(rawTarget, targetRa, targetDec);
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

            // Read guiding RMS (in-memory snapshot — no I/O, no blocking).
            double? rmsRa = null, rmsDec = null, rmsTotal = null;
            try
            {
                var guiderInfo = _guiderMediator?.GetInfo();
                if (guiderInfo is { Connected: true } && guiderInfo.RMSError.Total.Arcseconds > 0)
                {
                    rmsRa    = Finite(guiderInfo.RMSError.RA.Arcseconds);
                    rmsDec   = Finite(guiderInfo.RMSError.Dec.Arcseconds);
                    rmsTotal = Finite(guiderInfo.RMSError.Total.Arcseconds);
                }
            }
            catch { /* guider not available — leave null */ }

            // Read weather conditions (in-memory snapshot — no I/O, no blocking).
            double? ambientTemp = null, humidity = null, dewPoint = null, windSpeed = null, cloudCover = null, skyQuality = null;
            try
            {
                var weatherInfo = _weatherDataMediator?.GetInfo();
                if (weatherInfo is { Connected: true })
                {
                    ambientTemp = Finite(weatherInfo.Temperature);
                    humidity    = Finite(weatherInfo.Humidity);
                    dewPoint    = Finite(weatherInfo.DewPoint);
                    windSpeed   = Finite(weatherInfo.WindSpeed);
                    cloudCover  = Finite(weatherInfo.CloudCover);
                    skyQuality  = Finite(weatherInfo.SkyQuality);
                }
            }
            catch { /* weather device not available — leave null */ }

            // Read rotator mechanical position (in-memory snapshot — no I/O, no blocking).
            // Null when no rotator is connected; never 0 as a fallback (0 deg is a valid angle).
            double? rotatorPosition = null;
            try
            {
                var rotatorInfo = _rotatorMediator?.GetInfo();
                if (rotatorInfo is { Connected: true })
                    rotatorPosition = Finite((double)rotatorInfo.MechanicalPosition);
            }
            catch { /* rotator not available — leave null */ }

            // Update heartbeat snapshot atomically so the timer always reads consistent state.
            _snapshot = new HeartbeatSnapshot(filter, Finite(hfr), rmsTotal);

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
                Fwhm            = Finite(ReadReflectedDouble(e.StarDetectionAnalysis, ref _fwhmProp, ref _fwhmResolved, "FWHM", "StarFWHM")),
                Eccentricity    = Finite(ReadReflectedDouble(e.StarDetectionAnalysis, ref _eccentricityProp, ref _eccentricityResolved, "Eccentricity")),
                CameraTemp      = Finite(meta.Camera?.Temperature),
                RmsRa           = rmsRa,
                RmsDec          = rmsDec,
                RmsTotal        = rmsTotal,
                AmbientTemp     = ambientTemp,
                Humidity        = humidity,
                DewPoint        = dewPoint,
                WindSpeed       = windSpeed,
                CloudCover       = cloudCover,
                SkyQuality       = skyQuality,
                RotatorPosition  = rotatorPosition,
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
            var bitmap = e.Image;
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
                StartYieldPollTimer();
                RegisterSessionEventConsumers();
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

    // ── Exposure yield polling ───────────────────────────────────────────────

    /// <summary>
    /// Provides the list of current running sequence items for exposure yield tracking.
    /// Set by Plugin.cs after <c>ISequenceMediator</c> is resolved via MEF.
    /// Returns null when the sequencer is unavailable or the method does not exist on
    /// this NINA build — polling will continue but <see cref="_trackingExposureYield"/>
    /// will remain false so the backend receives null counters (not tracked).
    /// </summary>
    public Func<System.Collections.IList?>? SequenceItemsProvider { get; set; }

    private void StartYieldPollTimer()
    {
        StopYieldPollTimer();
        _prevItemStatuses = null;
        _yieldPollTimer = new Timer(PollExposureYield, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopYieldPollTimer()
    {
        _yieldPollTimer?.Dispose();
        _yieldPollTimer = null;
        _prevItemStatuses = null;
    }

    /// <summary>
    /// Timer callback — fires every second during an active session to detect LIGHT
    /// frame exposures that have transitioned to SKIPPED or FAILED in the NINA sequencer.
    /// All exceptions are caught; polling errors must never affect imaging.
    /// </summary>
    private void PollExposureYield(object? state)
    {
        try
        {
            var provider = SequenceItemsProvider;
            if (provider is null) return;

            System.Collections.IList? items;
            try
            {
                items = provider.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Subframes] PollExposureYield: sequence item provider threw, disabling yield tracking. {ex.Message}");
                _trackingExposureYield = false;
                StopYieldPollTimer();
                return;
            }

            if (items is null) return;

            // At least one successful poll — enable yield tracking so session end reports counts.
            _trackingExposureYield = true;

            var currentStatuses = new Dictionary<int, string>(items.Count);

            foreach (var item in items)
            {
                if (item is null) continue;
                var itemType = item.GetType();

                // Only track LIGHT frame exposures; skip calibration frames.
                var imageType = itemType.GetProperty("ImageType")?.GetValue(item)?.ToString();
                if (!string.Equals(imageType, "LIGHT", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Use object identity as a stable key across polls.
                var key = RuntimeHelpers.GetHashCode(item);
                var status = itemType.GetProperty("Status")?.GetValue(item)?.ToString() ?? string.Empty;
                currentStatuses[key] = status;

                // Detect transitions to SKIPPED or FAILED.
                if (_prevItemStatuses is not null
                    && _prevItemStatuses.TryGetValue(key, out var prevStatus)
                    && !string.Equals(prevStatus, status, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(status, "SKIPPED", StringComparison.OrdinalIgnoreCase))
                        IncrementSkippedExposures();
                    else if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
                        IncrementFailedExposures();
                }
            }

            _prevItemStatuses = currentStatuses;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] PollExposureYield error: {ex.Message}");
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
        StopYieldPollTimer();
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

                try
                {
                    var resolved = ActiveTargetResolver?.Invoke();
                    if (resolved is var (name, ra, dec) && !string.IsNullOrWhiteSpace(name))
                        await OnTargetDetectedAsync(name, ra, dec);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Subframes] Active target poll failed: {ex.Message}");
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
                    IsSafe         = ReadIsSafe(),
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

    // ── Session event consumers (autofocus + meridian flip) ──────────────────

    /// <summary>
    /// Register as a focuser consumer and subscribe to the AfterMeridianFlip event
    /// so we can send autofocus and meridian-flip events to the backend.
    /// Called at the start of each session.
    /// </summary>
    private void RegisterSessionEventConsumers()
    {
        try
        {
            _focuserMediator?.RegisterConsumer(this);
            Logger.Debug("[Subframes] Registered as IFocuserConsumer for autofocus events.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Could not register as focuser consumer: {ex.Message}");
        }

        try
        {
            if (_telescopeMediator is not null)
                _telescopeMediator.AfterMeridianFlip += OnAfterMeridianFlip;
            Logger.Debug("[Subframes] Subscribed to AfterMeridianFlip event.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Could not subscribe to AfterMeridianFlip: {ex.Message}");
        }
    }

    /// <summary>
    /// Unregister focuser consumer and unsubscribe from AfterMeridianFlip.
    /// Called at the end of each session (or on clear/dispose).
    /// </summary>
    private void UnregisterSessionEventConsumers()
    {
        try { _focuserMediator?.RemoveConsumer(this); }
        catch { /* ignore — mediator may already be gone */ }

        try
        {
            if (_telescopeMediator is not null)
                _telescopeMediator.AfterMeridianFlip -= OnAfterMeridianFlip;
        }
        catch { /* ignore */ }
    }

    // ── IFocuserConsumer ─────────────────────────────────────────────────────

    /// <summary>
    /// Called by NINA after each completed autofocus run.
    ///
    /// Sends an "autofocus" event with filter, temperature, and focuser position
    /// to the backend. Note: AutoFocusInfo does not expose success status or
    /// resulting HFR — only the final position and ambient conditions are available.
    ///
    /// This method must not throw; any exception is caught and logged.
    /// </summary>
    public void UpdateEndAutoFocusRun(AutoFocusInfo autofocusInfo)
    {
        var sessionId = _activeSessionId;
        if (sessionId is null) return;

        try
        {
            var request = new EventRequest
            {
                SessionId = sessionId,
                EventType = "autofocus",
                Timestamp = DateTime.UtcNow.ToString("o"),
                Metadata  = new Dictionary<string, object?>
                {
                    ["filter"]      = autofocusInfo.Filter,
                    ["temperature"] = Finite(autofocusInfo.Temperature),
                    ["position"]    = autofocusInfo.Position,
                },
            };
            _ = _apiClient.PostEventAsync(request, CancellationToken.None);
            Logger.Debug($"[Subframes] Autofocus event queued: session={sessionId} filter={autofocusInfo.Filter} position={autofocusInfo.Position}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] UpdateEndAutoFocusRun failed: {ex.Message}");
        }
    }

    /// <summary>No-op — we don't need device info updates.</summary>
    public void UpdateDeviceInfo(FocuserInfo deviceInfo) { }

    /// <summary>No-op — we don't need user-focused position updates.</summary>
    public void UpdateUserFocused(FocuserInfo info) { }

    // ── Meridian flip handler ────────────────────────────────────────────────

    /// <summary>
    /// Called by NINA after a meridian flip completes (success or failure).
    /// Sends a "meridian_flip" event to the backend.
    /// </summary>
    private Task OnAfterMeridianFlip(object? sender, AfterMeridianFlipEventArgs e)
    {
        var sessionId = _activeSessionId;
        if (sessionId is null) return Task.CompletedTask;

        try
        {
            var request = new EventRequest
            {
                SessionId = sessionId,
                EventType = "meridian_flip",
                Timestamp = DateTime.UtcNow.ToString("o"),
                Metadata  = new Dictionary<string, object?> { ["success"] = e.Success },
            };
            _ = _apiClient.PostEventAsync(request, CancellationToken.None);
            Logger.Debug($"[Subframes] Meridian flip event queued: session={sessionId} success={e.Success}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] OnAfterMeridianFlip failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopHeartbeatTimer();
        UnregisterSessionEventConsumers();
        _imageSaveMediator.ImageSaved -= OnImageSaved;
        _apiClient.Dispose();
        Logger.Debug("[Subframes] SessionService disposed.");
    }
}
