using System.ComponentModel.Composition;
using CommunityToolkit.Mvvm.ComponentModel;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
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
    private readonly ICameraMediator _cameraMediator;

    [ObservableProperty]
    private string _targetName = "Unknown Target";

    [ObservableProperty]
    private double _targetRa;

    [ObservableProperty]
    private double _targetDec;

    [ImportingConstructor]
    public StartSessionItem(SubframesPlugin plugin, IProfileService profileService, ICameraMediator cameraMediator)
    {
        _sessionService = plugin.SessionService;
        _profileService = profileService;
        _cameraMediator = cameraMediator;
    }

    // Copy constructor for Clone().
    private StartSessionItem(StartSessionItem other) : base(other)
    {
        _sessionService = other._sessionService;
        _profileService = other._profileService;
        _cameraMediator = other._cameraMediator;
        _targetName = other._targetName;
        _targetRa = other._targetRa;
        _targetDec = other._targetDec;
    }

    // ── ISequenceItem ────────────────────────────────────────────────────────

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken ct)
    {
        // Resolve target from the sequence tree when the user left the default
        // "Unknown Target" or when RA/Dec are both zero (no real coordinates).
        // This walks sibling DSO containers (e.g. in the TargetAreaContainer)
        // so the session opens with the correct target immediately — instead of
        // sending "Unknown Target" and waiting for the first image to be saved.
        var resolvedName = TargetName;
        var resolvedRa   = TargetRa;
        var resolvedDec  = TargetDec;

        if (resolvedName == "Unknown Target" || (resolvedRa == 0 && resolvedDec == 0))
        {
            var fromTree = ResolveTargetFromSequenceTree();
            if (fromTree is var (tName, tRa, tDec))
            {
                resolvedName = tName;
                resolvedRa   = tRa;
                resolvedDec  = tDec;
                Logger.Info($"[Subframes] Resolved target from sequence tree: '{resolvedName}' RA={resolvedRa:F4} Dec={resolvedDec:F4}");
            }
        }

        // If still "Unknown Target" with zero coords, send empty name so the
        // backend doesn't store a bogus target — the real target will be
        // registered when the DSO container starts or the first image saves.
        var isUnknown = string.Equals(resolvedName, "Unknown Target", StringComparison.OrdinalIgnoreCase)
                        && resolvedRa == 0 && resolvedDec == 0;
        var targetName = isUnknown ? string.Empty : CatalogNameNormalizer.Normalize(resolvedName);

        progress.Report(new ApplicationStatus
        {
            Status = string.IsNullOrEmpty(targetName)
                ? "Subframes: starting session (target pending)..."
                : $"Subframes: starting session for '{targetName}'..."
        });

        var options = PluginOptions.Load();

        var lat = _profileService.ActiveProfile.AstrometrySettings.Latitude;
        var lon = _profileService.ActiveProfile.AstrometrySettings.Longitude;
        var hasLocation = !(lat == 0.0 && lon == 0.0);

        // Read camera hardware snapshot — gracefully null if camera is disconnected.
        double? pixelSizeMicrons = null;
        int? sensorWidthPx = null;
        int? sensorHeightPx = null;
        try
        {
            var camInfo = _cameraMediator.GetInfo();
            if (camInfo is { Connected: true })
            {
                pixelSizeMicrons = camInfo.PixelSize > 0 ? camInfo.PixelSize : null;
                sensorWidthPx    = camInfo.XSize > 0 ? camInfo.XSize : null;
                sensorHeightPx   = camInfo.YSize > 0 ? camInfo.YSize : null;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Could not read camera info for session start: {ex.Message}");
        }

        double? focalLengthMm = null;
        try
        {
            var fl = _profileService.ActiveProfile?.TelescopeSettings?.FocalLength;
            focalLengthMm = fl is > 0 ? fl : null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] Could not read focal length for session start: {ex.Message}");
        }

        var plannedTargets = TsPlannedTargetReader.ReadPlannedTargets();
        if (plannedTargets is not null)
            Logger.Info($"[Subframes] Including {plannedTargets.Count} planned target(s) from Target Scheduler in session start");
        else
            Logger.Info("[Subframes] No Target Scheduler planned targets to include in session start");

        var request = new StartSessionRequest
        {
            TargetName       = targetName,
            TargetRa         = resolvedRa,
            TargetDec        = resolvedDec,
            StartTime        = DateTime.UtcNow.ToString("o"),
            InstanceId       = string.IsNullOrWhiteSpace(options.InstanceId) ? null : options.InstanceId,
            InstanceName     = string.IsNullOrWhiteSpace(options.InstanceName) ? null : options.InstanceName,
            LocationLat      = hasLocation ? lat : null,
            LocationLon      = hasLocation ? lon : null,
            LocationLabel    = hasLocation ? _profileService.ActiveProfile?.Name : null,
            PixelSizeMicrons = pixelSizeMicrons,
            SensorWidthPx    = sensorWidthPx,
            SensorHeightPx   = sensorHeightPx,
            FocalLengthMm    = focalLengthMm,
            PlannedTargets   = plannedTargets,
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

    // ── Sequence tree target resolution ─────────────────────────────────────

    /// <summary>
    /// Walks the sequence tree (up to root, then down through all children) to
    /// find the first <see cref="IDeepSkyObjectContainer"/> with a valid target
    /// name and non-zero coordinates.  Inspired by the Nina.DiscordAlert plugin's
    /// <c>GetDSOContainer()</c> approach of navigating the sequence hierarchy.
    /// </summary>
    private (string name, double ra, double dec)? ResolveTargetFromSequenceTree()
    {
        try
        {
            // Walk up to the root container.
            ISequenceContainer? root = this.Parent;
            while (root?.Parent != null)
                root = root.Parent;

            return root is not null ? FindFirstDsoTarget(root) : null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] Could not resolve target from sequence tree: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Recursively searches <paramref name="container"/> and its children for
    /// the first DSO container that has a named target with real coordinates.
    /// Uses reflection to read target properties (consistent with Plugin.cs)
    /// to avoid DLR cross-assembly binding failures.
    /// </summary>
    private static (string name, double ra, double dec)? FindFirstDsoTarget(ISequenceContainer container)
    {
        // Check this container.
        var result = TryExtractDsoTarget(container);
        if (result is not null) return result;

        // Recurse into children.
        try
        {
            foreach (var item in container.Items)
            {
                if (item is ISequenceContainer child)
                {
                    result = FindFirstDsoTarget(child);
                    if (result is not null) return result;
                }
            }
        }
        catch { /* Items may not be accessible — best effort */ }

        return null;
    }

    /// <summary>
    /// Extracts target info from an object if it looks like a DSO container
    /// (has a Target.TargetName and valid coordinates).
    /// </summary>
    private static (string name, double ra, double dec)? TryExtractDsoTarget(object obj)
    {
        try
        {
            var type = obj.GetType();

            // Read the target name: try Name first (IDeepSkyObjectContainer.Name
            // often returns the target name), then Target.TargetName.
            var name = type.GetProperty("Target") is { } targetProp
                ? targetProp.GetValue(obj) is { } target
                    ? target.GetType().GetProperty("TargetName")?.GetValue(target) as string
                    : null
                : null;

            if (string.IsNullOrWhiteSpace(name)) return null;

            // Extract coordinates via the same reflection paths used in Plugin.cs.
            var (ra, dec) = ReflectCoordinates(obj, type);
            if (ra == 0 && dec == 0) return null;

            return (name, ra, dec);
        }
        catch { return null; }
    }

    /// <summary>
    /// Extract RA/Dec from a DSO container via reflection.
    /// Mirrors Plugin.ReflectCoordinates — kept local to avoid coupling.
    /// </summary>
    private static (double ra, double dec) ReflectCoordinates(object obj, Type type)
    {
        try
        {
            var coords = type.GetProperty("Coordinates")?.GetValue(obj);
            if (coords is not null)
            {
                var ct = coords.GetType();
                if (ct.GetProperty("RA")?.GetValue(coords) is double r
                    && ct.GetProperty("Dec")?.GetValue(coords) is double d)
                    return (r, d);
            }
        }
        catch { /* fall through */ }

        try
        {
            var target = type.GetProperty("Target")?.GetValue(obj);
            if (target is null) return (0, 0);
            var inputCoords = target.GetType().GetProperty("InputCoordinates")?.GetValue(target);
            if (inputCoords is null) return (0, 0);
            var coords = inputCoords.GetType().GetProperty("Coordinates")?.GetValue(inputCoords);
            if (coords is null) return (0, 0);
            var ct = coords.GetType();
            if (ct.GetProperty("RA")?.GetValue(coords) is double r
                && ct.GetProperty("Dec")?.GetValue(coords) is double d)
                return (r, d);
        }
        catch { /* best effort */ }

        return (0, 0);
    }

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
