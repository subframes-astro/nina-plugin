namespace Subframes.NinaPlugin;

/// <summary>
/// Helpers for filtering frames by type so that calibration frames
/// (DARK, BIAS, FLAT, SNAPSHOT, …) are excluded from session ingest.
/// </summary>
internal static class FrameTypeFilter
{
    /// <summary>
    /// Returns <c>true</c> only when <paramref name="imageType"/> is exactly
    /// <c>"LIGHT"</c> (case-insensitive).  Null and empty values are treated
    /// as non-LIGHT and return <c>false</c>.
    /// </summary>
    internal static bool IsLightFrame(string? imageType) =>
        string.Equals(imageType, "LIGHT", StringComparison.OrdinalIgnoreCase);
}
