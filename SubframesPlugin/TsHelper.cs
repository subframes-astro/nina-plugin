using System.IO;

namespace Subframes.NinaPlugin;

internal static class TsHelper
{
    private static string? _configuredDbPath;

    /// <summary>
    /// Sets an optional override for the TS database path shared by all TS readers.
    /// Pass null or empty to revert to the default location:
    /// <c>%localappdata%\NINA\SchedulerPlugin\schedulerdb.sqlite</c>.
    /// </summary>
    internal static void Configure(string? dbPath)
    {
        _configuredDbPath = string.IsNullOrWhiteSpace(dbPath) ? null : dbPath.Trim();
    }

    internal static string GetTsDbPath()
    {
        if (_configuredDbPath is not null)
            return _configuredDbPath;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "NINA", "SchedulerPlugin", "schedulerdb.sqlite");
    }
}
