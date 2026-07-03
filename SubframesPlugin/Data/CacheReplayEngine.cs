using System.Text.Json;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;
using Subframes.NinaPlugin;

namespace Subframes.NinaPlugin.Data;

/// <summary>
/// Replays offline-cached sessions to the Subframes API after connectivity is restored.
///
/// Replay order per session (oldest first):
///   1. StartSession (if server_ack == false), using idempotency key for safety.
///   2. StartSessionTarget for each unacked target.
///   3. IngestFrames in batches (up to ReplayFramesPerBatch frames each).
///   4. IngestEvent for each cached event.
///   5. EndSession if the session was ended locally.
///   6. Mark session fully synced.
///
/// A token-bucket rate limiter caps outbound calls to avoid overwhelming the server
/// (default: 10 req/s sustained, burst 20).  On HTTP 429/503 the limiter backs off
/// exponentially and temporarily halves the refill rate.
///
/// Live-session priority: replay is auto-paused while a live imaging session is open
/// and resumes once it ends.
/// </summary>
public sealed class CacheReplayEngine : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly FrameCache _cache;
    private readonly SubframesClient _apiClient;
    private readonly PluginOptions _options;
    private readonly Func<bool> _hasLiveSession; // returns true when a live session is active

    private CancellationTokenSource? _cts;
    private Task? _replayTask;

    // Progress tracking (thread-safe for UI reads)
    private volatile int _sessionsTotal;
    private volatile int _sessionsDone;
    private volatile string _replayStatus = string.Empty;

    // Live-session pause gate
    private volatile bool _paused;

    // Token bucket state (access only from replay task thread)
    private double _tokens;
    private DateTime _lastRefill = DateTime.UtcNow;
    private double _currentRefillRate; // tokens per second

    public CacheReplayEngine(
        FrameCache cache,
        SubframesClient apiClient,
        PluginOptions options,
        Func<bool> hasLiveSession)
    {
        _cache = cache;
        _apiClient = apiClient;
        _options = options;
        _hasLiveSession = hasLiveSession;
        _tokens = options.ReplayBurstCapacity > 0 ? options.ReplayBurstCapacity : 20;
        _currentRefillRate = options.ReplayRateReqPerSec > 0 ? options.ReplayRateReqPerSec : 10;
    }

    // ── Public surface ───────────────────────────────────────────────────────

    /// <summary>Human-readable replay progress, e.g. "Syncing 3/12 sessions, ~4 min remaining".</summary>
    public string ReplayStatus => _replayStatus;

    /// <summary>True when the replay loop is running.</summary>
    public bool IsRunning => _replayTask is { IsCompleted: false };

    /// <summary>
    /// Pause replay because a live session started.
    /// Any in-flight inter-session delay is interrupted cleanly.
    /// </summary>
    public void PauseForLiveSession()
    {
        _paused = true;
        SubframesLogger.Info("CacheReplayEngine: paused for live session.");
    }

    /// <summary>Resume replay after the live session ended.</summary>
    public void ResumeAfterLiveSession()
    {
        _paused = false;
        SubframesLogger.Info("CacheReplayEngine: resumed after live session.");
    }

    /// <summary>Start the background replay loop (no-op if already running).</summary>
    public void Start()
    {
        if (_replayTask is { IsCompleted: false }) return;
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _replayTask = RunReplayLoopAsync(cts.Token);
        SubframesLogger.Info("CacheReplayEngine started.");
    }

    /// <summary>Stop the background replay loop.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _replayTask = null;
        _replayStatus = string.Empty;
    }

    // ── Replay loop ──────────────────────────────────────────────────────────

    private async Task RunReplayLoopAsync(CancellationToken ct)
    {
        // Initial delay: let the plugin finish startup and attempt live connections first.
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            // Run once immediately, then on 60-second intervals.
            await RunOneReplayPassAsync(ct);
            while (await timer.WaitForNextTickAsync(ct))
                await RunOneReplayPassAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SubframesLogger.Error($"CacheReplayEngine loop terminated: {ex.Message}");
        }
    }

    private async Task RunOneReplayPassAsync(CancellationToken ct)
    {
        if (!_options.IsEnabled) return;

        var sessions = _cache.GetPendingReplaySessions();
        if (sessions.Count == 0)
        {
            _replayStatus = string.Empty;
            return;
        }

        _sessionsTotal = sessions.Count;
        _sessionsDone  = 0;

        SubframesLogger.Info($"CacheReplayEngine: {sessions.Count} session(s) pending replay.");

        for (int i = 0; i < sessions.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            // Pause while a live session is active.
            while (_paused || _hasLiveSession())
            {
                _replayStatus = "Replay paused: live session in progress";
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
            }

            var session = sessions[i];
            var remaining = sessions.Count - i;
            _replayStatus = $"Syncing {i + 1}/{sessions.Count} sessions…";

            await ReplaySessionAsync(session, ct);

            _sessionsDone = i + 1;

            // Inter-session delay to avoid back-to-back bursts.
            if (i < sessions.Count - 1)
            {
                var delay = _options.ReplayInterSessionDelaySeconds > 0
                    ? _options.ReplayInterSessionDelaySeconds : 2;
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        var unsync = _cache.GetPendingReplaySessions().Count;
        _replayStatus = unsync == 0
            ? string.Empty
            : $"{unsync} session(s) still pending sync";
    }

    /// <summary>
    /// Replay one session end-to-end:
    /// StartSession → Targets → Frames → Events → EndSession → mark synced.
    /// </summary>
    private async Task ReplaySessionAsync(CachedSessionRecord session, CancellationToken ct)
    {
        SubframesLogger.Info($"Replaying session local={session.LocalId} ack={session.ServerAck}");

        // 1. Start session on server if not yet acked.
        string serverId;
        if (!session.ServerAck)
        {
            var startRequest = DeserializeOrNull<StartSessionRequest>(session.StartJson);
            if (startRequest is null)
            {
                SubframesLogger.Warning($"Replay: corrupt StartSession JSON for local={session.LocalId} — skipping.");
                _cache.MarkSessionReplayed(session.LocalId);
                return;
            }

            // Attach idempotency key so a crashed replay attempt is safe to retry.
            startRequest = startRequest with { IdempotencyKey = session.IdempotencyKey };

            await AcquireTokenAsync(ct);
            var newServerId = await _apiClient.StartSessionAsync(startRequest, ct);

            if (newServerId is null)
            {
                SubframesLogger.Warning($"Replay: StartSession failed for local={session.LocalId} — will retry next pass.");
                return;
            }

            _cache.MarkSessionAcked(session.LocalId, newServerId);
            serverId = newServerId;
            SubframesLogger.Info($"Replay: session started server={serverId}");
        }
        else
        {
            serverId = session.ServerId!;
        }

        // 2. Replay unacked targets.
        var targets = _cache.GetTargetsForSession(session.LocalId);
        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested) return;
            if (target.ServerAck) continue;

            var req = DeserializeOrNull<StartSessionTargetRequest>(target.StartJson);
            if (req is null) continue;

            // Bind to the now-known server session ID.
            req = req with { SessionId = serverId };

            await AcquireTokenAsync(ct);
            var targetServerId = await _apiClient.StartTargetAsync(req, ct);

            if (targetServerId is not null)
            {
                _cache.MarkTargetAcked(target.LocalId, targetServerId);

                // Send EndTarget if the target was closed locally.
                if (target.EndedLocally && target.EndTime is not null)
                {
                    await AcquireTokenAsync(ct);
                    await _apiClient.EndTargetAsync(new EndSessionTargetRequest
                    {
                        SessionId       = serverId,
                        SessionTargetId = targetServerId,
                        EndTime         = target.EndTime,
                    }, ct);
                }
            }
        }

        // 3. Replay frames in batches.
        // After MarkSessionAcked the frames now have session_id = serverId,
        // so the normal SyncEngine picks them up.  But during the replay pass
        // we drive them directly to ensure ordering before EndSession.
        int batchSize  = _options.ReplayFramesPerBatch > 0 ? _options.ReplayFramesPerBatch : 50;
        int framesDone = 0;
        while (true)
        {
            if (ct.IsCancellationRequested) return;

            var pending = _cache.GetPendingFrames(batchSize);
            // Filter to only this session.
            var batch = pending.Where(f => f.SessionId == serverId).ToList();
            if (batch.Count == 0) break;

            var frameInputs = new List<FrameInput>(batch.Count);
            var frameIds    = new List<long>(batch.Count);
            foreach (var (id, _, json) in batch)
            {
                var frame = DeserializeOrNull<FrameInput>(json);
                if (frame is not null) { frameInputs.Add(frame); frameIds.Add(id); }
                else _cache.MarkFailed([id], "JSON parse error during replay");
            }

            if (frameInputs.Count > 0)
            {
                await AcquireTokenAsync(ct);
                bool backoff = false;
                try
                {
                    var result = await _apiClient.IngestFramesAsync(serverId, frameInputs, ct);
                    if (result is not null)
                    {
                        _cache.MarkSynced(frameIds);
                        framesDone += frameIds.Count;
                    }
                    else
                    {
                        _cache.MarkFailed(frameIds, "IngestFrames returned null during replay");
                        backoff = true;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _cache.MarkFailed(frameIds, ex.Message);
                    SubframesLogger.Warning($"Replay IngestFrames failed: {ex.Message}");
                    backoff = true;
                }

                if (backoff)
                {
                    // Temporarily halve refill rate; restore after 60 s without errors.
                    _ = RestoreRefillRateAfterDelayAsync();
                    return; // Will retry next pass.
                }
            }
        }

        SubframesLogger.Info($"Replay: {framesDone} frame(s) uploaded for session={serverId}");

        // 4. Replay cached events.
        var events = _cache.GetPendingEventsForSession(session.LocalId);
        var syncedEventIds = new List<long>(events.Count);
        foreach (var (evId, evJson) in events)
        {
            if (ct.IsCancellationRequested) return;
            var evReq = DeserializeOrNull<EventRequest>(evJson);
            if (evReq is null) { syncedEventIds.Add(evId); continue; }

            // Patch the session ID to the server-assigned one.
            evReq = new EventRequest
            {
                SessionId = serverId,
                EventType = evReq.EventType,
                Timestamp = evReq.Timestamp,
                Metadata  = evReq.Metadata,
            };

            await AcquireTokenAsync(ct);
            await _apiClient.PostEventAsync(evReq, ct);
            syncedEventIds.Add(evId);
        }
        _cache.MarkEventsSynced(syncedEventIds);

        // 5. End session on server if it was ended locally.
        if (session.EndedLocally && session.EndTime is not null)
        {
            await AcquireTokenAsync(ct);
            await _apiClient.EndSessionAsync(
                serverId,
                session.SkippedExposures,
                session.FailedExposures,
                ct);
        }

        // 6. Mark session fully synced.
        _cache.MarkSessionReplayed(session.LocalId);
        SubframesLogger.Info($"Replay complete for session={serverId}");
    }

    // ── Token bucket rate limiter ────────────────────────────────────────────

    /// <summary>
    /// Wait until a token is available, then consume it.
    /// Refills tokens at <see cref="PluginOptions.ReplayRateReqPerSec"/> per second
    /// up to burst capacity.
    /// </summary>
    private async Task AcquireTokenAsync(CancellationToken ct)
    {
        while (true)
        {
            RefillTokens();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return;
            }

            // Wait ~100 ms and retry — fine-grained enough at 10 req/s.
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { throw; }
        }
    }

    private void RefillTokens()
    {
        var now    = DateTime.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;
        _lastRefill = now;

        var refill = _currentRefillRate > 0 ? _currentRefillRate : 10.0;
        var burst   = _options.ReplayBurstCapacity > 0 ? (double)_options.ReplayBurstCapacity : 20.0;
        _tokens = Math.Min(burst, _tokens + elapsed * refill);
    }

    /// <summary>
    /// On 429/5xx: halve the refill rate; restore after 60 s.
    /// Fire-and-forget from the replay task.
    /// </summary>
    private async Task RestoreRefillRateAfterDelayAsync()
    {
        var normal = _options.ReplayRateReqPerSec > 0 ? (double)_options.ReplayRateReqPerSec : 10.0;
        _currentRefillRate = normal / 2.0;
        SubframesLogger.Info($"Replay: rate halved to {_currentRefillRate:F1} req/s after error. Restoring in 60 s.");
        await Task.Delay(TimeSpan.FromSeconds(60));
        _currentRefillRate = normal;
        SubframesLogger.Info($"Replay: refill rate restored to {_currentRefillRate:F1} req/s.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static T? DeserializeOrNull<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return null; }
    }

    public void Dispose() => Stop();
}
