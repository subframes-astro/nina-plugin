using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;

namespace Subframes.NinaPlugin.Guiding;

/// <summary>
/// Captures PHD2 guide steps from NINA and queues them for batch upload.
///
/// <para>
/// Architecture: this class plays two distinct NINA roles simultaneously.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="IGuiderConsumer"/> — registered with <see cref="IGuiderMediator.RegisterConsumer"/>
///       so NINA calls <see cref="UpdateDeviceInfo"/> on every guider-state change. We use
///       this only to track the latest <see cref="GuiderInfo.PixelScale"/> value; no guide
///       step data flows through here in NINA SDK 3.2+.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="IGuiderMediator.GuideEvent"/> subscriber — PHD2 fires
///       <c>EventHandler&lt;IGuideStep&gt;</c> on every corrected guide frame (~1 Hz).
///       <see cref="OnGuideEvent"/> handles this, converts pixel errors to arcseconds using
///       the cached pixel scale, and enqueues the sample for the batch uploader.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// PixelScale == 0 policy: if the pixel scale has not been reported yet, the sample is
/// dropped and a single per-session warning is logged. Storing raw pixel values labelled
/// as arcseconds would corrupt the guide graph (wrong units). A gap is preferable.
/// </para>
///
/// Lifecycle: call <see cref="StartAsync"/> once on plugin init, <see cref="StopAsync"/>
/// on plugin teardown or session end. Internally owns and manages
/// <see cref="GuideSampleBatchUploader"/>.
/// </summary>
public sealed class GuideSampleCollector : IGuiderConsumer, IDisposable, IAsyncDisposable
{
    private readonly IGuiderMediator _guiderMediator;
    private readonly GuideSampleBatchUploader _uploader;

    private bool _isRegistered;
    private bool _disposed;
    private bool _pixelScaleWarningLogged;

    /// <summary>
    /// Latest pixel scale (arcsec/pixel) received via <see cref="UpdateDeviceInfo"/>.
    /// 0 means not yet reported by PHD2.
    /// </summary>
    private double _cachedPixelScale;

    public GuideSampleCollector(
        IGuiderMediator guiderMediator,
        ISessionContext sessionContext,
        IGuideSamplesApi apiClient)
    {
        _guiderMediator = guiderMediator;
        _uploader = new GuideSampleBatchUploader(apiClient, sessionContext);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Start accepting guide steps and flush the batch on a timer.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isRegistered) return;

        // Subscribe to the guide-step event BEFORE registering as a consumer so we
        // never miss a frame that arrives while the consumer registration is in flight.
        _guiderMediator.GuideEvent += OnGuideEvent;
        _guiderMediator.RegisterConsumer(this);
        _isRegistered = true;
        SubframesLogger.Info("GuideSampleCollector registered with guider mediator");

        await _uploader.StartAsync(cancellationToken);
    }

    /// <summary>Flush the remaining buffer and unregister from the mediator.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_isRegistered) return;

        // Flush before unregistering to avoid losing samples from an in-progress session.
        await _uploader.FlushAsync(cancellationToken);

        _guiderMediator.GuideEvent -= OnGuideEvent;
        _guiderMediator.RemoveConsumer(this);
        _isRegistered = false;
        await _uploader.StopAsync(cancellationToken);
        SubframesLogger.Info("GuideSampleCollector unregistered from guider mediator");
    }

    // -------------------------------------------------------------------------
    // IGuiderConsumer — used only to track pixel scale
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by NINA whenever the guider device state changes.
    /// In NINA SDK 3.2+, <see cref="GuiderInfo"/> no longer carries the individual
    /// guide-step data (that now flows via <see cref="IGuiderMediator.GuideEvent"/>).
    /// We only use this callback to keep <see cref="_cachedPixelScale"/> up to date.
    /// </summary>
    public void UpdateDeviceInfo(GuiderInfo guiderInfo)
    {
        try
        {
            // Cache the pixel scale whenever NINA reports it.  PHD2 sends this once it
            // has completed its calibration sequence; until then it may be 0.
            if (guiderInfo.PixelScale > 0)
            {
                _cachedPixelScale = guiderInfo.PixelScale;
            }
        }
        catch (Exception ex)
        {
            SubframesLogger.Warning($"Error caching pixel scale from GuiderInfo: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // GuideEvent handler — actual guide-step capture
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fired by <see cref="IGuiderMediator.GuideEvent"/> once per corrected frame
    /// (~1 Hz in typical PHD2 operation).
    /// </summary>
    private void OnGuideEvent(object? sender, IGuideStep guideStep)
    {
        try
        {
            var pixelScale = _cachedPixelScale;

            if (pixelScale <= 0)
            {
                // Pixel scale not yet available — drop the sample with a one-time warning.
                LogPixelScaleWarningOnce();
                return;
            }

            // RADistanceRaw / DECDistanceRaw are signed pixel offsets (PHD2 convention).
            // Multiply by pixel scale (arcsec/pixel) to get arcsecond errors.
            double raArcsec  = guideStep.RADistanceRaw  * pixelScale;
            double decArcsec = guideStep.DECDistanceRaw * pixelScale;

            var mapped = MappedGuideStep.FromArcseconds(
                DateTimeOffset.UtcNow,
                raArcsec,
                decArcsec);

            _uploader.Enqueue(mapped);
        }
        catch (Exception ex)
        {
            // Never propagate exceptions back to NINA's event dispatch.
            SubframesLogger.Warning($"Error processing guide step; sample dropped: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable / IAsyncDisposable
    // -------------------------------------------------------------------------

    /// <summary>
    /// Synchronous disposal — required by <see cref="IGuiderConsumer"/> (which extends
    /// <see cref="IDisposable"/>). Performs a best-effort flush with a 5-second timeout.
    /// Prefer <see cref="DisposeAsync"/> where possible for a clean async flush.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); }
        catch { /* StopAsync already logs; swallow here to satisfy Dispose contract */ }

        try { _uploader.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch { /* squelch */ }
    }

    /// <summary>
    /// Asynchronous disposal.  Accepts a <paramref name="cancellationToken"/> so the
    /// caller (typically <see cref="SubframesPlugin.Teardown"/>) can enforce a teardown
    /// time budget and avoid blocking NINA's shutdown loop indefinitely.
    /// Any pending samples that cannot be flushed within the budget are dead-lettered
    /// for replay on next launch — they are never silently dropped.
    /// </summary>
    public async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync(cancellationToken);
        await _uploader.DisposeAsync(cancellationToken);
    }

    /// <summary>
    /// Parameterless overload required by <see cref="IAsyncDisposable"/>.  Delegates
    /// to <see cref="DisposeAsync(CancellationToken)"/> with no time limit.
    /// Prefer the overload that accepts a token for teardown paths.
    /// </summary>
    public ValueTask DisposeAsync() => DisposeAsync(CancellationToken.None);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void LogPixelScaleWarningOnce()
    {
        if (_pixelScaleWarningLogged) return;
        _pixelScaleWarningLogged = true;
        SubframesLogger.Warning(
            "PHD2 pixel scale unavailable — guide samples are being dropped " +
            "rather than stored with incorrect units. Configure pixel scale in PHD2 for " +
            "accurate guide graphs. This warning will not repeat this session.");
    }
}
