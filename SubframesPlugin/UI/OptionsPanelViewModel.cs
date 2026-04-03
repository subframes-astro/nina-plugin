using System.ComponentModel.Composition;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using Subframes.NinaPlugin.Api;

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
///   - CheckApiConnectionCommand — tests connectivity to the configured API server
/// </summary>
public partial class OptionsPanelViewModel : ObservableObject
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

    [ObservableProperty]
    private string _apiStatusText = string.Empty;

    [ObservableProperty]
    private SolidColorBrush _apiStatusBrush = FrozenBrush(Colors.Gray);

    [ObservableProperty]
    private bool _isCheckingApi;

    public OptionsPanelViewModel(SubframesPlugin plugin)
    {
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

    [RelayCommand]
    private async Task CheckApiConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl))
        {
            ApiStatusText = "No URL configured";
            ApiStatusBrush = FrozenBrush(Colors.Gray);
            return;
        }

        IsCheckingApi = true;
        ApiStatusText = "Checking...";
        ApiStatusBrush = FrozenBrush(Colors.Gray);

        try
        {
            var trimmedUrl = ApiBaseUrl.Trim();
            var trimmedKey = ApiKey?.Trim() ?? string.Empty;

            var (connected, healthDetail) = await SubframesClient.CheckHealthAsync(trimmedUrl, trimmedKey);

            if (!connected)
            {
                ApiStatusText = "Disconnected";
                ApiStatusBrush = FrozenBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // red
                Logger.Warning($"[Subframes] API health check failed: {trimmedUrl} — {healthDetail}");
                return;
            }

            if (string.IsNullOrWhiteSpace(trimmedKey))
            {
                ApiStatusText = "Connected (no API key)";
                ApiStatusBrush = FrozenBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green
                Logger.Info($"[Subframes] API health check passed (no API key): {trimmedUrl}");
                return;
            }

            var (valid, keyDetail) = await SubframesClient.ValidateApiKeyAsync(trimmedUrl, trimmedKey);

            if (valid)
            {
                var suffix = keyDetail == "Endpoint not available" ? " (key not verified)" : "";
                ApiStatusText = $"Connected{suffix}";
                ApiStatusBrush = FrozenBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green
                Logger.Info($"[Subframes] API key validated: {trimmedUrl}{(suffix.Length > 0 ? " — validation endpoint not deployed" : "")}");
            }
            else if (keyDetail == "Invalid API key")
            {
                ApiStatusText = "Invalid API Key";
                ApiStatusBrush = FrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber
                Logger.Warning($"[Subframes] API key invalid: {trimmedUrl}");
            }
            else
            {
                ApiStatusText = "Key validation failed";
                ApiStatusBrush = FrozenBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // red
                Logger.Error($"[Subframes] API key validation error: {trimmedUrl} — {keyDetail}");
            }
        }
        catch (Exception ex)
        {
            ApiStatusText = "Error";
            ApiStatusBrush = FrozenBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // red
            Logger.Error($"[Subframes] API connection check error: {ex.Message}");
        }
        finally
        {
            IsCheckingApi = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        var id = _sessionService.ActiveSessionId;
        StatusMessage = id is not null
            ? $"Active session: {id}"
            : "No active session.";
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
