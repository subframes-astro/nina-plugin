using System.ComponentModel.Composition;
using System.Reflection;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
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
public class SubframesPlugin : PluginBase, IPluginManifest
{
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

    private CancellationTokenSource? _stationHeartbeatCts;
    private Task? _stationHeartbeatTask;

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

        Logger.Info("[Subframes] Plugin loaded.");
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
    /// Called by OptionsPanelViewModel after saving settings.
    /// Starts or stops the station heartbeat loop based on the current options.
    /// </summary>
    internal void ApplyOptionsChange()
    {
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
        StopStationHeartbeat();
        _syncEngine.Dispose();
        _sessionService.Dispose();
        _frameCache.Dispose();
        Logger.Info("[Subframes] Plugin unloaded.");
        await base.Teardown();
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

            equipment = new StationEquipmentDto
            {
                TelescopeName = ts?.Name,
                FocalLength   = DoubleExtensions.Finite(ts?.FocalLength),
                Aperture      = DoubleExtensions.Finite(apertureDiameter),
                CameraName    = cs?.Id,
                PixelSize     = DoubleExtensions.Finite(cs?.PixelSize),
                MountName     = ts?.Name,
                FilterWheel   = fw?.Id,
                Filters       = filters is { Count: > 0 } ? filters : null,
                Devices       = devices,
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

        DeviceDto Slot(string category, string? name, bool connected, string? driverVersion = null) =>
            new() { Category = category, Name = string.IsNullOrEmpty(name) ? null : name, Connected = connected, DriverVersion = driverVersion };

        try { var i = _cameraMediator.GetInfo();      devices.Add(Slot("Camera",      i.Name, i.Connected)); } catch { /* device not available */ }
        try { var i = _telescopeMediator.GetInfo();   devices.Add(Slot("Mount",        i.Name, i.Connected)); } catch { /* device not available */ }
        try { var i = _focuserMediator.GetInfo();     devices.Add(Slot("Focuser",     i.Name, i.Connected)); } catch { /* device not available */ }
        try { var i = _filterWheelMediator.GetInfo(); devices.Add(Slot("FilterWheel", i.Name, i.Connected)); } catch { /* device not available */ }
        try { var i = _rotatorMediator.GetInfo();     devices.Add(Slot("Rotator",     i.Name, i.Connected)); } catch { /* device not available */ }
        try { var i = _guiderMediator.GetInfo();      devices.Add(Slot("Guider",      i.Name, i.Connected)); } catch { /* device not available */ }
        try { var i = _flatDeviceMediator.GetInfo();  devices.Add(Slot("FlatPanel",   i.Name, i.Connected)); } catch { /* device not available */ }

        return devices;
    }
}
