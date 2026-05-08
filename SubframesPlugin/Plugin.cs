using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
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
/// by PluginBase - see SubframesPlugin.csproj and Properties/AssemblyInfo.cs.
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
    private readonly ISafetyMonitorMediator _safetyMonitorMediator;
    private readonly IWeatherDataMediator _weatherDataMediator;
    private readonly FrameCache _frameCache;
    private readonly SyncEngine _syncEngine;
    private readonly Data.CacheReplayEngine? _replayEngine;
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
    // True until the first station heartbeat is sent after init or restart.
    // Controls whether we send a full TS progress snapshot or an incremental delta.
    private bool _tsFirstBeat = true;
    // Handler stored so it can be unsubscribed from _tsDetector.StateChanged on stop.
    private EventHandler<string>? _tsStateChangedHandler;
    // UTC ticks of the most recent station heartbeat send; used to debounce rapid
    // event-driven sends (e.g. TS state flapping or collision with the periodic timer).
    private long _lastStationHeartbeatSentTicks;
    private TargetSchedulerDetector? _tsDetector;
    private TsPreviewClient? _tsPreviewClient;
    private volatile TsPreviewDto? _currentTsPreview; // Written from preview fetches; read from BuildStationHeartbeatRequest
    // Written from the floor timer (thread pool); read from TsProfiles property.
    // Field assignment is atomic on .NET for reference types, so no lock needed for
    // the snapshot-replace pattern used here.
    private IReadOnlyList<TsProfileInfo> _tsProfiles = Array.Empty<TsProfileInfo>();

    // ── TS Preview state machine ──────────────────────────────────────────────
    // Replaces the old 60-second PeriodicTimer with an event-driven approach that
    // reduces TS Preview API calls by 95-99%.
    private enum TsPreviewState { Idle, Startup, Active, Idle_CachedPreview }
    private volatile TsPreviewState _tsPreviewState = TsPreviewState.Idle;
    private DateTime _tsPreviewStartupTime;   // When we entered Startup state (used for diagnostics)
    private DateTime _tsLastPreviewFetch;     // Last successful fetch timestamp (ceiling guard)
    private Task? _tsPreviewFloorTimerTask;

    // Fired (on a thread pool thread) when the profile list is refreshed from the TS API.
    // The OptionsPanelViewModel subscribes to keep its dropdown in sync.
    internal event Action<IReadOnlyList<TsProfileInfo>>? TsProfilesUpdated;

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
        IFlatDeviceMediator flatDeviceMediator,
        ISafetyMonitorMediator safetyMonitorMediator,
        IWeatherDataMediator weatherDataMediator)
    {
        // Atomically claim the primary slot.  If another instance already
        // exists, proxy to its services and skip all background work.
        var existing = Interlocked.CompareExchange(ref _primary, this, null);
        if (existing is not null)
        {
            Logger.Warning("[Subframes] Duplicate plugin instance created by MEF - proxying to primary to prevent duplicate sessions/sync.");
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
            _safetyMonitorMediator = safetyMonitorMediator;
            _weatherDataMediator = weatherDataMediator;
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
        _safetyMonitorMediator = safetyMonitorMediator;
        _weatherDataMediator = weatherDataMediator;
        _frameCache = new FrameCache();
        _syncEngine = new SyncEngine(_frameCache, _apiClient, _options);
        _tsDetector = new TargetSchedulerDetector(_options.TsApiPort);
        TsHelper.Configure(_options.TsDatabasePath);
        _sessionService = new SessionService(imageSaveMediator, _apiClient, _options, _frameCache, _syncEngine, safetyMonitorMediator, guiderMediator, weatherDataMediator, rotatorMediator, telescopeMediator, focuserMediator);
        _replayEngine = new Data.CacheReplayEngine(_frameCache, _apiClient, _options, () => _sessionService.HasActiveSession);
        _sessionService.SessionStarted += OnSessionStarted;
        _sessionService.TsPreviewCallback = OnImageSavedTsPreviewCheck;
        // Wire live-session pause/resume into the replay engine.
        _sessionService.SessionStarted  += (_, _) => _replayEngine.PauseForLiveSession();
        _sessionService.SessionEnded    += (_, _) => _replayEngine.ResumeAfterLiveSession();
        _optionsVm = new OptionsPanelViewModel(this);

        if (_options.IsEnabled && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            StartStationHeartbeat();
            _syncEngine.Start();
            _replayEngine.Start();
        }

        var pending = _frameCache.GetPendingCount();
        if (pending > 0)
            Logger.Info($"[Subframes] {pending} cached frames pending sync from previous session.");

        Logger.Info("[Subframes] Plugin loaded (primary instance).");
    }

    // Expose singletons so MEF-constructed sequence items can import them.
    public SessionService SessionService => _sessionService;
    public OptionsPanelViewModel OptionsVM => _optionsVm;

    /// <summary>Exposed so <see cref="UI.OptionsPanelViewModel"/> can poll replay progress.</summary>
    internal Data.CacheReplayEngine? ReplayEngine => _replayEngine;

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

        // SequenceMediator exists but internals aren't ready yet - retry in background.
        Logger.Warning("[Subframes] Sequence event subscription deferred - SequenceMediator internals not yet initialized.");
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
                Logger.Info("[Subframes] ISequenceMediator.SequenceStarted not present in this NINA build - session will open on first captured image instead.");
            }

            _sequenceEventsSubscribed = true;
            _sessionService.ActiveTargetResolver = ResolveActiveTarget;
            _sessionService.SequenceItemsProvider = GetSequenceCurrentItems;
            _sessionService.ActiveProfileNameResolver = () =>
            {
                try { return _profileService.ActiveProfile?.Name; }
                catch { return null; }
            };
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

        Logger.Warning("[Subframes] Gave up subscribing to sequence events after 60s - sessions will not auto-open/close on sequence start/end.");
    }

    /// <summary>
    /// Called by OptionsPanelViewModel after saving settings.
    /// Starts or stops the station heartbeat loop based on the current options.
    /// </summary>
    internal void ApplyOptionsChange()
    {
        if (!_isPrimary) return; // Secondary instances don't own background tasks.

        // Re-apply TS configuration (port or DB path may have changed).
        TsHelper.Configure(_options.TsDatabasePath);
        if (_tsDetector is not null && _tsDetector.Port != _options.TsApiPort)
        {
            _tsDetector.Dispose();
            _tsDetector = new TargetSchedulerDetector(_options.TsApiPort);
        }

        if (_options.IsEnabled && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            StartStationHeartbeat();
            _syncEngine.Start();
            _replayEngine?.Start();
        }
        else
        {
            StopStationHeartbeat();
            _syncEngine.Stop();
            _replayEngine?.Stop();
        }
    }

    /// <summary>
    /// The most recently fetched list of TS profiles, or empty when TS is not active.
    /// Updated by the preview loop every 60 seconds.
    /// </summary>
    internal IReadOnlyList<TsProfileInfo> TsProfiles => _tsProfiles;

    /// <summary>
    /// Called by <see cref="UI.OptionsPanelViewModel"/> when the user picks a different TS profile.
    /// Immediately re-fetches the preview and fires station + session heartbeats.
    /// </summary>
    internal void OnTsProfileSelected(string profileId)
    {
        if (!_isPrimary) return;

        _options.SelectedTsProfileId = profileId;
        _options.Save();

        // Re-fetch preview and send heartbeats asynchronously - fire-and-forget.
        _ = Task.Run(async () =>
        {
            await FetchAndUpdateTsPreviewAsync(CancellationToken.None).ConfigureAwait(false);
            if (_isPrimary && _options.IsEnabled)
            {
                TrySendStationHeartbeatDebounced("TS profile selected");
                _sessionService.TriggerImmediateHeartbeatIfActive();
            }
        });
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
            _sessionService.SequenceItemsProvider = null;
            _sessionService.ActiveProfileNameResolver = null;
            _sessionService.SessionStarted -= OnSessionStarted;
            UnsubscribeFromContainerEvents();
            StopStationHeartbeat();
            _tsDetector?.Dispose();
            _tsDetector = null;
            _tsPreviewClient = null;
            _replayEngine?.Dispose();
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
    /// the user hits "Start" - not after the first exposure completes.
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

        // Bug 2: RA=0/Dec=0 is the vernal equinox - not a real DSO target.
        // Treat it as "no target known yet" so the session opens without a bogus location.
        if (targetRa == 0 && targetDec == 0)
            targetName = null;

        // Warn the user if the sequence doesn't contain a "Start Subframes Session" item.
        // Suppressed when Target Scheduler is active (it manages sessions itself) or when
        // a session is already open (StartSessionItem executed before sequence start).
        try
        {
            bool tsActive = _tsDetector?.CurrentState == "active";
            if (!tsActive && !_sessionService.HasActiveSession && !ContainsStartSessionItem(sender))
            {
                const string toast = "Subframes: no \"Start Subframes Session\" command found in this sequence. "
                    + "Add it to Sequence Start for explicit session control.";
                Logger.Warning("[Subframes] Sequence started without a \"Start Subframes Session\" instruction. "
                    + "Add it to the Sequence Start area for explicit session control.");
                Notification.ShowWarning(toast);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] StartSessionItem presence check failed: {ex.Message}");
        }

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
    /// Called by NINA when the advanced sequence finishes - whether it completed
    /// normally, was cancelled by the user, or failed.  Closes any open session
    /// so the website reflects the actual end of the imaging run.
    /// </summary>
    private Task OnSequenceFinished(object sender, EventArgs e)
    {
        UnsubscribeFromContainerEvents();
        if (_sessionService.HasActiveSession)
        {
            Logger.Info("[Subframes] Sequence run ended - closing active session.");
            // Preserve the cached preview for the Tonight's Plan post-dawn heartbeat.
            if (_tsPreviewState == TsPreviewState.Active || _tsPreviewState == TsPreviewState.Startup)
            {
                _tsPreviewState = TsPreviewState.Idle_CachedPreview;
                Logger.Debug("[Subframes] TS preview state: → IDLE_CACHED_PREVIEW (session ended).");
            }
            _ = _sessionService.EndSessionAsync(CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when SessionService successfully starts a new session.
    /// Transitions the TS preview state machine to Active, fetches the preview with
    /// a stabilization check (re-fetch after a short delay to confirm the TS API has
    /// finished computing all targets), then sends the station heartbeat.
    /// </summary>
    private void OnSessionStarted(object? sender, EventArgs e)
    {
        _tsPreviewState = TsPreviewState.Active;
        _tsPreviewStartupTime = DateTime.UtcNow;
        _tsLastPreviewFetch = DateTime.UtcNow;
        Logger.Info("[Subframes] TS preview state: -> ACTIVE (session started).");

        // Fire-and-forget: fetch preview with stabilization, then send heartbeat.
        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAndUpdateTsPreviewAsync(CancellationToken.None).ConfigureAwait(false);
                var firstCount = _currentTsPreview?.Blocks?.Count ?? 0;
                Logger.Debug($"[Subframes] OnSessionStarted: initial TS preview fetch complete ({firstCount} blocks).");

                // Stabilization: wait briefly and re-fetch to confirm the TS API
                // has finished computing all targets. If the block count increases,
                // repeat once more. Max 3 attempts to avoid delaying indefinitely.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                    var prevCount = _currentTsPreview?.Blocks?.Count ?? 0;
                    await FetchAndUpdateTsPreviewAsync(CancellationToken.None).ConfigureAwait(false);
                    var newCount = _currentTsPreview?.Blocks?.Count ?? 0;
                    Logger.Debug($"[Subframes] OnSessionStarted: stabilization check {attempt + 1} ({prevCount} -> {newCount} blocks).");
                    if (newCount <= prevCount) break; // Stable — stop re-fetching.
                }

                _tsLastPreviewFetch = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Subframes] OnSessionStarted: TS preview fetch failed: {ex.Message}");
            }

            try
            {
                TrySendStationHeartbeatDebounced("session started");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Subframes] OnSessionStarted: station heartbeat failed: {ex.Message}");
            }
        });
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

                // Status lives on ISequenceEntity - use reflection to avoid DLR
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

    /// <summary>
    /// Returns the flat list of all sequence items currently tracked by the advanced sequencer,
    /// via reflection on <c>GetAdvancedSequencerCurrentRunningItems</c>.  Returns null when the
    /// mediator is unavailable or the method does not exist on this NINA build - the caller
    /// treats null as "not tracked" and leaves <see cref="SessionService.SequenceItemsProvider"/>
    /// producing null, which keeps yield counters untracked at session end.
    /// </summary>
    private System.Collections.IList? GetSequenceCurrentItems()
    {
        try
        {
            var method = SequenceMediator?.GetType()
                .GetMethod("GetAdvancedSequencerCurrentRunningItems");
            return method?.Invoke(SequenceMediator, null) as System.Collections.IList;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] GetSequenceCurrentItems failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Recursively walks the sequence item tree rooted at <paramref name="container"/> via
    /// reflection, looking for any item whose type name is <c>StartSessionItem</c>.
    /// Returns <c>true</c> if at least one such item is found anywhere in the tree.
    /// Defensive: any reflection failure is silently ignored.
    /// </summary>
    private static bool ContainsStartSessionItem(object? container)
    {
        if (container is null) return false;
        if (container.GetType().Name == nameof(Sequence.StartSessionItem)) return true;

        try
        {
            var itemsProp = container.GetType().GetProperty("Items");
            if (itemsProp?.GetValue(container) is System.Collections.IEnumerable items)
            {
                foreach (var item in items)
                {
                    if (item is not null && ContainsStartSessionItem(item))
                        return true;
                }
            }
        }
        catch { /* ignore reflection failures */ }

        return false;
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
    /// - without waiting for the first image to be saved.
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
            catch { /* ignore - container may already be GC'd */ }
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
        // Reset TS progress state so the next heartbeat sends a full snapshot.
        _tsFirstBeat = true;
        TsProgressReader.ResetCache();
        _tsDetector?.Start();
        // Subscribe to TS state changes so we can fire an immediate heartbeat when
        // the detector transitions (e.g. no_api → active after DoProbeAsync completes).
        _tsStateChangedHandler = OnTsStateChanged;
        if (_tsDetector is not null)
            _tsDetector.StateChanged += _tsStateChangedHandler;
        _tsPreviewClient = new TsPreviewClient(_options.TsApiPort);
        var cts = new CancellationTokenSource();
        _stationHeartbeatCts = cts;
        _tsPreviewState = TsPreviewState.Idle;
        _stationHeartbeatTask = RunStationHeartbeatLoopAsync(cts.Token);
        _tsPreviewFloorTimerTask = RunTsPreviewFloorTimerAsync(cts.Token);
    }

    private void StopStationHeartbeat()
    {
        // Unsubscribe state-change handler before stopping/replacing the detector.
        if (_tsDetector is not null && _tsStateChangedHandler is not null)
            _tsDetector.StateChanged -= _tsStateChangedHandler;
        _tsStateChangedHandler = null;

        _stationHeartbeatCts?.Cancel();
        _stationHeartbeatCts?.Dispose();
        _stationHeartbeatCts = null;
        _stationHeartbeatTask = null;
        _tsPreviewFloorTimerTask = null;
        _tsPreviewState = TsPreviewState.Idle;
        // Do NOT null _currentTsPreview here - the cached preview is needed for
        // the Tonight's Plan post-dawn heartbeat even after imaging ends.
        _tsProfiles = Array.Empty<TsProfileInfo>();
        _tsDetector?.Stop();
    }

    /// <summary>
    /// Fired by <see cref="TargetSchedulerDetector"/> when the TS availability state transitions
    /// (e.g. <c>no_api → active</c> after <c>DoProbeAsync</c> completes at startup).
    /// Sends an immediate out-of-cycle station heartbeat so the server always receives the
    /// correct <c>tsAvailabilityState</c> within milliseconds of detection.
    /// </summary>
    /// <remarks>
    /// Debouncing: if a heartbeat was sent within the last 2 seconds (e.g. by the periodic
    /// timer or a rapid back-to-back state oscillation), this send is suppressed.  The
    /// timestamp is updated atomically with <see cref="Interlocked.Exchange(ref long, long)"/>.
    /// <para>
    /// One-shot preview fetch: when TS transitions to <c>active</c> while in the <c>Idle</c>
    /// state and no preview has been cached yet, a background fetch is kicked off so the
    /// first station heartbeat after startup already carries <c>TsPreview</c> data.  This
    /// allows the Tonight's Plan email to fire before any imaging session begins.
    /// </para>
    /// </remarks>
    private void OnTsStateChanged(object? sender, string newState)
    {
        try
        {
            if (!_isPrimary || !_options.IsEnabled) return;

            // One-shot pre-session TS preview fetch.
            // When TS first becomes active while we are idle and have no cached preview,
            // kick off a background fetch so the next heartbeat carries TsPreview data.
            // This is the only place this fetch is triggered from Idle - the Active state
            // is already handled by OnImageSavedTsPreviewCheck and RunTsPreviewFloorTimerAsync.
            //
            // We delay 60 seconds before fetching because TS needs time after its API
            // comes online to fully compute the schedule for all targets.  Fetching
            // immediately returns an incomplete plan (e.g. 1-2 blocks instead of 6+).
            if (newState == "active"
                && _tsPreviewState == TsPreviewState.Idle
                && _currentTsPreview is null)
            {
                var fetchCt = _stationHeartbeatCts?.Token ?? CancellationToken.None;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Logger.Info("[Subframes] One-shot TS preview: waiting 60 s for TS to compute full schedule.");
                        await Task.Delay(TimeSpan.FromSeconds(60), fetchCt).ConfigureAwait(false);

                        await FetchAndUpdateTsPreviewAsync(fetchCt).ConfigureAwait(false);
                        var blockCount = _currentTsPreview?.Blocks?.Count ?? 0;
                        Logger.Info($"[Subframes] One-shot TS preview fetch complete (pre-session, {blockCount} blocks).");

                        // The preview heartbeat is the most valuable one (it carries
                        // Tonight's Plan data).  If the debounce window would suppress
                        // it (e.g. the periodic timer fired moments ago), wait it out
                        // so we never silently drop the preview payload.
                        if (!TrySendStationHeartbeatDebounced("one-shot TS preview"))
                        {
                            try { await Task.Delay(TimeSpan.FromSeconds(3), fetchCt).ConfigureAwait(false); }
                            catch (OperationCanceledException) { return; }
                            TrySendStationHeartbeatDebounced("one-shot TS preview (retry after debounce)");
                        }
                    }
                    catch (OperationCanceledException) { /* normal shutdown */ }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[Subframes] One-shot TS preview fetch failed: {ex.Message}");
                    }
                });
            }

            TrySendStationHeartbeatDebounced($"TS state → {newState}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] OnTsStateChanged: immediate station heartbeat failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a station heartbeat only if no heartbeat was sent within the last 2 seconds.
    /// Returns <c>true</c> when the heartbeat was fired, <c>false</c> when suppressed.
    /// <para>
    /// All out-of-cycle heartbeat sites (event handlers, one-shot callbacks) MUST call
    /// this instead of invoking <c>_apiClient.SendStationHeartbeatAsync</c> directly so
    /// startup bursts are deduplicated from a single place.
    /// </para>
    /// <para>
    /// The periodic 300-second loop may still call <c>SendStationHeartbeatAsync</c> directly
    /// (no burst risk at that cadence) but must keep <c>_lastStationHeartbeatSentTicks</c>
    /// up-to-date so the debounce window remains accurate.
    /// </para>
    /// </summary>
    private bool TrySendStationHeartbeatDebounced(string? reason = null)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var prevTicks = Interlocked.Read(ref _lastStationHeartbeatSentTicks);
        if (nowTicks - prevTicks < TimeSpan.TicksPerSecond * 2)
        {
            Logger.Debug(
                $"[Subframes] Station heartbeat suppressed (< 2 s since last send)"
                + (reason is null ? "." : $" [{reason}]."));
            return false;
        }

        Interlocked.Exchange(ref _lastStationHeartbeatSentTicks, nowTicks);
        Logger.Info(
            $"[Subframes] Firing station heartbeat"
            + (reason is null ? "." : $" [{reason}]."));
        _ = _apiClient.SendStationHeartbeatAsync(BuildStationHeartbeatRequest(), CancellationToken.None);
        return true;
    }

    private async Task RunStationHeartbeatLoopAsync(CancellationToken ct)
    {
        // If TS was not detected during initial startup, wait for NINA to
        // finish loading all plugins and retry.  Subframes often loads before
        // Target Scheduler, so the assembly-presence check sees nothing on the
        // first pass.  A 5-second grace period covers typical load-order gaps
        // (observed ~600 ms) with generous margin for slower machines.
        if (_tsDetector?.CurrentState == "none")
        {
            Logger.Info("[Subframes] TS not detected at startup - deferring first heartbeat to allow plugin load to complete.");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            _tsDetector.Start();

            // Give the HTTP probe (2 s timeout) time to reach the TS API so the
            // first heartbeat can report "active" instead of "no_api".
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }

        // Wait for the preview loop to complete its first fetch (up to 10 s) so the
        // first heartbeat includes TS preview data.  Times out gracefully - the
        // heartbeat fires regardless so no data is permanently lost.
        //
        // NOTE: With the event-driven state machine the preview is no longer fetched
        // eagerly at startup (only after session start + 5-min warmup), so we skip
        // this wait entirely.

        // First heartbeat - TS state should now be accurate.
        // Use the centralised debounce so this is suppressed when OnTsStateChanged already
        // fired a heartbeat during the 5 s + 3 s grace window above.
        TrySendStationHeartbeatDebounced("startup loop first tick");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(300));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Interlocked.Exchange(ref _lastStationHeartbeatSentTicks, DateTime.UtcNow.Ticks);
                _ = _apiClient.SendStationHeartbeatAsync(BuildStationHeartbeatRequest(), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - plugin unloaded.
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
            int? sensorWidth = null;
            int? sensorHeight = null;
            try
            {
                var camInfo = _cameraMediator.GetInfo();
                cameraName = camInfo.Name;
                if (camInfo.Connected && camInfo.XSize > 0) sensorWidth = camInfo.XSize;
                if (camInfo.Connected && camInfo.YSize > 0) sensorHeight = camInfo.YSize;
            }
            catch { }

            string? filterWheelName = null;
            try { filterWheelName = _filterWheelMediator.GetInfo().Name; } catch { }

            string? mountName = null;
            try { var i = _telescopeMediator.GetInfo(); if (i.Connected) mountName = i.Name; } catch { }

            string? focuserName = null, rotatorName = null, guiderName = null,
                    flatDeviceName = null, safetyMonitorName = null, weatherStationName = null;
            try { var i = _focuserMediator.GetInfo();       if (i.Connected) focuserName        = i.Name; } catch { }
            try { var i = _rotatorMediator.GetInfo();       if (i.Connected) rotatorName         = i.Name; } catch { }
            try { var i = _guiderMediator.GetInfo();        if (i.Connected) guiderName          = i.Name; } catch { }
            try { var i = _flatDeviceMediator.GetInfo();    if (i.Connected) flatDeviceName      = i.Name; } catch { }
            try { var i = _safetyMonitorMediator.GetInfo(); if (i.Connected) safetyMonitorName   = i.Name; } catch { }
            try { var i = _weatherDataMediator.GetInfo();   if (i.Connected) weatherStationName  = i.Name; } catch { }

            equipment = new StationEquipmentDto
            {
                TelescopeName      = ts?.Name,
                FocalLength        = DoubleExtensions.Finite(ts?.FocalLength),
                Aperture           = DoubleExtensions.Finite(apertureDiameter),
                CameraName         = cameraName,
                SensorWidth        = sensorWidth,
                SensorHeight       = sensorHeight,
                PixelSize          = DoubleExtensions.Finite(cs?.PixelSize),
                MountName          = string.IsNullOrEmpty(mountName) ? null : mountName,
                FilterWheel        = filterWheelName,
                Filters            = filters is { Count: > 0 } ? filters : null,
                FocuserName        = string.IsNullOrEmpty(focuserName)       ? null : focuserName,
                RotatorName        = string.IsNullOrEmpty(rotatorName)       ? null : rotatorName,
                GuiderName         = string.IsNullOrEmpty(guiderName)        ? null : guiderName,
                FlatDeviceName     = string.IsNullOrEmpty(flatDeviceName)    ? null : flatDeviceName,
                SafetyMonitorName  = string.IsNullOrEmpty(safetyMonitorName) ? null : safetyMonitorName,
                WeatherStationName = string.IsNullOrEmpty(weatherStationName)? null : weatherStationName,
                Devices            = devices is { Count: > 0 } ? devices : null,
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
                Timezone        = TimezoneHelper.ResolveIanaTimezone() is string tz && tz.Length > 0 ? tz : null,
            };
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Station heartbeat: could not read location profile: {ex.Message}");
        }

        var status = _sessionService.HasActiveSession ? "imaging" : "online";

        bool? isSafe = null;
        try
        {
            var smInfo = _safetyMonitorMediator.GetInfo();
            if (smInfo.Connected) isSafe = smInfo.IsSafe;
        }
        catch { /* safety monitor not available */ }

        // Include TS progress: full snapshot on first beat, delta on subsequent beats.
        // Reads directly from the TS SQLite DB — available from plugin startup, no session required.
        TsProgressSnapshotDto? tsSnapshot = null;
        TsProgressDeltaDto? tsDelta = null;
        try
        {
            if (_tsFirstBeat)
            {
                tsSnapshot = TsProgressReader.ReadProgressSnapshot();
                _tsFirstBeat = false;
            }
            else
            {
                tsDelta = TsProgressReader.ReadProgressDelta();
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] Station heartbeat: TS progress skipped ({ex.GetType().Name}: {ex.Message})");
        }

        var request = new StationHeartbeatRequest
        {
            InstanceId          = string.IsNullOrWhiteSpace(_options.InstanceId) ? null : _options.InstanceId,
            InstanceName        = string.IsNullOrWhiteSpace(_options.InstanceName) ? null : _options.InstanceName,
            Status              = status,
            PluginVersion       = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            IsSafe              = isSafe,
            Equipment           = equipment,
            Location            = location,
            TsProgressSnapshot  = tsSnapshot,
            TsProgressDelta     = tsDelta,
            TsAvailabilityState = _tsDetector?.CurrentState,
            TsPreview           = (_tsDetector?.CurrentState == "active" || _tsPreviewState == TsPreviewState.Idle_CachedPreview)
                ? _currentTsPreview : null,
        };

        // Log what we're about to send so we can trace data loss between
        // TS fetch and backend ingest.
        var hbBlockCount = request.TsPreview?.Blocks?.Count ?? 0;
        var hbTargetNames = request.TsPreview?.Blocks?
            .Where(b => !b.WaitPeriod)
            .Select(b => $"'{b.TargetName}' {b.StartTime}->{b.EndTime}")
            .ToList();
        Logger.Debug($"[Subframes] BuildStationHeartbeatRequest: TsPreview={request.TsPreview != null}, blocks={hbBlockCount}, tsDetector={_tsDetector?.CurrentState}, previewState={_tsPreviewState}");
        if (hbTargetNames is { Count: > 0 })
            Logger.Debug($"[Subframes] BuildStationHeartbeatRequest targets: {string.Join("; ", hbTargetNames)}");

        return request;
    }

    // ── TS Preview state machine ────────────────────────────────────────────

    /// <summary>
    /// Called via <see cref="SessionService.TsPreviewCallback"/> after each image save.
    /// Only fetches the TS preview if the state machine is in <c>Active</c> state and at
    /// least 30 seconds have elapsed since the last fetch (ceiling guard - prevents
    /// burst-fire during rapid exposures).
    /// </summary>
    private void OnImageSavedTsPreviewCheck()
    {
        if (_tsPreviewState != TsPreviewState.Active) return;

        var now = DateTime.UtcNow;
        if ((now - _tsLastPreviewFetch).TotalSeconds < 30) return;

        _tsLastPreviewFetch = now;
        var cts = _stationHeartbeatCts;
        if (cts is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var previewChanged = await FetchAndUpdateTsPreviewAsync(cts.Token).ConfigureAwait(false);
                if (previewChanged)
                    TrySendStationHeartbeatDebounced("image-saved preview update");
            }
            catch (OperationCanceledException) { /* Normal shutdown */ }
            catch (Exception ex)
            {
                Logger.Debug($"[Subframes] OnImageSavedTsPreviewCheck failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 15-minute floor timer for the TS preview state machine.
    /// During <c>Active</c> state, if no image-driven preview fetch has occurred in the
    /// last 10 minutes, fires one poll to keep the cached preview reasonably fresh.
    /// This replaces the old 60-second polling loop and fires ~96x less often per night.
    /// </summary>
    private async Task RunTsPreviewFloorTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_tsPreviewState != TsPreviewState.Active) continue;

                var idleSinceMinutes = (DateTime.UtcNow - _tsLastPreviewFetch).TotalMinutes;
                if (idleSinceMinutes < 10) continue; // Image-driven fetch was recent enough.

                Logger.Debug("[Subframes] TS preview floor timer: no recent image-driven fetch, polling once.");
                var previewChanged = await FetchAndUpdateTsPreviewAsync(ct).ConfigureAwait(false);
                _tsLastPreviewFetch = DateTime.UtcNow;
                if (previewChanged)
                    TrySendStationHeartbeatDebounced("preview floor timer");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] TS preview floor timer terminated unexpectedly: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the TS preview block count increased (and is non-zero),
    /// signalling to callers that sending a station heartbeat is worthwhile.
    /// Callers are responsible for deciding whether and how to send the heartbeat;
    /// this method no longer fires one internally.
    /// </summary>
    private async Task<bool> FetchAndUpdateTsPreviewAsync(CancellationToken ct)
    {
        if (_tsDetector?.CurrentState != "active" || _tsPreviewClient is null)
        {
            // When TS is not active, preserve the cached preview value - do not null it.
            // This keeps heartbeats carrying the last-known preview (e.g. post-dawn, Idle_CachedPreview state).
            return false;
        }

        try
        {
            var profiles = await _tsPreviewClient.FetchProfilesAsync(ct).ConfigureAwait(false);
            if (profiles.Count == 0)
            {
                // No profiles available - preserve the cached preview (do not null it).
                return false;
            }

            // Notify ViewModel if the profile list changed (compare by ID sequence).
            var prevIds = _tsProfiles.Select(p => p.Id);
            var newIds  = profiles.Select(p => p.Id);
            if (!prevIds.SequenceEqual(newIds))
            {
                _tsProfiles = profiles;
                TsProfilesUpdated?.Invoke(profiles);
            }

            // Resolve the selected profile: prefer the saved ID, fall back to Active, then first.
            var savedId = _options.SelectedTsProfileId;
            TsProfileInfo? selected = null;
            if (!string.IsNullOrEmpty(savedId))
                selected = profiles.FirstOrDefault(p => p.Id == savedId);

            if (selected is null)
            {
                selected = profiles.FirstOrDefault(p => p.Active) ?? profiles[0];
                // Persist the auto-selected profile so the dropdown reflects it.
                _options.SelectedTsProfileId = selected.Id;
                _options.Save();
            }

            var previousBlockCount = _currentTsPreview?.Blocks?.Count ?? 0;
            _currentTsPreview = await _tsPreviewClient.FetchPreviewAsync(selected.Id, selected.Name, ct).ConfigureAwait(false);
            var newBlockCount = _currentTsPreview?.Blocks?.Count ?? 0;
            if (newBlockCount != previousBlockCount && newBlockCount > 0)
            {
                // Preview changed - return true so the caller can push an immediate
                // heartbeat via TrySendStationHeartbeatDebounced(). We no longer fire
                // one here to avoid double-fires when the caller is already about to
                // send its own heartbeat.
                return true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] FetchAndUpdateTsPreviewAsync error: {ex.Message}");
            _currentTsPreview = null;
        }

        return false;
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
        try { var i = _safetyMonitorMediator.GetInfo(); Add("SafetyMonitor", i.Name, i.Connected); } catch { /* device not available */ }

        return devices;
    }
}
