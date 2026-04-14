using System.Windows;
using System.Windows.Controls;

namespace Subframes.NinaPlugin.UI;

/// <summary>Code-behind for the Subframes settings panel. All logic lives in the ViewModel.</summary>
public partial class OptionsPanel : UserControl
{
    public OptionsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is OptionsPanelViewModel vm)
            ApiKeyBox.Password = vm.ApiKey;
    }

    internal void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsPanelViewModel vm)
            vm.ApiKey = ApiKeyBox.Password;
    }
}
