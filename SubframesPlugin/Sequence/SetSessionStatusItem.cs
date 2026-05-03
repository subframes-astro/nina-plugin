using System.ComponentModel.Composition;
using CommunityToolkit.Mvvm.ComponentModel;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;

namespace Subframes.NinaPlugin.Sequence;

/// <summary>
/// NINA sequence item: "Subframes: Set Session Status".
///
/// Use this to signal waiting or paused states to the Subframes platform.
/// Drop it immediately before a NINA wait instruction (WaitForTime,
/// WaitForAltitude, WaitForTwilight, CoolCamera, etc.) and set:
///   - SessionStatus = "waiting"
///   - WaitReason = e.g. "Waiting for astronomical twilight"
///
/// When a new exposure is saved, the plugin automatically transitions the
/// session back to "active" — you do not need a separate "set active" item
/// after each wait.
///
/// If no session is active, or the API returns 404 (older backend), this
/// item is a graceful no-op and does NOT abort the imaging sequence.
///
/// Predefined wait reasons (copy-paste friendly):
///   Waiting for scheduled time
///   Waiting for target altitude > {threshold}°
///   Waiting for target to rise
///   Waiting for astronomical twilight
///   Cooling camera to {target}°C
///   Waiting for loop condition
/// </summary>
[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Subframes: Set Session Status")]
[ExportMetadata("Description", "Update the Subframes session status (waiting / active / paused). Place before wait instructions to signal the platform.")]
[ExportMetadata("Icon", "Subframes_SVG")]
[ExportMetadata("Category", "Subframes")]
public partial class SetSessionStatusItem : SequenceItem, IValidatable
{
    private readonly SessionService _sessionService;

    /// <summary>One of: waiting, active, paused.</summary>
    [ObservableProperty]
    private string _sessionStatus = "waiting";

    /// <summary>Human-readable reason shown to the user. Only used when SessionStatus = "waiting".</summary>
    [ObservableProperty]
    private string _waitReason = string.Empty;

    [ImportingConstructor]
    public SetSessionStatusItem(SubframesPlugin plugin)
    {
        _sessionService = plugin.SessionService;
    }

    // Copy constructor for Clone().
    private SetSessionStatusItem(SetSessionStatusItem other) : base(other)
    {
        _sessionService = other._sessionService;
        _sessionStatus  = other._sessionStatus;
        _waitReason     = other._waitReason;
    }

    // ── ISequenceItem ────────────────────────────────────────────────────────

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken ct)
    {
        if (!_sessionService.HasActiveSession)
        {
            Logger.Debug("[Subframes] SetSessionStatusItem: no active session — skipping.");
            return;
        }

        var status = SessionStatus.Trim().ToLowerInvariant();
        var waitReason = (status == "waiting" && !string.IsNullOrWhiteSpace(WaitReason))
            ? WaitReason.Trim()
            : null;

        progress.Report(new ApplicationStatus
        {
            Status = waitReason is not null
                ? $"Subframes: session status → {status} ({waitReason})"
                : $"Subframes: session status → {status}"
        });

        await _sessionService.UpdateStatusAsync(status, waitReason, ct);

        Logger.Info($"[Subframes] SetSessionStatusItem: status={status}" +
                    (waitReason is not null ? $" reason='{waitReason}'" : ""));
    }

    public override object Clone() => new SetSessionStatusItem(this);

    public override string ToString() =>
        $"[SubframesSetStatus] SessionStatus='{SessionStatus}'" +
        (SessionStatus == "waiting" && !string.IsNullOrWhiteSpace(WaitReason) ? $" Reason='{WaitReason}'" : "");

    // ── IValidatable ─────────────────────────────────────────────────────────

    public IList<string> Issues { get; } = new List<string>();

    public bool Validate()
    {
        Issues.Clear();

        var validStatuses = new[] { "waiting", "active", "paused" };
        if (!validStatuses.Contains(SessionStatus.Trim().ToLowerInvariant()))
            Issues.Add($"Status must be one of: {string.Join(", ", validStatuses)}");

        RaisePropertyChanged(nameof(Issues));
        return !Issues.Any(i => i.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
    }
}
