using System.Text.Json;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;
using Subframes.NinaPlugin;

namespace Subframes.NinaPlugin.Data;

/// <summary>
/// Background engine that syncs cached frames to the Subframes API.
/// Runs on a configurable timer (default 30s), batches up to 50 frames per session,
/// and handles retries with graceful degradation.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly FrameCache _cache;
    private readonly SubframesClient _apiClient;
    private readonly PluginOptions _options;

    private CancellationTokenSource? _cts;
    private Task? _syncTask;

    public SyncEngine(FrameCache cache, SubframesClient apiClient, PluginOptions options)
    {
        _cache = cache;
        _apiClient = apiClient;
        _options = options;
    }

    /// <summary>Start the background sync loop.</summary>
    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _syncTask = RunSyncLoopAsync(cts.Token);
        SubframesLogger.Info("SyncEngine started.");
    }

    /// <summary>Stop the background sync loop.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _syncTask = null;
    }

    /// <summary>
    /// Run a single sync pass immediately. Useful for flushing before session end.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await SyncPendingFramesAsync(ct);
    }

    private async Task RunSyncLoopAsync(CancellationToken ct)
    {
        // Small initial delay to let the plugin finish startup.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }

        var intervalSeconds = _options.CacheSyncIntervalSeconds > 0
            ? _options.CacheSyncIntervalSeconds
            : 30;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await SyncPendingFramesAsync(ct);
                _cache.PruneSynced(_options.CacheRetentionHours > 0
                    ? _options.CacheRetentionHours
                    : 72);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            SubframesLogger.Error($"SyncEngine loop terminated unexpectedly: {ex.Message}");
        }
    }

    private async Task SyncPendingFramesAsync(CancellationToken ct)
    {
        if (!_options.IsEnabled) return;

        try
        {
            var pending = _cache.GetPendingFrames(50);
            if (pending.Count == 0) return;

            SubframesLogger.Debug($"SyncEngine: {pending.Count} pending frames to sync");

            // Group by session for batch upload.
            var bySession = pending
                .GroupBy(f => f.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (sessionId, frames) in bySession)
            {
                if (ct.IsCancellationRequested) break;

                var frameInputs = new List<FrameInput>();
                var frameIds = new List<long>();

                foreach (var (id, _, json) in frames)
                {
                    try
                    {
                        var frame = JsonSerializer.Deserialize<FrameInput>(json, JsonOptions);
                        if (frame is not null)
                        {
                            frameInputs.Add(frame);
                            frameIds.Add(id);
                        }
                    }
                    catch (JsonException ex)
                    {
                        SubframesLogger.Warning($"SyncEngine: corrupt frame json id={id}: {ex.Message}");
                        _cache.MarkFailed([id], $"JSON parse error: {ex.Message}");
                    }
                }

                if (frameInputs.Count == 0) continue;

                try
                {
                    var result = await _apiClient.IngestFramesAsync(sessionId, frameInputs, ct);
                    if (result is not null)
                    {
                        _cache.MarkSynced(frameIds);
                        SubframesLogger.Info($"SyncEngine: synced {frameIds.Count} frames for session {sessionId} (accepted={result.Accepted})");
                    }
                    else
                    {
                        _cache.MarkFailed(frameIds, "API returned null — request failed or plugin disabled");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _cache.MarkFailed(frameIds, ex.Message);
                    SubframesLogger.Warning($"SyncEngine: batch sync failed for session {sessionId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            SubframesLogger.Error($"SyncEngine: unexpected error during sync pass: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}
