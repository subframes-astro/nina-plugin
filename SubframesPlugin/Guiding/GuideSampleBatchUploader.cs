using System.Collections.Concurrent;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin.Guiding;

/// <summary>
/// Accumulates <see cref="MappedGuideStep"/> samples in a thread-safe queue and
/// flushes them to the Subframes API in batches.
///
/// Flush triggers:
///   • Periodic timer (default: 60 seconds, configurable 30–120 s).
///   • Explicit <see cref="FlushAsync"/> call (session end / plugin teardown).
///   • Batch size ceiling (1 000 samples — API limit).
///
/// Retry policy: exponential back-off (1 s → 2 s → 4 s → … up to <see cref="MaxBackoffCeiling"/>)
/// for transient HTTP errors. On 429 (rate limited) the <c>Retry-After</c> header delay is
/// used instead (or exponential back-off if the header is absent).
/// After <see cref="MaxRetryAttempts"/> retries the batch is written to the dead-letter store
/// (or logged as an error if no store is configured) — data is NEVER silently dropped.
/// Permanent failures (4xx other than 429) are dead-lettered immediately.
/// </summary>
public sealed class GuideSampleBatchUploader : IAsyncDisposable
{
    // -------------------------------------------------------------------------
    // Constants / defaults
    // -------------------------------------------------------------------------

    /// <summary>Maximum samples per API batch (matches server-side cap).</summary>
    public const int MaxBatchSize = 1_000;

    /// <summary>Default interval between automatic flushes.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(60);

    /// <summary>Default maximum back-off delay before giving up on a transient failure.</summary>
    public static readonly TimeSpan DefaultMaxBackoffCeiling = TimeSpan.FromSeconds(60);

    /// <summary>Default maximum number of retry attempts per batch.</summary>
    public const int DefaultMaxRetryAttempts = 5;

    // -------------------------------------------------------------------------
    // Configurable retry params (settable via constructor)
    // -------------------------------------------------------------------------

    /// <summary>Maximum back-off ceiling (configurable; default 60 s).</summary>
    private readonly TimeSpan _maxBackoffCeiling;

    /// <summary>Maximum retry attempts per batch (configurable; default 5).</summary>
    private readonly int _maxRetryAttempts;

    // Expose for tests
    internal TimeSpan MaxBackoffCeiling => _maxBackoffCeiling;
    internal int MaxRetryAttempts => _maxRetryAttempts;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private readonly IGuideSamplesApi _apiClient;
    private readonly ISessionContext _sessionContext;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _initialRetryDelay;
    private readonly DeadLetterStore? _deadLetterStore;
    private readonly ConcurrentQueue<MappedGuideStep> _queue = new();

    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;

    public GuideSampleBatchUploader(
        IGuideSamplesApi apiClient,
        ISessionContext sessionContext,
        TimeSpan? flushInterval = null,
        TimeSpan? initialRetryDelay = null,
        int? maxRetryAttempts = null,
        TimeSpan? maxBackoffCeiling = null,
        DeadLetterStore? deadLetterStore = null)
    {
        _apiClient = apiClient;
        _sessionContext = sessionContext;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _initialRetryDelay = initialRetryDelay ?? TimeSpan.FromSeconds(1);
        _maxRetryAttempts = maxRetryAttempts ?? DefaultMaxRetryAttempts;
        _maxBackoffCeiling = maxBackoffCeiling ?? DefaultMaxBackoffCeiling;
        _deadLetterStore = deadLetterStore;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timerTask = RunFlushLoopAsync(_timerCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timerCts is null) return;

        await _timerCts.CancelAsync();

        try { await (_timerTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { /* expected */ }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Enqueue a single guide step for the next batch.</summary>
    internal void Enqueue(MappedGuideStep step) => _queue.Enqueue(step);

    /// <summary>
    /// Drain the queue and upload all pending samples immediately.
    /// Called on session end or plugin teardown.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_queue.IsEmpty) return;
        if (!_sessionContext.HasActiveSession)
        {
            // No session — discard; nothing meaningful to upload.
            while (_queue.TryDequeue(out _)) { }
            return;
        }

        await UploadPendingAsync(_sessionContext.ActiveSessionId!, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        _timerCts?.Dispose();
    }

    /// <summary>Parameterless overload required by <see cref="IAsyncDisposable"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    private async Task RunFlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_flushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_queue.IsEmpty) continue;
                if (!_sessionContext.HasActiveSession)
                {
                    // Session ended without flushing — discard stale samples.
                    while (_queue.TryDequeue(out _)) { }
                    continue;
                }

                try
                {
                    await UploadPendingAsync(_sessionContext.ActiveSessionId!, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log but never crash the loop — imaging session must continue.
                    SubframesLogger.Warning(
                        $"Guide sample flush failed unexpectedly; will retry next tick: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Drain up to <see cref="MaxBatchSize"/> samples from the queue and upload them.
    /// Retries transient (5xx / network) and rate-limited (429) errors with back-off.
    /// On permanent failure or retry exhaustion, the batch is dead-lettered.
    /// </summary>
    private async Task UploadPendingAsync(string sessionId, CancellationToken ct)
    {
        // Drain up to MaxBatchSize items.
        var batch = new List<MappedGuideStep>(Math.Min(MaxBatchSize, _queue.Count + 1));

        while (batch.Count < MaxBatchSize && _queue.TryDequeue(out var step))
            batch.Add(step);

        if (batch.Count == 0) return;

        SubframesLogger.Debug(
            $"Uploading batch of {batch.Count} guide samples for session {sessionId}");

        var request = new GuideSampleBatchRequest
        {
            SessionId = sessionId,
            Samples = batch.Select(s => s.ToGuideSample()).ToList()
        };

        try
        {
            var (success, finalAttempts) = await UploadWithRetryAsync(request, ct);

            if (!success)
            {
                SubframesLogger.Error(
                    $"Exhausted {finalAttempts} retry attempts for {batch.Count} guide samples " +
                    $"(session {sessionId}); moving to dead-letter store");

                DeadLetter("ingest/guide-samples", request, finalAttempts);
            }
            else
            {
                SubframesLogger.Debug(
                    $"Successfully uploaded {batch.Count} guide samples for session {sessionId}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Fix C (SUB-2051): teardown was cancelled before the upload completed.
            // Preserve the batch in the dead-letter store so samples are replayed on
            // next launch rather than silently dropped.
            SubframesLogger.Info(
                $"Guide sample flush aborted at teardown; dead-lettering {batch.Count} samples " +
                "for retry on next launch.");
            DeadLetter("ingest/guide-samples", request, attempts: 0); // 0 = cancelled before any response received
            throw;   // Let StopAsync unwind cleanly
        }
    }

    /// <summary>
    /// Upload with exponential back-off and 429-aware retry.
    /// Returns <c>(true, attempts)</c> on success, <c>(false, totalAttempts)</c> when all retries
    /// are exhausted. Permanent failures are dead-lettered immediately and return <c>false</c>.
    /// </summary>
    private async Task<(bool success, int attempts)> UploadWithRetryAsync(
        GuideSampleBatchRequest request,
        CancellationToken ct)
    {
        const string endpoint = "ingest/guide-samples";
        var delay = _initialRetryDelay;

        for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _apiClient.PostGuideSamplesAsync(request, ct);

            if (result.IsSuccess) return (true, attempt);

            // 429 — rate limited: honour Retry-After or fall back to exponential back-off
            if (result.IsRateLimited)
            {
                if (attempt == _maxRetryAttempts) break;

                var retryDelay = result.RetryAfter.HasValue
                    ? TimeSpan.FromTicks(Math.Min(result.RetryAfter.Value.Ticks, _maxBackoffCeiling.Ticks))
                    : delay;

                SubframesLogger.Warning(
                    $"{endpoint} rate limited (429) — " +
                    $"RetryAfterHeader={RetryAfterStr(result.RetryAfter)} DelayApplied={retryDelay.TotalSeconds}s " +
                    $"attempt={attempt}/{_maxRetryAttempts} queueDepth={_queue.Count}");

                await Task.Delay(retryDelay, ct);

                // Advance exponential delay for subsequent attempts (if no Retry-After)
                if (!result.RetryAfter.HasValue)
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxBackoffCeiling.Ticks));

                continue;
            }

            // Permanent 4xx — dead-letter immediately, no point retrying
            if (result.IsPermanentFailure)
            {
                SubframesLogger.Warning(
                    $"{endpoint} rejected permanently (HTTP {result.StatusCode}) — " +
                    $"dead-lettering batch (attempt {attempt}/{_maxRetryAttempts})");
                DeadLetter(endpoint, request, attempt);
                return (false, attempt);
            }

            // Transient failure (5xx, network timeout, etc.)
            if (attempt == _maxRetryAttempts) break;

            SubframesLogger.Warning(
                $"{endpoint} transient failure (HTTP {result.StatusCode}) — " +
                $"retrying in {delay.TotalSeconds}s attempt={attempt}/{_maxRetryAttempts} queueDepth={_queue.Count}");

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxBackoffCeiling.Ticks));
        }

        return (false, _maxRetryAttempts);
    }

    /// <summary>
    /// Writes a failed batch to the dead-letter store (SQLite) if configured,
    /// or logs a structured error if the store is unavailable.
    /// </summary>
    private void DeadLetter<T>(string endpoint, T payload, int attempts)
    {
        if (_deadLetterStore is not null)
        {
            _deadLetterStore.Write(endpoint, payload, attempts);
        }
        else
        {
            SubframesLogger.Error(
                $"No dead-letter store configured — " +
                $"batch for {endpoint} permanently dropped after {attempts} attempts. " +
                "Configure DeadLetterStore to preserve failed payloads.");
        }
    }

    private static string RetryAfterStr(TimeSpan? t) =>
        t.HasValue ? $"{t.Value.TotalSeconds}s" : "(none)";
}
