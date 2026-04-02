using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
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

    [ImportingConstructor]
    public SubframesPlugin(IImageSaveMediator imageSaveMediator, IProfileService profileService)
    {
        var options = PluginOptions.Load();
        var apiClient = new SubframesClient(options);
        _sessionService = new SessionService(imageSaveMediator, apiClient, options);
        _optionsVm = new OptionsPanelViewModel(this, profileService);
        Logger.Info("[Subframes] Plugin loaded.");
    }

    // Expose singletons so MEF-constructed sequence items can import them.
    public SessionService SessionService => _sessionService;
    public OptionsPanelViewModel OptionsVM => _optionsVm;

    public override ResourceDictionary Options
    {
        get
        {
            var dict = new ResourceDictionary();
            dict.Source = new Uri(
                "pack://application:,,,/Subframes.NinaPlugin;component/UI/OptionsPanelTemplate.xaml");
            return dict;
        }
    }

    public override async Task Teardown()
    {
        _sessionService.Dispose();
        Logger.Info("[Subframes] Plugin unloaded.");
        await base.Teardown();
    }
}
