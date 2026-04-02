using System.ComponentModel.Composition;
using System.Reflection;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Main plugin entry point.  NINA discovers this via MEF ([Export(typeof(IPluginManifest))]).
/// On load it subscribes to the imaging mediator's ImageSaved event so we can
/// ship exposure data to the Subframes API after every captured frame.
/// </summary>
[Export(typeof(IPluginManifest))]
public class SubframesPlugin : PluginBase, IPluginManifest
{
    private readonly SessionService _sessionService;

    [ImportingConstructor]
    public SubframesPlugin(IImagingMediator imagingMediator)
    {
        // Load persisted options and build the shared singletons.
        var options = PluginOptions.Load();
        var apiClient = new SubframesClient(options);
        _sessionService = new SessionService(imagingMediator, apiClient, options);

        Logger.Info("[Subframes] Plugin loaded.");
    }

    // ── IPluginManifest ──────────────────────────────────────────────────────

    public override string Name => "Subframes";
    public override string Identifier => "com.subframes.nina-plugin";
    public override string Version => Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString(3) ?? "0.1.0";
    public override string Author => "Subframes";
    public override string Description =>
        "Captures exposure telemetry from NINA and sends it to the Subframes API in real time.";
    public override string MinimumApplicationVersion => "3.0.0.0";
    public override Uri? RepositoryUrl => null;
    public override Uri? DownloadUrl => null;
    public override BitmapSource? Logo => null;

    // Expose singletons so MEF-constructed sequence items can import them.
    public SessionService SessionService => _sessionService;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sessionService.Dispose();
            Logger.Info("[Subframes] Plugin unloaded.");
        }
        base.Dispose(disposing);
    }
}
