using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;

namespace Subframes.NinaPlugin.Guiding;

/// <summary>
/// Subscribes to NINA's guider mediator, buffers PHD2 guide-step events at
/// approximately 1 Hz, and delegates batch uploads to <see cref="GuideSampleBatchUploader"/>.
///
/// Lifecycle: started once during plugin initialization, stopped on plugin teardown
/// and session end. Internally creates and manages the <see cref="GuideSampleBatchUploader"/>.
///
/// PHD2 pixel scale handling: if the pixel scale is unavailable, samples are dropped and
/// a single per-session warning is emitted. Storing raw pixel values labelled as arcseconds
/// would produce wrong-unit data in the guide graph, which is worse than a gap.
/// </summary>
public sealed class GuideSampleCollector : IGuiderConsumer, IDisposable, IAsyncDisposable
{
    private readonly IGuiderMediator _guiderMediator;
    private readonly GuideSampleBatchUploader _uploader;

    private bool _isRegistered;
    private bool _disposed;
    private bool _pixelScaleWarningLogged;

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

        _guiderMediator.RemoveConsumer(this);
        _isRegistered = false;
        await _uploader.StopAsync(cancellationToken);
        SubframesLogger.Info("GuideSampleCollector unregistered from guider mediator");
    }

    // -------------------------------------------------------------------------
    // IGuiderConsumer
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by NINA on every guide step device update (~1 Hz in typical PHD2 operation).
    /// We only collect when a Subframes session is active and a valid guide step is present.
    /// </summary>
    /// <remarks>
    /// NINA SDK 3.2+ renamed UpdateGuider() to UpdateDeviceInfo(GuiderInfo).
    /// </remarks>
    public void UpdateDeviceInfo(GuiderInfo guiderInfo)
    {
        try
        {
            // Guard: must actually be connected with a valid guide step.
            if (!guiderInfo.Connected) return;
            if (guiderInfo.GuideStep is not { } step) return;

            // PHD2 exposes pixel errors; convert to arcseconds using the pixel scale.
            // If the pixel scale is unavailable we drop the sample: storing raw pixel
            // values labelled as arcseconds produces wrong-unit data in the guide graph,
            // which is worse than a gap. A one-time warning is emitted so the user
            // knows to configure the pixel scale in PHD2.
            double raArcsec, decArcsec;

            if (guiderInfo.PixelScale is double pixelScale && pixelScale > 0)
            {
                // RaDistanceRaw / DecDistanceRaw are signed pixel offsets in PHD2.
                raArcsec = step.RaDistanceRaw * pixelScale;
                decArcsec = step.DecDistanceRaw * pixelScale;
            }
            else
            {
                LogPixelScaleWarningOnce();
                return;
            }

            var mapped = MappedGuideStep.FromArcseconds(
                DateTimeOffset.UtcNow,
                raArcsec,
                decArcsec);

            _uploader.Enqueue(mapped);
        }
        catch (Exception ex)
        {
            // Never propagate exceptions back to NINA's mediator dispatch.
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync(CancellationToken.None);
        await _uploader.DisposeAsync();
    }

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
