using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using Subframes.NinaPlugin.Api;
using Subframes.NinaPlugin.Data;
using Subframes.NinaPlugin.UI;

namespace Subframes.NinaPlugin;

// Shared helper: replace NaN/±Infinity with null for clean JSON.
file static class DoubleExtensions
{
    internal static double? Finite(double? v) => v is double d && double.IsFinite(d) ? v : null;
}

/// <summary>
/// Main plugin entry point.  NINA discovers this via MEF ([Export(typeof(IPluginManifest))]).
/// Manifest properties (Name, Identifier, Author, etc.) are read from assembly attributes
/// by PluginBase — see SubframesPlugin.csproj and Properties/AssemblyInfo.cs.
/// </summary>
[Export(typeof(IPluginManifest))]
[Export(typeof(SubframesPlugin))]
[PartCreationPolicy(CreationPolicy.Shared)]
public class SubframesPlugin : PluginBase, IPluginManifest, IPartImportsSatisfiedNotification
{
    // Static primary-instance guard: NINA's MEF may create multiple instances
    // across separate CompositionContainers.  Only the first instance owns the
    // background services (SessionService, SyncEngine, station heartbeat).
    // Secondary instances proxy to the primary's services so sequence items
    // still work regardless of which MEF container resolved them.
    private static SubframesPlugin? _primary;

    private readonly SessionService _sessionService;
    private readonly OptionsPanelViewModel _optionsVm;
    private readonly SubframesClient _apiClient;
    private readonly PluginOptions _options;
    private readonly IProfileService _profileService;
    private readonly ICameraMediator _cameraMediator;
    private readonly ITelescopeMediator _telescopeMediator;
    private readonly IFocuserMediator _focuserMediator;
    private readonly IFilterWheelMediator _filterWheelMediator;
    private readonly IRotatorMediator _rotatorMediator;
    private readonly IGuiderMediator _guiderMediator;
    private readonly IFlatDeviceMediator _flatDeviceMediator;
    private readonly FrameCache _frameCache;
    private readonly SyncEngine _syncEngine;
    private readonly bool _isPrimary;

    // Optional import: ISequenceMediator may not be available in all NINA
    // versions or composition containers.  Subscribed in OnImportsSatisfied.
    [Import(AllowDefault = true)]
    public ISequenceMediator? SequenceMediator { get; set; }

    private bool _sequenceEventsSubscribed;
    // Stored so we can remove the reflection-added SequenceStarted handler in Teardown.
    private Delegate? _sequenceStartedDelegate;
    private CancellationTokenSource? _sequenceRetrySubscribeCts;
    private CancellationTokenSource? _stationHeartbeatCts;
    private Task? _stationHeartbeatTask;

    // DSO container subscription tracking: subscribed during each sequence run,
    // unsubscribed on SequenceFinished / Teardown.
    private readonly List<(INotifyPropertyChanged Container, PropertyChangedEventHandler Handler)> _containerSubscriptions = new();

    [ImportingConstructor]
    public SubframesPlugin(
        IImageSaveMediator imageSaveMediator,
        IProfileService profileService,
        ICameraMediator cameraMediator,
        ITelescopeMediator telescopeMediator,
        IFocuserMediator focuserMediator,
        IFilterWheelMediator filterWheelMediator,
        IRotatorMediator rotatorMediator,
        IGuiderMediator guiderMediator,
        IFlatDeviceMediator flatDeviceMediator)
    {
        // Atomically claim the primary slot.  If another instance already
        // exists, proxy to its services and skip all background work.
        var existing = Interlocked.CompareExchange(ref _primary, this, null);
        if (existing is not null)
        {
            Logger.Warning("[Subframes] Duplicate plugin instance created by MEF — proxying to primary to prevent duplicate sessions/sync.");
            _isPrimary = false;
            // Share the primary's service objects so sequence items work.
            _sessionService = existing._sessionService;
            _optionsVm = existing._optionsVm;
            _apiClient = existing._apiClient;
            _options = existing._options;
            _frameCache = existing._frameCache;
            _syncEngine = existing._syncEngine;
            _profileService = profileService;
            _cameraMediator = cameraMediator;
            _telescopeMediator = telescopeMediator;
            _focuserMediator = focuserMediator;
            _filterWheelMediator = filterWheelMediator;
            _rotatorMediator = rotatorMediator;
            _guiderMediator = guiderMediator;
            _flatDeviceMediator = flatDeviceMediator;
            return;
        }

        _isPrimary = true;
        _options = PluginOptions.Load();
        _apiClient = new SubframesClient(_options);
        _profileService = profileService;
        _cameraMediator = cameraMediator;
        _telescopeMediator = telescopeMediator;
        _focuserMediator = focuserMediator;
        _filterWheelMediator = filterWheelMediator;
        _rotatorMediator = rotatorMediator;
        _guiderMediator = guiderMediator;
        _flatDeviceMediator = flatDeviceMediator;
        _frameCache = new FrameCache();
        _syncEngine = new SyncEngine(_frameCache, _apiClient, _options);
        _sessionService = new SessionService(imageSaveMediator, _apiClient, _options, _frameCache, _syncEngine);
        _optionsVm = new OptionsPanelViewModel(this);

        if (_options.IsEnabled && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            StartStationHeartbeat();
            _syncEngine.Start();
        }

        var pending = _frameCache.GetPendingCount();
        if (pending > 0)
            Logger.Info($"[Subframes] {pending} cached frames pending sync from previous session.");

        Logger.Info("[Subframes] Plugin loaded (primary instance).");
    }

    // Expose singletons so MEF-constructed sequence items can import them.
    public SessionService SessionService => _sessionService;
    public OptionsPanelViewModel OptionsVM => _optionsVm;

    /// <summary>
    /// The shared plugin options instance. OptionsPanelViewModel uses this directly
    /// so that saved changes are immediately visible to SubframesClient.
    /// </summary>
    public PluginOptions Options => _options;

    /// <summary>
    /// Called by MEF after all imports (including optional ones) are satisfied.
    /// Subscribes to SequenceStarted and SequenceFinished if ISequenceMediator
    /// was resolved.  Because NINA's SequenceMediator is lazy (internals are
    /// null until RegisterSequenceNavigation is called later), the subscription
    /// may fail at this point.  If so, a background task retries every 2s for
    /// up to 60s.
    /// </summary>
    public void OnImportsSatisfied()
    {
        if (!_isPrimary || SequenceMediator is null)
            return;

        if (TrySubscribeSequenceEvents())
            return;

        // SequenceMediator exists but internals aren't ready yet — retry in background.
        Logger.Warning("[Subframes] Sequence event subscription deferred — SequenceMediator internals not yet initialized.");
        var cts = new CancellationTokenSource();
        _sequenceRetrySubscribeCts = cts;
        _ = RetrySubscribeSequenceEventsAsync(cts.Token);
    }

    /// <summary>
    /// Attempts to subscribe to SequenceStarted and SequenceFinished. Returns true on success.
    /// </summary>
    private bool TrySubscribeSequenceEvents()
    {
        try
        {
            SequenceMediator!.SequenceFinished += OnSequenceFinished;

            // SequenceStarted was added in NINA 3.1.3+.  Subscribe via reflection so the
            // plugin compiles and runs on SDK 3.1.2.9001 and degrades gracefully there.
            var seqStartedEvent = SequenceMediator.GetType().GetEvent("SequenceStarted");
            if (seqStartedEvent?.EventHandlerType is Type evtType)
            {
                _sequenceStartedDelegate = Delegate.CreateDelegate(evtType, this, nameof(OnSequenceStarted));
                seqStartedEvent.AddEventHandler(SequenceMediator, _sequenceStartedDelegate);
                Logger.Debug("[Subframes] Subscribed to ISequenceMediator.SequenceStarted (via reflection).");
            }
            else
            {
                Logger.Info("[Subframes] ISequenceMediator.SequenceStarted not present in this NINA build — session will open on first captured image instead.");
            }

            _sequenceEventsSubscribed = true;
            _sessionService.ActiveTargetResolver = ResolveActiveTarget;
            Logger.Debug("[Subframes] Subscribed to ISequenceMediator.SequenceFinished.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] Sequence event subscription attempt failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Retries sequence event subscription every 2 seconds, giving up after 60 seconds.
    /// </summary>
    private async Task RetrySubscribeSequenceEventsAsync(CancellationToken ct)
    {
        const int retryIntervalMs = 2_000;
        const int maxRetries = 30; // 30 × 2s = 60s

        for (int i = 0; i < maxRetries; i++)
        {
            try { await Task.Delay(retryIntervalMs, ct); }
            catch (OperationCanceledException) { return; }

            if (TrySubscribeSequenceEvents())
            {
                Logger.Info($"[Subframes] Deferred sequence event subscription succeeded after {(i + 1) * 2}s.");
                return;
            }
        }

        Logger.Warning("[Subframes] Gave up subscribing to sequence events after 60s — sessions will not auto-open/close on sequence start/end.");
    }

    /// <summary>
    /// Called by OptionsPanelViewModel after saving settings.
    /// Starts or stops the station heartbeat loop based on the current options.
    /// </summary>
    internal void ApplyOptionsChange()
    {
        if (!_isPrimary) return; // Secondary instances don't own background tasks.

        if (_options.IsEnabled && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            StartStationHeartbeat();
            _syncEngine.Start();
        }
        else
        {
            StopStationHeartbeat();
            _syncEngine.Stop();
        }
    }

    public override async Task Teardown()
    {
        if (_isPrimary)
        {
            _sequenceRetrySubscribeCts?.Cancel();
            _sequenceRetrySubscribeCts?.Dispose();
            _sequenceRetrySubscribeCts = null;
            if (_sequenceEventsSubscribed && SequenceMediator is not null)
            {
                if (_sequenceStartedDelegate != null)
                {
                    SequenceMediator.GetType().GetEvent("SequenceStarted")
                        ?.RemoveEventHandler(SequenceMediator, _sequenceStartedDelegate);
                    _sequenceStartedDelegate = null;
                }
                SequenceMediator.SequenceFinished -= OnSequenceFinished;
            }
            _sessionService.ActiveTargetResolver = null;
            UnsubscribeFromContainerEvents();
            StopStationHeartbeat();
            _syncEngine.Dispose();
            _sessionService.Dispose();
            _frameCache.Dispose();
            Interlocked.CompareExchange(ref _primary, null, this);
            Logger.Info("[Subframes] Plugin unloaded (primary instance).");
        }
        else
        {
            Logger.Info("[Subframes] Plugin unloaded (secondary proxy instance).");
        }
        await base.Teardown();
    }

    // ── Sequence lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Called by NINA when the advanced sequence starts.  Opens an auto-session
    /// immediately so the dashboard reflects the imaging run from the moment
    /// the user hits "Start" — not after the first exposure completes.
    /// </summary>
    private Task OnSequenceStarted(object sender, EventArgs e)
    {
        string? targetName = null;
        double targetRa = 0, targetDec = 0;
        try
        {
            // GetAllTargets() was added in NINA 3.1.3+.  Call via reflection so the
            // plugin compiles against SDK 3.1.2.9001 and degrades gracefully there.
            var getAllTargets = SequenceMediator?.GetType().GetMethod("GetAllTargets");
            var targets = getAllTargets?.Invoke(SequenceMediator, null) as System.Collections.IList;
            if (targets is { Count: > 0 })
            {
                var first = targets[0]!;
                var firstType = first.GetType();

                // Use explicit reflection instead of dynamic to avoid DLR cross-assembly
                // binding failures that silently swallow property access.
                var name = firstType.GetProperty("Name")?.GetValue(first) as string;
                if (string.IsNullOrWhiteSpace(name))
                    name = ReflectNestedString(first, firstType, "Target", "TargetName");

                if (!string.IsNullOrWhiteSpace(name))
                {
                    targetName = CatalogNameNormalizer.Normalize(name);
                    (targetRa, targetDec) = ReflectCoordinates(first, firstType);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] Could not read targets from sequence: {ex.Message}");
        }

        // Bug 2: RA=0/Dec=0 is the vernal equinox — not a real DSO target.
        // Treat it as "no target known yet" so the session opens without a bogus location.
        if (targetRa == 0 && targetDec == 0)
            targetName = null;

        if (_sessionService.HasActiveSession)
        {
            // Session already active (e.g. manual StartSubframesSession ran first).
            // If we found a real target, register it immediately so the web app
            // updates without waiting for the first exposure to complete.
            if (!string.IsNullOrWhiteSpace(targetName))
                _ = _sessionService.OnTargetDetectedAsync(targetName, targetRa, targetDec);
            SubscribeToDsoContainerEvents();
            return Task.CompletedTask;
        }

        _ = _sessionService.OnSequenceStartedAsync(targetName, targetRa, targetDec);
        SubscribeToDsoContainerEvents();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by NINA when the advanced sequence finishes — whether it completed
    /// normally, was cancelled by the user, or failed.  Closes any open session
    /// so the website reflects the actual end of the imaging run.
    /// </summary>
    private Task OnSequenceFinished(object sender, EventArgs e)
    {
        UnsubscribeFromContainerEvents();
        if (_sessionService.HasActiveSession)
        {
            Logger.Info("[Subframes] Sequence run ended — closing active session.");
            _ = _sessionService.EndSessionAsync(CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called on every heartbeat tick by SessionService to detect the currently running
    /// DSO container.  Uses reflection so the plugin degrades gracefully on NINA versions
    /// that do not have GetAllTargets().
    /// </summary>
    private (string? name, double ra, double dec)? ResolveActiveTarget()
    {
        try
        {
            var getAllTargets = SequenceMediator?.GetType().GetMethod("GetAllTargets");
            var targets = getAllTargets?.Invoke(SequenceMediator, null) as System.Collections.IList;
            if (targets is null || targets.Count == 0)
                return null;

            foreach (var t in targets)
            {
                if (t is null) continue;
                var type = t.GetType();

                // Status lives on ISequenceEntity — use reflection to avoid DLR
                // cross-assembly binding failures that silently swallow the property.
                var statusStr = type.GetProperty("Status")?.GetValue(t)?.ToString();
                if (!string.Equals(statusStr, "RUNNING", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Name: try ISequenceItem.Name first, fall back to Target.TargetName.
                var name = type.GetProperty("Name")?.GetValue(t) as string;
                if (string.IsNullOrWhiteSpace(name))
                    name = ReflectNestedString(t, type, "Target", "TargetName");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // Coordinates: try direct Coordinates.RA/Dec (concrete class),
                // then Target.InputCoordinates.Coordinates.RA/Dec.
                var (ra, dec) = ReflectCoordinates(t, type);

                return (CatalogNameNormalizer.Normalize(name), ra, dec);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] ResolveActiveTarget failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Read a nested string property via reflection: obj.prop1.prop2.</summary>
    private static string? ReflectNestedString(object obj, Type type, string prop1, string prop2)
    {
        try
        {
            var intermediate = type.GetProperty(prop1)?.GetValue(obj);
            if (intermediate is null) return null;
            return intermediate.GetType().GetProperty(prop2)?.GetValue(intermediate) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Extract RA/Dec from a DSO container via reflection. Tries direct Coordinates
    /// property first (concrete DeepSkyObjectContainer), then Target.InputCoordinates.Coordinates.
    /// </summary>
    private static (double ra, double dec) ReflectCoordinates(object obj, Type type)
    {
        try
        {
            // Path 1: obj.Coordinates.RA / obj.Coordinates.Dec
            var coords = type.GetProperty("Coordinates")?.GetValue(obj);
            if (coords is not null)
            {
                var ct = coords.GetType();
                var ra  = ct.GetProperty("RA")?.GetValue(coords);
                var dec = ct.GetProperty("Dec")?.GetValue(coords);
                if (ra is double r && dec is double d)
                    return (r, d);
            }
        }
        catch { /* fall through */ }

        try
        {
            // Path 2: obj.Target.InputCoordinates.Coordinates.RA / .Dec
            var target = type.GetProperty("Target")?.GetValue(obj);
            if (target is null) return (0, 0);
            var inputCoords = target.GetType().GetProperty("InputCoordinates")?.GetValue(target);
            if (inputCoords is null) return (0, 0);
            var coords = inputCoords.GetType().GetProperty("Coordinates")?.GetValue(inputCoords);
            if (coords is null) return (0, 0);
            var ct = coords.GetType();
            var ra  = ct.GetProperty("RA")?.GetValue(coords);
            var dec = ct.GetProperty("Dec")?.GetValue(coords);
            if (ra is double r && dec is double d)
                return (r, d);
        }
        catch { /* best effort */ }

        return (0, 0);
    }

    // ── DSO container event subscription ────────────────────────────────────

    /// <summary>
    /// Subscribes to PropertyChanged on all DSO containers in the current sequence so that
    /// when a container transitions to RUNNING we can immediately update the current target
    /// — without waiting for the first image to be saved.
    /// </summary>
    private void SubscribeToDsoContainerEvents()
    {
        UnsubscribeFromContainerEvents();
        try
        {
            var getAllTargets = SequenceMediator?.GetType().GetMethod("GetAllTargets");
            var containers = getAllTargets?.Invoke(SequenceMediator, null) as System.Collections.IList;
            if (containers is null or { Count: 0 }) return;

            foreach (var obj in containers)
            {
                if (obj is not INotifyPropertyChanged notifiable) continue;
                PropertyChangedEventHandler handler = OnContainerPropertyChanged;
                notifiable.PropertyChanged += handler;
                _containerSubscriptions.Add((notifiable, handler));
            }

            if (_containerSubscriptions.Count > 0)
                Logger.Debug($"[Subframes] Subscribed to PropertyChanged for {_containerSubscriptions.Count} DSO container(s).");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] SubscribeToDsoContainerEvents error: {ex.Message}");
        }
    }

    private void UnsubscribeFromContainerEvents()
    {
        foreach (var (container, handler) in _containerSubscriptions)
        {
            try { container.PropertyChanged -= handler; }
            catch { /* ignore — container may already be GC'd */ }
        }
        _containerSubscriptions.Clear();
    }

    /// <summary>
    /// Fires when any subscribed DSO container's properties change.
    /// When the container transitions to RUNNING, extracts its target and
    /// calls <see cref="SessionService.OnDSOContainerStartedAsync"/>.
    /// Uses explicit reflection (not dynamic) to avoid DLR cross-assembly
    /// binding failures that silently swallow property access.
    /// </summary>
    private void OnContainerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Status" || sender is null) return;
        try
        {
            var type = sender.GetType();
            var statusStr = type.GetProperty("Status")?.GetValue(sender)?.ToString();
            if (!string.Equals(statusStr, "RUNNING", StringComparison.OrdinalIgnoreCase))
                return;

            var name = type.GetProperty("Name")?.GetValue(sender) as string;
            if (string.IsNullOrWhiteSpace(name))
                name = ReflectNestedString(sender, type, "Target", "TargetName");
            if (string.IsNullOrWhiteSpace(name)) return;

            var (ra, dec) = ReflectCoordinates(sender, type);
            if (ra == 0 && dec == 0) return;

            var normalized = CatalogNameNormalizer.Normalize(name);
            Logger.Debug($"[Subframes] DSO container RUNNING: '{normalized}' RA={ra:F4} Dec={dec:F4}");
            _ = _sessionService.OnDSOContainerStartedAsync(normalized, ra, dec);
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] OnContainerPropertyChanged error: {ex.Message}");
        }
    }

    // ── Station heartbeat ────────────────────────────────────────────────────

    private void StartStationHeartbeat()
    {
        StopStationHeartbeat();
        var cts = new CancellationTokenSource();
        _stationHeartbeatCts = cts;
        _stationHeartbeatTask = RunStationHeartbeatLoopAsync(cts.Token);
    }

    private void StopStationHeartbeat()
    {
        _stationHeartbeatCts?.Cancel();
        _stationHeartbeatCts?.Dispose();
        _stationHeartbeatCts = null;
        _stationHeartbeatTask = null;
    }

    private async Task RunStationHeartbeatLoopAsync(CancellationToken ct)
    {
        // Immediate heartbeat on startup.
        _ = _apiClient.SendStationHeartbeatAsync(BuildStationHeartbeatRequest(), CancellationToken.None);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(300));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                _ = _apiClient.SendStationHeartbeatAsync(BuildStationHeartbeatRequest(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — plugin unloaded.
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Station heartbeat loop terminated unexpectedly: {ex.Message}");
        }
    }

    private StationHeartbeatRequest BuildStationHeartbeatRequest()
    {
        var profile = _profileService.ActiveProfile;

        List<DeviceDto>? devices = null;
        try { devices = BuildDevices(); }
        catch (Exception ex) { Logger.Warning($"[Subframes] Station heartbeat: could not collect device statuses: {ex.Message}"); }

        StationEquipmentDto? equipment = null;
        StationLocationDto? location = null;

        try
        {
            var ts = profile?.TelescopeSettings;
            var cs = profile?.CameraSettings;
            var fw = profile?.FilterWheelSettings;

            // Derive aperture diameter (mm) from FocalLength / FocalRatio (f-number).
            double? apertureDiameter = (ts?.FocalLength is double fl and > 0 && ts?.FocalRatio is double fr and > 0)
                ? fl / fr
                : null;

            var filters = fw?.FilterWheelFilters
                ?.Where(f => !string.IsNullOrEmpty(f.Name))
                .Select(f => f.Name!)
                .ToList();

            // ICameraSettings and IFilterWheelSettings no longer expose a Name property
            // in NINA SDK 3.1.2+.  Fall back to the mediators' GetInfo() which report
            // the connected device name at runtime.
            string? cameraName = null;
            try { cameraName = _cameraMediator.GetInfo().Name; } catch { }

            string? filterWheelName = null;
            try { filterWheelName = _filterWheelMediator.GetInfo().Name; } catch { }

            equipment = new StationEquipmentDto
            {
                TelescopeName = ts?.Name,
                FocalLength   = DoubleExtensions.Finite(ts?.FocalLength),
                Aperture      = DoubleExtensions.Finite(apertureDiameter),
                CameraName    = cameraName,
                PixelSize     = DoubleExtensions.Finite(cs?.PixelSize),
                MountName     = ts?.Name,
                FilterWheel   = filterWheelName,
                Filters       = filters is { Count: > 0 } ? filters : null,
                Devices       = devices is { Count: > 0 } ? devices : null,
            };
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Station heartbeat: could not read equipment profile: {ex.Message}");
        }

        try
        {
            var ast = profile?.AstrometrySettings;
            location = new StationLocationDto
            {
                Latitude        = DoubleExtensions.Finite(ast?.Latitude),
                Longitude       = DoubleExtensions.Finite(ast?.Longitude),
                Label           = profile?.Name,
                ElevationMeters = DoubleExtensions.Finite(ast?.Elevation),
            };
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Station heartbeat: could not read location profile: {ex.Message}");
        }

        var status = _sessionService.HasActiveSession ? "imaging" : "online";

        return new StationHeartbeatRequest
        {
            InstanceId    = string.IsNullOrWhiteSpace(_options.InstanceId) ? null : _options.InstanceId,
            InstanceName  = string.IsNullOrWhiteSpace(_options.InstanceName) ? null : _options.InstanceName,
            Status        = status,
            PluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            Equipment     = equipment,
            Location      = location,
        };
    }

    private List<DeviceDto> BuildDevices()
    {
        var devices = new List<DeviceDto>(8);

        void Add(string category, string? name, bool connected, string? driverVersion = null)
        {
            if (!connected && string.IsNullOrEmpty(name)) return;
            devices.Add(new DeviceDto { Category = category, Name = string.IsNullOrEmpty(name) ? null : name, Connected = connected, DriverVersion = driverVersion });
        }

        try { var i = _cameraMediator.GetInfo();      Add("Camera",      i.Name, i.Connected); } catch { /* device not available */ }
        try { var i = _telescopeMediator.GetInfo();   Add("Mount",        i.Name, i.Connected); } catch { /* device not available */ }
        try { var i = _focuserMediator.GetInfo();     Add("Focuser",     i.Name, i.Connected); } catch { /* device not available */ }
        try { var i = _filterWheelMediator.GetInfo(); Add("FilterWheel", i.Name, i.Connected); } catch { /* device not available */ }
        try { var i = _rotatorMediator.GetInfo();     Add("Rotator",     i.Name, i.Connected); } catch { /* device not available */ }
        try { var i = _guiderMediator.GetInfo();      Add("Guider",      i.Name, i.Connected); } catch { /* device not available */ }
        try { var i = _flatDeviceMediator.GetInfo();  Add("FlatPanel",   i.Name, i.Connected); } catch { /* device not available */ }

        return devices;
    }
}
