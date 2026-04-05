using System.ComponentModel.Composition;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;

namespace Subframes.NinaPlugin.Sequence;

/// <summary>
/// NINA sequence item: "End Subframes Session".
///
/// The user can add this to the end of their sequence (typically in the
/// "Sequence End" area) to explicitly close the active Subframes session and
/// flush any remaining frame data to the API.
///
/// If no session is active the item logs a warning and returns without error
/// so the sequence is never aborted.
/// </summary>
[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "End Subframes Session")]
[ExportMetadata("Description", "Ends the active Subframes session and flushes any remaining frame data.")]
[ExportMetadata("Icon", "Subframes_SVG")]
[ExportMetadata("Category", "Subframes")]
public sealed class EndSessionItem : SequenceItem, IValidatable
{
    private readonly SessionService _sessionService;

    [ImportingConstructor]
    public EndSessionItem(SubframesPlugin plugin)
    {
        _sessionService = plugin.SessionService;
    }

    // Copy constructor for Clone().
    private EndSessionItem(EndSessionItem other) : base(other)
    {
        _sessionService = other._sessionService;
    }

    // ── ISequenceItem ────────────────────────────────────────────────────────

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken ct)
    {
        progress.Report(new ApplicationStatus { Status = "Subframes: ending session..." });

        if (!_sessionService.HasActiveSession)
        {
            Logger.Warning("[Subframes] EndSessionItem: no active session to end.");
            progress.Report(new ApplicationStatus { Status = "Subframes: no active session." });
            return;
        }

        await _sessionService.EndSessionAsync(ct);

        Logger.Info("[Subframes] EndSessionItem complete — session ended.");
        progress.Report(new ApplicationStatus { Status = "Subframes: session ended." });
    }

    public override object Clone() => new EndSessionItem(this);

    public override string ToString() => "[EndSubframesSession]";

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
