using System.ComponentModel.Composition;
using System.Reflection;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using Subframes.NinaPlugin.Api;
using Subframes.NinaPlugin.UI;

namespace Subframes.NinaPlugin;

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

    private CancellationTokenSource? _stationHeartbeatCts;
    private Task? _stationHeartbeatTask;

    [ImportingConstructor]
    public SubframesPlugin(IImageSaveMediator imageSaveMediator, IProfileService profileService)
    {
        _options = PluginOptions.Load();
        _apiClient = new SubframesClient(_options);
        _profileService = profileService;
        _sessionService = new SessionService(imageSaveMediator, _apiClient, _options);
        _optionsVm = new OptionsPanelViewModel(this);

        if (_options.IsEnabled && !string.IsNullOrWhiteSpace(_options.ApiKey))
            StartStationHeartbeat();

        Logger.Info("[Subframes] Plugin loaded.");
    }

    // Expose singletons so MEF-constructed sequence items can import them.
    public SessionService SessionService => _sessionService;
    public OptionsPanelViewModel OptionsVM => _optionsVm;

    public override async Task Teardown()
    {
        StopStationHeartbeat();
        _sessionService.Dispose();
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

        StationEquipmentDto? equipment = null;
        StationLocationDto? location = null;

        try
        {
            var ts = profile?.TelescopeSettings;
            var cs = profile?.CameraSettings;
            var fw = profile?.FilterWheelSettings;

            // ApertureArea in NINA is stored in mm²; derive diameter = 2*sqrt(area/π).
            double? apertureDiameter = ts?.ApertureArea is double area and > 0
                ? 2.0 * Math.Sqrt(area / Math.PI)
                : null;

            var filters = fw?.FilterWheelFilters
                ?.Where(f => !string.IsNullOrEmpty(f.Name))
                .Select(f => f.Name!)
                .ToList();

            equipment = new StationEquipmentDto
            {
                TelescopeName = ts?.Name,
                FocalLength   = ts?.FocalLength,
                Aperture      = apertureDiameter,
                CameraName    = cs?.Name,
                PixelSize     = cs?.PixelSize,
                MountName     = ts?.Name,
                FilterWheel   = fw?.Id,
                Filters       = filters is { Count: > 0 } ? filters : null,
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
                Latitude        = ast?.Latitude,
                Longitude       = ast?.Longitude,
                Label           = profile?.Name,
                ElevationMeters = ast?.Elevation,
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
}
