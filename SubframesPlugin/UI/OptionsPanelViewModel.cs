using System.ComponentModel.Composition;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

namespace Subframes.NinaPlugin.UI;

/// <summary>
/// ViewModel for the Subframes plugin settings panel.
///
/// Exposes:
///   - ApiBaseUrl    — user-editable text field
///   - IsEnabled     — enable/disable data posting without removing the item
///   - InstanceId    — read-only stable identifier for this NINA instance
///   - InstanceName  — user-editable friendly name for this NINA instance
///   - SaveCommand   — persists settings to disk
///   - StatusMessage — live status string showing the active session ID (if any)
/// </summary>
public partial class OptionsPanelViewModel : DockableVM
{
    private readonly PluginOptions _options;
    private readonly SessionService _sessionService;

    [ObservableProperty]
    private string _apiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _instanceId = string.Empty;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public OptionsPanelViewModel(SubframesPlugin plugin, IProfileService profileService)
        : base(profileService)
    {
        Title = "Subframes";
        _sessionService = plugin.SessionService;

        // Load from disk.
        _options = PluginOptions.Load();
        ApiBaseUrl = _options.ApiBaseUrl;
        ApiKey = _options.ApiKey;
        IsEnabled = _options.IsEnabled;
        InstanceId = _options.InstanceId;
        InstanceName = _options.InstanceName;

        RefreshStatus();
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Save()
    {
        _options.ApiBaseUrl = ApiBaseUrl.Trim();
        _options.ApiKey = ApiKey.Trim();
        _options.IsEnabled = IsEnabled;
        _options.InstanceName = InstanceName.Trim();
        _options.Save();
        StatusMessage = "Settings saved.";
        Logger.Info($"[Subframes] Settings saved — API URL: {_options.ApiBaseUrl}  Enabled: {_options.IsEnabled}");
    }

    [RelayCommand]
    private void Refresh() => RefreshStatus();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        var id = _sessionService.ActiveSessionId;
        StatusMessage = id is not null
            ? $"Active session: {id}"
            : "No active session.";
    }
}
