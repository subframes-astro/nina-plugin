using System.Collections.ObjectModel;
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
    private readonly SubframesPlugin _plugin;

    [ObservableProperty]
    private string _apiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isDebugEnabled;

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

    [ObservableProperty]
    private int _tsApiPort;

    [ObservableProperty]
    private string _tsDatabasePath = string.Empty;

    [ObservableProperty]
    private string? _selectedTsProfileId;

    /// <summary>Replay engine progress text, e.g. "Syncing 2/5 sessions…" or empty when idle.</summary>
    [ObservableProperty]
    private string _replayStatusText = string.Empty;

    /// <summary>True when multiple TS profiles are available and the user should pick one.</summary>
    public bool ShowProfileSelector => TsProfiles.Count > 1;

    /// <summary>Available TS profiles, populated when TS is active.</summary>
    public ObservableCollection<TsProfileInfo> TsProfiles { get; } = new();

    // Prevents auto-save and heartbeat triggers from firing during ViewModel initialization.
    private bool _initialized;
    private bool _profileSelectorInitialized;

    public OptionsPanelViewModel(SubframesPlugin plugin)
    {
        _plugin = plugin;
        _sessionService = plugin.SessionService;

        // Use the shared options instance from the plugin so that saved changes
        // are immediately visible to SubframesClient (avoids stale-key 401s).
        _options = plugin.Options;
        ApiBaseUrl = _options.ApiBaseUrl;
        ApiKey = _options.ApiKey;
        IsEnabled = _options.IsEnabled;
        IsDebugEnabled = _options.IsDebugEnabled;
        InstanceId = _options.InstanceId;
        InstanceName = _options.InstanceName;
        TsApiPort = _options.TsApiPort;
        TsDatabasePath = _options.TsDatabasePath;

        // Options are fully loaded — auto-save callbacks are now live.
        _initialized = true;

        // Subscribe to profile list updates from the plugin's preview loop.
        plugin.TsProfilesUpdated += OnTsProfilesUpdated;

        // Seed initial profile list (may already be populated if plugin started earlier).
        var initial = plugin.TsProfiles;
        if (initial.Count > 0)
            OnTsProfilesUpdated(initial);

        _profileSelectorInitialized = true;

        RefreshStatus();
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Save()
    {
        AutoSave();
        StatusMessage = "Settings saved.";
    }

    /// <summary>
    /// Persists the current settings to disk. Called automatically when the user leaves a
    /// field or control, and explicitly via <see cref="SaveCommand"/>.
    /// </summary>
    internal void AutoSave()
    {
        _options.ApiBaseUrl = ApiBaseUrl.Trim();
        _options.ApiKey = ApiKey.Trim();
        _options.IsEnabled = IsEnabled;
        _options.IsDebugEnabled = IsDebugEnabled;
        _options.InstanceName = InstanceName.Trim();
        _options.TsApiPort = TsApiPort;
        _options.TsDatabasePath = TsDatabasePath.Trim();
        _options.Save();
        _plugin.ApplyOptionsChange();
        Logger.Info($"[Subframes] Settings saved — API URL: {_options.ApiBaseUrl}  Enabled: {_options.IsEnabled}  Debug: {_options.IsDebugEnabled}");
    }

    // ── Auto-save callbacks ───────────────────────────────────────────────────

    partial void OnIsEnabledChanged(bool value)
    {
        if (_initialized) AutoSave();
    }

    partial void OnIsDebugEnabledChanged(bool value)
    {
        if (_initialized) AutoSave();
    }

    partial void OnApiBaseUrlChanged(string value)
    {
        if (_initialized) AutoSave();
    }

    partial void OnInstanceNameChanged(string value)
    {
        if (_initialized) AutoSave();
    }

    partial void OnTsApiPortChanged(int value)
    {
        if (_initialized) AutoSave();
    }

    partial void OnTsDatabasePathChanged(string value)
    {
        if (_initialized) AutoSave();
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
                ApiStatusText = "Connected";
                ApiStatusBrush = FrozenBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green
                if (keyDetail == "Endpoint not available")
                    Logger.Info($"[Subframes] Connected: {trimmedUrl} — key validation endpoint not deployed, key will be verified on first API call");
                else
                    Logger.Info($"[Subframes] Connected (key verified): {trimmedUrl}");
            }
            else if (keyDetail == "Invalid API key")
            {
                ApiStatusText = "Invalid API Key";
                ApiStatusBrush = FrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber
                var keyPreview = trimmedKey.Length > 12 ? trimmedKey[..12] + "..." : "(short key)";
                Logger.Warning($"[Subframes] API key invalid: {trimmedUrl} (key starts with '{keyPreview}')");
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

    // ── Profile selector ─────────────────────────────────────────────────────

    /// <summary>
    /// Called by CommunityToolkit.Mvvm when SelectedTsProfileId changes.
    /// Notifies the plugin to re-fetch the preview and fire immediate heartbeats.
    /// </summary>
    partial void OnSelectedTsProfileIdChanged(string? value)
    {
        if (!_profileSelectorInitialized || value is null) return;
        _plugin.OnTsProfileSelected(value);
    }

    /// <summary>
    /// Updates the profile dropdown. Called from the plugin's preview loop (thread-pool thread)
    /// and marshalled to the UI dispatcher.
    /// </summary>
    private void OnTsProfilesUpdated(IReadOnlyList<TsProfileInfo> profiles)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            TsProfiles.Clear();
            foreach (var p in profiles)
                TsProfiles.Add(p);

            // Auto-select when only one profile — no UI needed.
            if (profiles.Count == 1)
            {
                SelectedTsProfileId = profiles[0].Id;
            }
            else if (SelectedTsProfileId is null ||
                     !profiles.Any(p => p.Id == SelectedTsProfileId))
            {
                // Restore persisted selection or fall back to the first.
                var saved = _options.SelectedTsProfileId;
                SelectedTsProfileId = profiles.Any(p => p.Id == saved) ? saved : profiles[0].Id;
            }

            OnPropertyChanged(nameof(ShowProfileSelector));
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        var id = _sessionService.ActiveSessionId;
        StatusMessage = id is not null
            ? $"Active session: {id}"
            : "No active session.";

        // Poll replay engine status (null-safe; ReplayEngine is null on secondary instances).
        ReplayStatusText = _plugin.ReplayEngine?.ReplayStatus ?? string.Empty;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
