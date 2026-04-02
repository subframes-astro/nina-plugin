using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Core.Utility;

namespace Subframes.NinaPlugin;

/// <summary>
/// Persisted settings for the Subframes plugin.
/// Stored in a JSON file alongside the plugin assembly.
/// </summary>
public class PluginOptions
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Subframes", "nina-plugin", "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Base URL for the Subframes API, e.g. http://localhost:8080</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>API key for authenticating with the Subframes API (prefix: astk_live_).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Whether to send data to the API (can be toggled in the UI).</summary>
    public bool IsEnabled { get; set; } = true;

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>Load settings from disk, or return defaults if not present.</summary>
    public static PluginOptions Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<PluginOptions>(json, SerializerOptions)
                       ?? new PluginOptions();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to load settings: {ex.Message}");
        }
        return new PluginOptions();
    }

    /// <summary>Persist settings to disk.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] Failed to save settings: {ex.Message}");
        }
    }
}
