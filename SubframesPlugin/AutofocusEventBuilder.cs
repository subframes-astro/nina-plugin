using System.Reflection;
using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Builds the <see cref="EventRequest"/> payload for an autofocus completion event.
///
/// Extracted as a static helper so the payload construction logic can be unit-tested
/// independently of NINA SDK types. <see cref="SessionService.UpdateEndAutoFocusRun"/>
/// calls this method and passes the result to <c>SubframesClient.PostEventAsync</c>.
///
/// When Hocus Focus is installed, the method attempts to reflect on the concrete
/// <c>AutoFocusInfo</c> subtype to extract enriched metrics (achievedHfr, initialHfr,
/// rSquared, curveFitting, duration, stepCount, success, starCount). All enriched
/// fields are optional — when Hocus Focus is absent or the reflection fails the
/// payload falls back to the standard minimal schema (filter / temperature / position).
/// </summary>
internal static class AutofocusEventBuilder
{
    // ── Hocus Focus assembly detection ───────────────────────────────────────

    /// <summary>
    /// Detects the Hocus Focus plugin assembly in the current AppDomain.
    /// Returns the assembly when found, or null when Hocus Focus is not installed.
    /// The result is cached after the first call so assembly scanning only runs once.
    /// </summary>
    private static System.Reflection.Assembly? _hfAssembly;
    private static bool _hfDetected;

    internal static System.Reflection.Assembly? DetectHocusFocus()
    {
        if (_hfDetected) return _hfAssembly;
        _hfAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a =>
            {
                var name = a.GetName().Name;
                return name != null && name.IndexOf("HocusFocus", StringComparison.OrdinalIgnoreCase) >= 0;
            });
        _hfDetected = true;
        return _hfAssembly;
    }

    // ── Reflection property caches for AutoFocusInfo HF enrichment ───────────

    // Resolved once per process; null means not found on this type.
    private static PropertyInfo? _achievedHfrProp;
    private static PropertyInfo? _initialHfrProp;
    private static PropertyInfo? _rSquaredProp;
    private static PropertyInfo? _curveFittingProp;
    private static PropertyInfo? _durationProp;
    private static PropertyInfo? _stepCountProp;
    private static PropertyInfo? _successProp;
    private static PropertyInfo? _starCountProp;
    private static bool _hfPropsResolved;

    // ── Public Build overloads ────────────────────────────────────────────────

    /// <summary>
    /// Constructs an <see cref="EventRequest"/> for an autofocus completion event
    /// using only the standard minimal fields available from NINA's stock
    /// <see cref="NINA.Core.Model.Equipment.AutoFocusInfo"/> struct.
    /// </summary>
    /// <param name="sessionId">Active session ID assigned by the Subframes backend.</param>
    /// <param name="filter">Name of the filter in use during autofocus, or null when unknown.</param>
    /// <param name="temperature">Ambient temperature in °C at the time of the run, or null when unavailable.</param>
    /// <param name="position">Final focuser step position after the run.</param>
    /// <returns>An <see cref="EventRequest"/> ready to be passed to <c>PostEventAsync</c>.</returns>
    internal static EventRequest Build(
        string sessionId,
        string? filter,
        double? temperature,
        int position)
    {
        return new EventRequest
        {
            SessionId = sessionId,
            EventType = "autofocus",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Metadata  = new Dictionary<string, object?>
            {
                ["filter"]      = filter,
                ["temperature"] = temperature,
                ["position"]    = position,
            },
        };
    }

    /// <summary>
    /// Constructs an enriched <see cref="EventRequest"/> for an autofocus completion event.
    ///
    /// Attempts to extract Hocus Focus metrics from the concrete runtime type of
    /// <paramref name="autofocusInfoObj"/> via reflection. When Hocus Focus is installed
    /// and the properties are found, the metadata includes <c>source</c>, <c>sourceVersion</c>,
    /// <c>achievedHfr</c>, <c>initialHfr</c>, <c>rSquared</c>, <c>curveFitting</c>,
    /// <c>duration</c>, <c>stepCount</c>, <c>success</c>, and <c>starCount</c>.
    /// Falls back to the minimal schema when Hocus Focus is absent or reflection fails.
    /// </summary>
    /// <param name="sessionId">Active session ID assigned by the Subframes backend.</param>
    /// <param name="filter">Filter name from the standard NINA <c>AutoFocusInfo</c>.</param>
    /// <param name="temperature">Temperature in °C from the standard <c>AutoFocusInfo</c>.</param>
    /// <param name="position">Final focuser position from the standard <c>AutoFocusInfo</c>.</param>
    /// <param name="autofocusInfoObj">
    ///   The runtime <c>AutoFocusInfo</c> object passed to <c>UpdateEndAutoFocusRun</c>.
    ///   May be a Hocus Focus subtype with additional properties.
    /// </param>
    /// <returns>An <see cref="EventRequest"/> ready to be passed to <c>PostEventAsync</c>.</returns>
    internal static EventRequest Build(
        string sessionId,
        string? filter,
        double? temperature,
        int position,
        object autofocusInfoObj)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["filter"]      = filter,
            ["temperature"] = temperature,
            ["position"]    = position,
        };

        try
        {
            var hfAssembly = DetectHocusFocus();
            if (hfAssembly is not null)
            {
                EnrichWithHocusFocusData(autofocusInfoObj, hfAssembly, metadata);
            }
        }
        catch (Exception)
        {
            // Enrichment failed — the caller (SessionService) logs the error
            // with the full exception; the builder degrades silently.
        }

        return new EventRequest
        {
            SessionId = sessionId,
            EventType = "autofocus",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Metadata  = metadata,
        };
    }

    // ── Hocus Focus reflection helpers ────────────────────────────────────────

    /// <summary>
    /// Reflects on <paramref name="autofocusInfoObj"/> to extract Hocus Focus–specific
    /// properties and adds them to <paramref name="metadata"/>. Adds <c>source</c> and
    /// <c>sourceVersion</c> fields so downstream consumers can identify the enrichment origin.
    /// </summary>
    private static void EnrichWithHocusFocusData(
        object autofocusInfoObj,
        System.Reflection.Assembly hfAssembly,
        Dictionary<string, object?> metadata)
    {
        if (!_hfPropsResolved)
            ResolveHFProperties(autofocusInfoObj);

        // Add source attribution so the backend/frontend know this is enriched HF data.
        metadata["source"]        = "hocus_focus";
        metadata["sourceVersion"] = hfAssembly.GetName().Version?.ToString();

        // achievedHfr — best HFR at final focus position.
        var achievedHfr = ReadDouble(autofocusInfoObj, _achievedHfrProp);
        if (achievedHfr.HasValue)
            metadata["achievedHfr"] = achievedHfr;

        // initialHfr — HFR before autofocus began (measures improvement).
        var initialHfr = ReadDouble(autofocusInfoObj, _initialHfrProp);
        if (initialHfr.HasValue)
            metadata["initialHfr"] = initialHfr;

        // rSquared — goodness-of-fit for the focus curve.
        var rSquared = ReadDouble(autofocusInfoObj, _rSquaredProp);
        if (rSquared.HasValue)
            metadata["rSquared"] = rSquared;

        // curveFitting — string name of the curve fitting method (Hyperbolic, etc.).
        var curveFitting = ReadString(autofocusInfoObj, _curveFittingProp);
        if (curveFitting is not null)
            metadata["curveFitting"] = curveFitting;

        // duration — total autofocus run duration in seconds.
        var duration = ReadDurationSeconds(autofocusInfoObj, _durationProp);
        if (duration.HasValue)
            metadata["duration"] = duration;

        // stepCount — number of focus points sampled during the run.
        var stepCount = ReadInt(autofocusInfoObj, _stepCountProp);
        if (stepCount.HasValue)
            metadata["stepCount"] = stepCount;

        // success — whether the AF run converged to a solution.
        var success = ReadBool(autofocusInfoObj, _successProp);
        if (success.HasValue)
            metadata["success"] = success;

        // starCount — number of stars used for analysis.
        var starCount = ReadInt(autofocusInfoObj, _starCountProp);
        if (starCount.HasValue)
            metadata["starCount"] = starCount;
    }

    /// <summary>
    /// Resolves and caches Hocus Focus reflection properties from the runtime type of
    /// <paramref name="autofocusInfoObj"/>. Called once per process.
    /// </summary>
    private static void ResolveHFProperties(object autofocusInfoObj)
    {
        var type = autofocusInfoObj.GetType();

        _achievedHfrProp  = FindProperty(type, "AchievedHFR",  "FinalHFR",   "HFR");
        _initialHfrProp   = FindProperty(type, "InitialHFR",   "StartHFR");
        _rSquaredProp     = FindProperty(type, "RSquared",     "GoodnessOfFit", "R2", "Rsquared");
        _curveFittingProp = FindProperty(type, "CurveFittingMethod", "FittingMethod", "CurveFit");
        _durationProp     = FindProperty(type, "Duration",     "RunDuration", "Elapsed");
        _stepCountProp    = FindProperty(type, "NumberOfSteps","StepCount",   "Steps");
        _successProp      = FindProperty(type, "Succeeded",    "Success",     "IsSuccess");
        _starCountProp    = FindProperty(type, "NumberOfStars","StarCount",   "Stars");

        _hfPropsResolved = true;

        // Property resolution logged by the caller (SessionService) so the builder
        // stays free of NINA SDK logging dependencies and is testable in isolation.
    }

    // ── Low-level reflection value readers ───────────────────────────────────

    private static PropertyInfo? FindProperty(Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is not null) return prop;
        }
        return null;
    }

    private static double? ReadDouble(object obj, PropertyInfo? prop)
    {
        if (prop is null) return null;
        try
        {
            var val = prop.GetValue(obj);
            double? d = val switch
            {
                double dv  => dv,
                float fv   => (double)fv,
                decimal mv => (double)mv,
                _          => null,
            };
            return d is double dbl && double.IsFinite(dbl) && dbl >= 0 ? dbl : null;
        }
        catch { return null; }
    }

    private static string? ReadString(object obj, PropertyInfo? prop)
    {
        if (prop is null) return null;
        try
        {
            var val = prop.GetValue(obj);
            return val?.ToString() is string s && s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads a duration property and returns its value in seconds.
    /// Handles <see cref="TimeSpan"/> and plain numeric types.
    /// </summary>
    private static double? ReadDurationSeconds(object obj, PropertyInfo? prop)
    {
        if (prop is null) return null;
        try
        {
            var val = prop.GetValue(obj);
            double? seconds = val switch
            {
                TimeSpan ts => ts.TotalSeconds,
                double dv   => dv,
                float fv    => (double)fv,
                int iv      => (double)iv,
                long lv     => (double)lv,
                _           => null,
            };
            return seconds is double d && double.IsFinite(d) && d >= 0 ? d : null;
        }
        catch { return null; }
    }

    private static int? ReadInt(object obj, PropertyInfo? prop)
    {
        if (prop is null) return null;
        try
        {
            var val = prop.GetValue(obj);
            int? i = val switch
            {
                int iv       => iv,
                long lv      => (int)lv,
                uint uiv     => (int)uiv,
                double dv    => double.IsFinite(dv) ? (int)dv : (int?)null,
                _            => null,
            };
            return i is int result && result >= 0 ? result : null;
        }
        catch { return null; }
    }

    private static bool? ReadBool(object obj, PropertyInfo? prop)
    {
        if (prop is null) return null;
        try
        {
            var val = prop.GetValue(obj);
            return val switch
            {
                bool b  => b,
                int i   => i != 0,
                _       => null,
            };
        }
        catch { return null; }
    }
}
