using System.ComponentModel.Composition;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using Subframes.NinaPlugin.Api;

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

    [ImportingConstructor]
    public SubframesPlugin(IImageSaveMediator imageSaveMediator)
    {
        var options = PluginOptions.Load();
        var apiClient = new SubframesClient(options);
        _sessionService = new SessionService(imageSaveMediator, apiClient, options);
        Logger.Info("[Subframes] Plugin loaded.");
    }

    // Expose singletons so MEF-constructed sequence items can import them.
    public SessionService SessionService => _sessionService;

    public override async Task Teardown()
    {
        _sessionService.Dispose();
        Logger.Info("[Subframes] Plugin unloaded.");
        await base.Teardown();
    }
}
