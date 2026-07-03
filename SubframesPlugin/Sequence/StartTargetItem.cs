using System.ComponentModel.Composition;
using CommunityToolkit.Mvvm.ComponentModel;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Subframes.NinaPlugin;

namespace Subframes.NinaPlugin.Sequence;

/// <summary>
/// NINA sequence item: "Subframes: Start Target".
///
/// Place this in "Before Each Target" (or at the start of each target container).
/// When executed, it calls the Subframes API to register the new target within the
/// active session and stores the returned sessionTargetId for frame attribution.
///
/// If no session is active, or the API returns 404 (older backend), this item is
/// a graceful no-op and does NOT abort the imaging sequence.
/// </summary>
[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Subframes: Start Target")]
[ExportMetadata("Description", "Registers a new target within the active Subframes session. Place at the beginning of each target block.")]
[ExportMetadata("Icon", "Subframes_SVG")]
[ExportMetadata("Category", "Subframes")]
public partial class StartTargetItem : SequenceItem, IValidatable
{
    private readonly SessionService _sessionService;

    [ObservableProperty]
    private string _targetName = "Unknown Target";

    [ObservableProperty]
    private double _targetRa;

    [ObservableProperty]
    private double _targetDec;

    [ObservableProperty]
    private string _targetType = string.Empty;

    [ImportingConstructor]
    public StartTargetItem(SubframesPlugin plugin)
    {
        _sessionService = plugin.SessionService;
    }

    // Copy constructor for Clone().
    private StartTargetItem(StartTargetItem other) : base(other)
    {
        _sessionService = other._sessionService;
        _targetName     = other._targetName;
        _targetRa       = other._targetRa;
        _targetDec      = other._targetDec;
        _targetType     = other._targetType;
    }

    // ── ISequenceItem ────────────────────────────────────────────────────────

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken ct)
    {
        if (!_sessionService.HasActiveSession)
        {
            SubframesLogger.Warning("StartTargetItem: no active session — skipping target start.");
            progress.Report(new ApplicationStatus
            {
                Status = "Subframes: no active session — skipping target start."
            });
            return;
        }

        progress.Report(new ApplicationStatus
        {
            Status = $"Subframes: starting target '{TargetName}'..."
        });

        var normalizedName = CatalogNameNormalizer.Normalize(TargetName);
        var targetType = string.IsNullOrWhiteSpace(TargetType) ? null : TargetType.Trim();

        var targetId = await _sessionService.StartTargetAsync(
            normalizedName, TargetRa, TargetDec, targetType, ct);

        if (targetId is not null)
        {
            SubframesLogger.Info($"StartTargetItem complete — targetId={targetId} name='{normalizedName}'");
            progress.Report(new ApplicationStatus
            {
                Status = $"Subframes: target '{normalizedName}' registered."
            });
        }
        else
        {
            // 404 / disabled / API unreachable — never abort the sequence.
            SubframesLogger.Info("StartTargetItem: target start returned null (API unavailable or older version).");
            progress.Report(new ApplicationStatus
            {
                Status = $"Subframes: target registration skipped (API unavailable)."
            });
        }
    }

    public override object Clone() => new StartTargetItem(this);

    public override string ToString() =>
        $"[SubframesStartTarget] Target='{TargetName}' RA={TargetRa:F4} Dec={TargetDec:F4}";

    // ── IValidatable ─────────────────────────────────────────────────────────

    public IList<string> Issues { get; } = new List<string>();

    public bool Validate()
    {
        Issues.Clear();
        RaisePropertyChanged(nameof(Issues));
        return true;
    }
}
