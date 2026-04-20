using NINA.Core.Utility;

namespace Subframes.NinaPlugin;

/// <summary>
/// Shared timezone resolution helpers used by session start and station heartbeat paths.
/// </summary>
internal static class TimezoneHelper
{
    /// <summary>
    /// Returns the IANA timezone identifier for the local machine
    /// (e.g. <c>"America/New_York"</c>), or an empty string when conversion
    /// from the Windows timezone ID fails.  The backend interprets an empty
    /// string as "use UTC".  Never throws.
    /// </summary>
    public static string ResolveIanaTimezone()
    {
        try
        {
            var windowsId = TimeZoneInfo.Local.Id;
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var ianaId)
                && !string.IsNullOrEmpty(ianaId))
            {
                Logger.Debug($"[Subframes] Resolved IANA timezone: '{ianaId}' (Windows ID: '{windowsId}')");
                return ianaId;
            }

            // On Linux/macOS NINA builds the ID is already IANA — return it directly.
            if (windowsId.Contains('/'))
            {
                Logger.Debug($"[Subframes] Timezone ID appears to be IANA already: '{windowsId}'");
                return windowsId;
            }

            Logger.Warning($"[Subframes] Could not convert Windows timezone '{windowsId}' to IANA — sending empty string.");
            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Subframes] ResolveIanaTimezone failed: {ex.Message} — sending empty string.");
            return string.Empty;
        }
    }
}
