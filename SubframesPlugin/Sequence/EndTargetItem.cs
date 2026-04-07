using System.ComponentModel.Composition;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;

namespace Subframes.NinaPlugin.Sequence;

/// <summary>
/// NINA sequence item: "Subframes: End Target".
///
/// Place this in "After Each Target" (or at the end of each target container).
/// When executed, it notifies the Subframes API that the current target is complete
/// and clears the active sessionTargetId.
///
/// If no target is active, or the API returns 404 (older backend), this item is
/// a graceful no-op and does NOT abort the imaging sequence.
/// </summary>
[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Subframes: End Target")]
[ExportMetadata("Description", "Marks the current Subframes target as complete. Place at the end of each target block.")]
[ExportMetadata("Icon", "Subframes_SVG")]
[ExportMetadata("Category", "Subframes")]
public sealed class EndTargetItem : SequenceItem, IValidatable
{
    private readonly SessionService _sessionService;

    [ImportingConstructor]
    public EndTargetItem(SubframesPlugin plugin)
    {
        _sessionService = plugin.SessionService;
    }

    // Copy constructor for Clone().
    private EndTargetItem(EndTargetItem other) : base(other)
    {
        _sessionService = other._sessionService;
    }

    // ── ISequenceItem ────────────────────────────────────────────────────────

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken ct)
    {
        if (_sessionService.ActiveSessionTargetId is null)
        {
            Logger.Debug("[Subframes] EndTargetItem: no active target — skipping.");
            return;
        }

        progress.Report(new ApplicationStatus
        {
            Status = "Subframes: ending current target..."
        });

        await _sessionService.EndTargetAsync(ct);

        Logger.Info("[Subframes] EndTargetItem complete.");
        progress.Report(new ApplicationStatus
        {
            Status = "Subframes: target ended."
        });
    }

    public override object Clone() => new EndTargetItem(this);

    public override string ToString() => "[SubframesEndTarget]";

    // ── IValidatable ─────────────────────────────────────────────────────────

    public IList<string> Issues { get; } = new List<string>();

    public bool Validate()
    {
        Issues.Clear();
        RaisePropertyChanged(nameof(Issues));
        return true;
    }
}
