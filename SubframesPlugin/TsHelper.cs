using System.IO;

namespace Subframes.NinaPlugin;

internal static class TsHelper
{
    internal static string GetTsDbPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "NINA", "SchedulerPlugin", "schedulerdb.sqlite");
    }
}
