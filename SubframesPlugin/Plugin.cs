using System.ComponentModel.Composition;
using System.Reflection;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Main plugin entry point.  NINA discovers this via MEF ([Export(typeof(IPluginManifest))]).
/// On load it subscribes to the image save mediator's BeforeImageSaved event so we can
/// ship exposure data to the Subframes API after every captured frame.
/// </summary>
[Export(typeof(IPluginManifest))]
public class SubframesPlugin : PluginBase, IPluginManifest
{
    private readonly SessionService _sessionService;

    [ImportingConstructor]
    public SubframesPlugin(IImageSaveMediator imageSaveMediator)
    {
        // Load persisted options and build the shared singletons.
        var options = PluginOptions.Load();
        var apiClient = new SubframesClient(options);
        _sessionService = new SessionService(imageSaveMediator, apiClient, options);

        // Set plugin manifest properties (non-virtual in NINA 3.1 — assign via setter).
        Name = "Subframes";
        Identifier = "com.subframes.nina-plugin";
        Author = "Subframes";
        MinimumApplicationVersion = "3.0.0.0";
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        Logger.Info("[Subframes] Plugin loaded.");
    }

    // Expose singletons so MEF-constructed sequence items can import them.
    public SessionService SessionService => _sessionService;

    public override void Dispose()
    {
        _sessionService.Dispose();
        Logger.Info("[Subframes] Plugin unloaded.");
        base.Dispose();
    }
}
