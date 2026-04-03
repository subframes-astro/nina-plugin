using System.ComponentModel.Composition;
using CommunityToolkit.Mvvm.ComponentModel;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin.Sequence;

/// <summary>
/// NINA sequence item: "Start Subframes Session".
///
/// The user drags this into the beginning of their sequence (typically in the
/// "Sequence Start" area).  On Execute it calls the Subframes API to create a
/// new imaging session and stores the session ID so subsequent ImageSaved events
/// can be attributed to that session.
///
/// Usage:
///   1. Add "Start Subframes Session" to Sequence Start or Before Each Target.
///   2. Set the Target Name property (defaults to "Unknown Target").
///   3. Set RA/Dec if known (otherwise defaults to 0).
///   4. Run your sequence as normal.
/// </summary>
[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Start Subframes Session")]
[ExportMetadata("Description", "Opens a new Subframes session and records all subsequent exposures to the API.")]
[ExportMetadata("Icon", "Subframes_SVG")]
[ExportMetadata("Category", "Subframes")]
public partial class StartSessionItem : SequenceItem, IValidatable
{
    private readonly SessionService _sessionService;
    private readonly IProfileService _profileService;

    [ObservableProperty]
    private string _targetName = "Unknown Target";

    [ObservableProperty]
    private double _targetRa;

    [ObservableProperty]
    private double _targetDec;

    [ImportingConstructor]
    public StartSessionItem(SubframesPlugin plugin, IProfileService profileService)
    {
        _sessionService = plugin.SessionService;
        _profileService = profileService;
    }

    // Copy constructor for Clone().
    private StartSessionItem(StartSessionItem other) : base(other)
    {
        _sessionService = other._sessionService;
        _profileService = other._profileService;
        _targetName = other._targetName;
        _targetRa = other._targetRa;
        _targetDec = other._targetDec;
    }

    // ── ISequenceItem ────────────────────────────────────────────────────────

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken ct)
    {
        progress.Report(new ApplicationStatus
        {
            Status = $"Subframes: starting session for '{TargetName}'..."
        });

        var options = PluginOptions.Load();

        var lat = _profileService.ActiveProfile.AstrometrySettings.Latitude;
        var lon = _profileService.ActiveProfile.AstrometrySettings.Longitude;
        var hasLocation = !(lat == 0.0 && lon == 0.0);

        var request = new StartSessionRequest
        {
            TargetName    = CatalogNameNormalizer.Normalize(TargetName),
            TargetRa      = TargetRa,
            TargetDec     = TargetDec,
            StartTime     = DateTime.UtcNow.ToString("o"),
            InstanceId    = string.IsNullOrWhiteSpace(options.InstanceId) ? null : options.InstanceId,
            InstanceName  = string.IsNullOrWhiteSpace(options.InstanceName) ? null : options.InstanceName,
            LocationLat   = hasLocation ? lat : null,
            LocationLon   = hasLocation ? lon : null,
            LocationLabel = hasLocation ? _profileService.ActiveProfile.Name : null,
        };

        var sessionId = await _sessionService.StartSessionAsync(request, ct);

        if (sessionId is not null)
        {
            Logger.Info($"[Subframes] StartSessionItem complete — session {sessionId}");
            progress.Report(new ApplicationStatus
            {
                Status = $"Subframes: session {sessionId} open."
            });
        }
        else
        {
            // Do NOT throw — a failure here must not abort the imaging sequence.
            Logger.Warning("[Subframes] StartSessionItem: session could not be created (API unreachable?). Continuing sequence.");
            progress.Report(new ApplicationStatus
            {
                Status = "Subframes: could not start session — check API URL and API key in plugin settings."
            });
        }
    }

    public override object Clone() => new StartSessionItem(this);

    public override string ToString() =>
        $"[StartSubframesSession] Target='{TargetName}' RA={TargetRa:F4} Dec={TargetDec:F4}";

    // ── IValidatable ─────────────────────────────────────────────────────────

    public IList<string> Issues { get; } = new List<string>();

    public bool Validate()
    {
        Issues.Clear();

        var options = PluginOptions.Load();
        if (!options.IsEnabled)
            Issues.Add("Subframes plugin is disabled in settings — no data will be sent.");

        if (string.IsNullOrWhiteSpace(options.ApiBaseUrl))
            Issues.Add("Subframes API URL is not configured. Open plugin settings and enter the API base URL.");

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            Issues.Add("Subframes API key is not configured. Open plugin settings and enter your API key.");

        RaisePropertyChanged(nameof(Issues));
        return !Issues.Any(i => i.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
    }
}
