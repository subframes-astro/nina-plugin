namespace Subframes.NinaPlugin;

/// <summary>
/// Pure decision logic for target-block tracking and session idle/waiting transitions.
/// Kept free of NINA SDK dependencies so it can be unit-tested directly.
/// </summary>
public static class TargetBlockPolicy
{
    /// <summary>Default coordinate tolerance (degrees) for "same target" comparison.</summary>
    public const double DefaultToleranceDegrees = 0.01;

    /// <summary>
    /// Returns true when an incoming target is the same block as the one already being tracked.
    /// Requires a target to already be registered (<paramref name="hasActiveTarget"/>), equal
    /// normalized names, and coordinates within tolerance. A (0,0) coordinate pair on either
    /// side means "unknown" and is ignored (name-only comparison).
    /// </summary>
    /// <param name="hasActiveTarget">True if a target row exists (server-acked OR locally cached).</param>
    public static bool IsSameTarget(
        bool hasActiveTarget,
        string? currentName, double currentRaDeg, double currentDecDeg,
        string? newName, double newRaDeg, double newDecDeg,
        double toleranceDegrees = DefaultToleranceDegrees)
    {
        if (!hasActiveTarget) return false;
        if (string.IsNullOrEmpty(currentName) || string.IsNullOrEmpty(newName)) return false;

        var a = CatalogNameNormalizer.Normalize(currentName);
        var b = CatalogNameNormalizer.Normalize(newName);
        if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;

        return CoordinatesMatch(currentRaDeg, currentDecDeg, newRaDeg, newDecDeg, toleranceDegrees);
    }

    /// <summary>True if two RA/Dec pairs (degrees) are within tolerance; unknown (0,0) matches anything.</summary>
    public static bool CoordinatesMatch(double ra1, double dec1, double ra2, double dec2, double tol)
    {
        if ((ra1 == 0 && dec1 == 0) || (ra2 == 0 && dec2 == 0)) return true;
        var dRa = Math.Abs(ra1 - ra2) % 360.0;
        if (dRa > 180.0) dRa = 360.0 - dRa;
        dRa *= Math.Cos(((dec1 + dec2) / 2.0) * Math.PI / 180.0);
        return Math.Abs(dRa) <= tol && Math.Abs(dec1 - dec2) <= tol;
    }
}

/// <summary>What the heartbeat loop should do about an idle session.</summary>
public enum IdleAction { None, MarkWaiting, Close }

/// <summary>Idle/waiting/close decisions for the session heartbeat loop.</summary>
public static class SessionIdlePolicy
{
    /// <summary>
    /// Decide how to treat a session with no recent activity.
    /// Auto sessions go "waiting" after <paramref name="timeoutMinutes"/> and close after
    /// <paramref name="maxIdleMinutes"/>. Manual sessions are only closed on idle when the
    /// sequence has stopped (<paramref name="sequenceStopped"/>).
    /// </summary>
    public static IdleAction Evaluate(
        bool isManual, bool sequenceStopped, bool alreadyWaiting,
        double idleMinutes, int timeoutMinutes, int maxIdleMinutes)
    {
        if (isManual && !sequenceStopped) return IdleAction.None;
        if (maxIdleMinutes > 0 && idleMinutes >= maxIdleMinutes) return IdleAction.Close;
        if (!isManual && timeoutMinutes > 0 && idleMinutes >= timeoutMinutes && !alreadyWaiting)
            return IdleAction.MarkWaiting;
        return IdleAction.None;
    }
}

/// <summary>Approximate "has dawn broken?" check used to decide whether a finished sequence ends the night.</summary>
public static class DawnPolicy
{
    /// <summary>
    /// True when it is morning (before solar noon) and the sun is above nautical twilight (-12°).
    /// Returns false when the location is unknown (0,0) so the session is kept waiting
    /// and closed by the max-idle timeout instead.
    /// </summary>
    public static bool IsAfterDawn(double latitudeDeg, double longitudeDeg, DateTime utcNow)
    {
        if (latitudeDeg == 0.0 && longitudeDeg == 0.0) return false;

        var (elevation, localSolarHour) = SolarPosition(latitudeDeg, longitudeDeg, utcNow);
        return localSolarHour < 12.0 && elevation > -12.0;
    }

    /// <summary>Low-precision solar elevation (deg) and local apparent solar hour (0-24).</summary>
    internal static (double elevationDeg, double solarHour) SolarPosition(double lat, double lon, DateTime utc)
    {
        utc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        double dayOfYear = utc.DayOfYear + (utc.Hour + utc.Minute / 60.0) / 24.0;
        double g = 2 * Math.PI / 365.0 * (dayOfYear - 1);
        double eqTimeMin = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g)
                            - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
        double decl = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g)
                      - 0.006758 * Math.Cos(2 * g) + 0.000907 * Math.Sin(2 * g)
                      - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
        double utcMin = utc.Hour * 60 + utc.Minute + utc.Second / 60.0;
        double solarMin = ((utcMin + eqTimeMin + 4 * lon) % 1440 + 1440) % 1440;
        double solarHour = solarMin / 60.0;
        double ha = (solarMin / 4.0 - 180.0) * Math.PI / 180.0;
        double latR = lat * Math.PI / 180.0;
        double sinEl = Math.Sin(latR) * Math.Sin(decl) + Math.Cos(latR) * Math.Cos(decl) * Math.Cos(ha);
        return (Math.Asin(Math.Clamp(sinEl, -1, 1)) * 180.0 / Math.PI, solarHour);
    }
}
