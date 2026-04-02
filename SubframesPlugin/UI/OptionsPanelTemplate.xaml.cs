using System.ComponentModel.Composition;
using System.Windows;

namespace Subframes.NinaPlugin.UI;

/// <summary>
/// MEF-exported ResourceDictionary containing the Subframes plugin options DataTemplate.
/// NINA discovers plugin options panels by searching for exported ResourceDictionary instances
/// that contain a DataTemplate keyed as "{PluginName}_Options".
/// </summary>
[Export(typeof(ResourceDictionary))]
public partial class OptionsPanelTemplate : ResourceDictionary
{
    public OptionsPanelTemplate()
    {
        InitializeComponent();
    }
}
