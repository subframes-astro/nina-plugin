using System.Collections.Concurrent;
using System.Reflection;

namespace Subframes.NinaPlugin.Imaging;

/// <summary>
/// Extracts FWHM, Eccentricity, and PSF-type metrics from NINA's
/// <c>IStarDetectionAnalysis</c> objects using soft reflection.
///
/// <para>
/// Supports two extraction paths:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <b>Hocus Focus path</b> — activated when the runtime type's full name starts with
///       <c>NINA.Joko.Plugin.HocusFocus</c>.  Reads aggregate <c>AverageFWHM</c>,
///       <c>AverageEccentricity</c>, and <c>PSFType</c> via <see cref="PropertyInfo"/>.
///       If aggregates are absent or null, walks <c>DetectedStars</c> and computes the
///       per-star median (requires ≥ 3 valid stars).
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Generic reflection fallback</b> — sweeps a list of known candidate property names
///       on the analysis object and one level into any <c>PSF</c> or <c>Statistics</c>
///       sub-object.  Emits a one-per-session diagnostic when nothing is found.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// No exceptions are propagated to the caller — all errors are caught and logged.
/// The returned result may have any combination of null fields.
/// </para>
///
/// <para>
/// The runtime type is logged at DEBUG level exactly once per (sessionId, typeName) pair
/// so operators can confirm which detector is active without per-frame noise.
/// </para>
/// </summary>
public sealed class StarAnalysisExtractor
{
    // -------------------------------------------------------------------------
    // Known candidate property names for the generic fallback
    // -------------------------------------------------------------------------

    private static readonly string[] FwhmCandidates =
    [
        "AverageFWHM", "FWHM", "Fwhm", "FWHMArcsecs", "MedianFWHM", "StarFWHM"
    ];

    private static readonly string[] EccentricityCandidates =
    [
        "AverageEccentricity", "Eccentricity", "MedianEccentricity"
    ];

    /// <summary>Sub-object property names to probe one level down.</summary>
    private static readonly string[] SubObjectCandidates = ["PSF", "Statistics"];

    // -------------------------------------------------------------------------
    // Hocus Focus namespace prefix
    // -------------------------------------------------------------------------

    private const string HocusFocusPrefix = "NINA.Joko.Plugin.HocusFocus";

    // -------------------------------------------------------------------------
    // Per-session type-logging dedup
    // Key: "{sessionId}:{typeName}" — logs at most once per (session, type) pair.
    // -------------------------------------------------------------------------

    private readonly ConcurrentDictionary<string, byte> _loggedKeys = new();

    // Per-session single FWHM/Eccentricity-not-resolved warning dedup.
    private readonly ConcurrentDictionary<string, byte> _warnedKeys = new();

    // -------------------------------------------------------------------------
    // Reflection cache — PropertyInfo lookup per (type, propertyName)
    // -------------------------------------------------------------------------

    private readonly ConcurrentDictionary<(Type, string), PropertyInfo?> _propCache = new();

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts FWHM, Eccentricity, and PSF type from <paramref name="analysis"/>.
    /// </summary>
    /// <param name="analysis">
    /// The analysis object returned by NINA (typically implements
    /// <c>NINA.Image.Interfaces.IStarDetectionAnalysis</c>).
    /// Passing <c>null</c> returns <see cref="StarAnalysisResult.Empty"/>.
    /// </param>
    /// <param name="sessionId">
    /// Active session ID used to gate per-session diagnostic logging.
    /// Pass <c>null</c> to disable per-session dedup (all diagnostics will fire every call).
    /// </param>
    /// <returns>
    /// An extraction result; any or all fields may be null.  Never throws.
    /// </returns>
    public StarAnalysisResult Extract(object? analysis, string? sessionId)
    {
        if (analysis is null)
            return StarAnalysisResult.Empty;

        try
        {
            var type = analysis.GetType();
            var typeName = type.FullName ?? type.Name;
            return ExtractImpl(analysis, type, typeName, sessionId);
        }
        catch (Exception ex)
        {
            SubframesLogger.Warning(
                $"StarAnalysisExtractor: unexpected error; FWHM/Eccentricity will be null. {ex.Message}");
            return StarAnalysisResult.Empty;
        }
    }

    /// <summary>
    /// Test hook: extracts using a caller-supplied <paramref name="effectiveTypeName"/> instead
    /// of the runtime type's <c>FullName</c>.  This allows unit tests to exercise the
    /// Hocus Focus code path without a real HF assembly.
    /// </summary>
    internal StarAnalysisResult ExtractForTest(
        object analysis, string effectiveTypeName, string? sessionId)
    {
        var type = analysis.GetType();
        return ExtractImpl(analysis, type, effectiveTypeName, sessionId);
    }

    private StarAnalysisResult ExtractImpl(
        object analysis, Type type, string typeName, string? sessionId)
    {
        LogTypeOnce(typeName, sessionId);

        return IsHocusFocusType(typeName)
            ? ExtractHocusFocus(analysis, type, typeName, sessionId)
            : ExtractGenericFallback(analysis, type, typeName, sessionId);
    }

    // -------------------------------------------------------------------------
    // Hocus Focus path
    // -------------------------------------------------------------------------

    private StarAnalysisResult ExtractHocusFocus(
        object analysis, Type type, string typeName, string? sessionId)
    {
        double? fwhm = ReadDouble(analysis, type, "AverageFWHM");
        double? eccentricity = ReadDouble(analysis, type, "AverageEccentricity");
        string? psfType = ReadString(analysis, type, "PSFType");

        // If aggregates are missing, fall back to per-star median from DetectedStars
        if (fwhm is null || eccentricity is null)
        {
            var (medianFwhm, medianEcc) = ComputeMedianFromDetectedStars(analysis, type);

            fwhm ??= medianFwhm;
            eccentricity ??= medianEcc;

            if (medianFwhm.HasValue || medianEcc.HasValue)
            {
                SubframesLogger.Debug(
                    $"HocusFocus aggregate FWHM/Eccentricity unavailable " +
                    $"— computed median from DetectedStars (type={typeName})");
            }
        }

        return new StarAnalysisResult
        {
            Fwhm = fwhm,
            Eccentricity = eccentricity,
            PsfType = psfType
        };
    }

    /// <summary>
    /// Reads <c>DetectedStars</c> from the analysis object and computes per-star medians.
    /// Requires ≥ 3 valid (non-null, non-NaN, non-negative) values to return a result.
    /// </summary>
    private (double? fwhm, double? eccentricity) ComputeMedianFromDetectedStars(
        object analysis, Type type)
    {
        var starsProperty = GetCachedProperty(type, "DetectedStars");
        if (starsProperty is null)
            return (null, null);

        var starsObj = starsProperty.GetValue(analysis);
        if (starsObj is not System.Collections.IEnumerable stars)
            return (null, null);

        var fwhmValues = new List<double>();
        var eccValues = new List<double>();

        foreach (var star in stars)
        {
            if (star is null) continue;

            var starType = star.GetType();

            var fwhmVal = ReadDouble(star, starType, "FWHMArcsecs");
            if (fwhmVal is > 0 && !double.IsNaN(fwhmVal.Value))
                fwhmValues.Add(fwhmVal.Value);

            var eccVal = ReadDouble(star, starType, "Eccentricity");
            if (eccVal is >= 0 and <= 1 && !double.IsNaN(eccVal.Value))
                eccValues.Add(eccVal.Value);
        }

        const int minStars = 3;

        double? medianFwhm = fwhmValues.Count >= minStars
            ? ComputeMedian(fwhmValues)
            : null;

        double? medianEcc = eccValues.Count >= minStars
            ? ComputeMedian(eccValues)
            : null;

        return (medianFwhm, medianEcc);
    }

    // -------------------------------------------------------------------------
    // Generic reflection fallback
    // -------------------------------------------------------------------------

    private StarAnalysisResult ExtractGenericFallback(
        object analysis, Type type, string typeName, string? sessionId)
    {
        double? fwhm = TrySweepFwhmCandidates(analysis, type);
        double? eccentricity = TrySweepEccentricityCandidates(analysis, type);

        if (fwhm is null && eccentricity is null)
        {
            WarnOncePerSession(typeName, sessionId);
        }

        return new StarAnalysisResult
        {
            Fwhm = fwhm,
            Eccentricity = eccentricity
        };
    }

    /// <summary>
    /// Sweeps FWHM candidate names on <paramref name="obj"/> and one level into
    /// <c>PSF</c> / <c>Statistics</c> sub-objects.
    /// </summary>
    private double? TrySweepFwhmCandidates(object obj, Type type)
    {
        foreach (var name in FwhmCandidates)
        {
            var value = ReadDouble(obj, type, name);
            if (value is > 0) return value;
        }

        // Try one level into sub-objects
        foreach (var subName in SubObjectCandidates)
        {
            var sub = GetCachedProperty(type, subName)?.GetValue(obj);
            if (sub is null) continue;
            var subType = sub.GetType();
            foreach (var name in FwhmCandidates)
            {
                var value = ReadDouble(sub, subType, name);
                if (value is > 0) return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Sweeps Eccentricity candidate names on <paramref name="obj"/> and one level into
    /// <c>PSF</c> / <c>Statistics</c> sub-objects.
    /// </summary>
    private double? TrySweepEccentricityCandidates(object obj, Type type)
    {
        foreach (var name in EccentricityCandidates)
        {
            var value = ReadDouble(obj, type, name);
            if (value is >= 0 and <= 1) return value;
        }

        foreach (var subName in SubObjectCandidates)
        {
            var sub = GetCachedProperty(type, subName)?.GetValue(obj);
            if (sub is null) continue;
            var subType = sub.GetType();
            foreach (var name in EccentricityCandidates)
            {
                var value = ReadDouble(sub, subType, name);
                if (value is >= 0 and <= 1) return value;
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Logging helpers
    // -------------------------------------------------------------------------

    private void LogTypeOnce(string typeName, string? sessionId)
    {
        var key = sessionId is null ? typeName : $"{sessionId}:{typeName}";
        if (_loggedKeys.TryAdd(key, 0))
        {
            SubframesLogger.Debug($"Star analysis type resolved: {typeName}");
        }
    }

    private void WarnOncePerSession(string typeName, string? sessionId)
    {
        var key = sessionId is null ? typeName : $"{sessionId}:{typeName}";
        if (_warnedKeys.TryAdd(key, 0))
        {
            SubframesLogger.Warning(
                $"FWHM/Eccentricity not resolved: analysisType={typeName}; " +
                $"tried {string.Join(", ", FwhmCandidates)}, {string.Join(", ", EccentricityCandidates)}. " +
                "Hocus Focus PSF fit may be disabled or a stock star detector is active.");
        }
    }

    // -------------------------------------------------------------------------
    // Reflection helpers
    // -------------------------------------------------------------------------

    private static bool IsHocusFocusType(string typeName) =>
        typeName.StartsWith(HocusFocusPrefix, StringComparison.Ordinal);

    private double? ReadDouble(object obj, Type type, string propertyName)
    {
        try
        {
            var prop = GetCachedProperty(type, propertyName);
            if (prop is null) return null;

            var raw = prop.GetValue(obj);
            return raw switch
            {
                double d => d,
                float f => (double)f,
                decimal m => (double)m,
                int i => (double)i,
                long l => (double)l,
                null => null,
                _ => Convert.ToDouble(raw)
            };
        }
        catch
        {
            return null;
        }
    }

    private string? ReadString(object obj, Type type, string propertyName)
    {
        try
        {
            var prop = GetCachedProperty(type, propertyName);
            if (prop is null) return null;

            var raw = prop.GetValue(obj);
            return raw?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private PropertyInfo? GetCachedProperty(Type type, string name) =>
        _propCache.GetOrAdd(
            (type, name),
            static key => key.Item1.GetProperty(
                key.Item2,
                BindingFlags.Public | BindingFlags.Instance));

    // -------------------------------------------------------------------------
    // Math helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the median of a sorted or unsorted list of doubles.
    /// Caller must guarantee the list is non-empty.
    /// </summary>
    internal static double ComputeMedian(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[mid - 1] + values[mid]) / 2.0
            : values[mid];
    }
}

/// <summary>
/// Result of extracting FWHM/Eccentricity/PSF type from a star-detection analysis object.
/// </summary>
public sealed record StarAnalysisResult
{
    /// <summary>FWHM in arcseconds, or null if unavailable.</summary>
    public double? Fwhm { get; init; }

    /// <summary>Average eccentricity, or null if unavailable.</summary>
    public double? Eccentricity { get; init; }

    /// <summary>PSF model type string (Hocus Focus only), or null if unavailable.</summary>
    public string? PsfType { get; init; }

    /// <summary>Sentinel representing a completely empty extraction result.</summary>
    public static readonly StarAnalysisResult Empty = new();
}
