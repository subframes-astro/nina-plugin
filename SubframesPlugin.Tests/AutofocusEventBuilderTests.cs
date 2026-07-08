using System;
using System.Collections.Generic;
using System.Reflection;
using Subframes.NinaPlugin;
using Subframes.NinaPlugin.Api;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Verifies that <see cref="AutofocusEventBuilder.Build"/> produces an
/// <see cref="EventRequest"/> with the exact payload the Subframes backend
/// expects for an autofocus completion event.
///
/// Test groups:
/// 1. Minimal overload (standard NINA stock AF, no HF object) — unchanged contract.
/// 2. Enriched overload without Hocus Focus (fallback path).
/// 3. Enriched overload with a mock HF object (all metrics present).
/// 4. Enriched overload with partial / null HF properties (graceful degradation).
/// 5. HocusFocus assembly detection helper.
/// </summary>
public sealed class AutofocusEventBuilderTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // 1. Minimal overload (4-param Build)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_Minimal_EventType_IsAutofocus()
    {
        var req = AutofocusEventBuilder.Build("session-1", "Ha", 10.0, 45000);
        Assert.Equal("autofocus", req.EventType);
    }

    [Fact]
    public void Build_Minimal_SessionId_IsPreserved()
    {
        var req = AutofocusEventBuilder.Build("abc-session", "L", null, 12345);
        Assert.Equal("abc-session", req.SessionId);
    }

    [Fact]
    public void Build_Minimal_Timestamp_IsValidIso8601()
    {
        var req = AutofocusEventBuilder.Build("s", null, null, 0);
        Assert.True(
            DateTime.TryParse(req.Timestamp, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out _),
            $"Timestamp '{req.Timestamp}' is not a valid ISO-8601 string.");
    }

    [Theory]
    [InlineData("Ha")]
    [InlineData("OIII")]
    [InlineData("L")]
    [InlineData(null)]
    public void Build_Minimal_FilterMatchesInput(string? filter)
    {
        var req  = AutofocusEventBuilder.Build("s", filter, null, 0);
        var meta = AssertMeta(req);
        Assert.Equal(filter, meta["filter"]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-12.5)]
    [InlineData(25.3)]
    [InlineData(null)]
    public void Build_Minimal_TemperatureMatchesInput(double? temperature)
    {
        var req  = AutofocusEventBuilder.Build("s", null, temperature, 0);
        var meta = AssertMeta(req);
        Assert.Equal(temperature, meta["temperature"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45000)]
    [InlineData(100000)]
    public void Build_Minimal_PositionMatchesInput(int position)
    {
        var req  = AutofocusEventBuilder.Build("s", null, null, position);
        var meta = AssertMeta(req);
        Assert.Equal(position, meta["position"]);
    }

    [Fact]
    public void Build_Minimal_ContainsAllExpectedKeys()
    {
        var req  = AutofocusEventBuilder.Build("s", null, null, 0);
        var meta = AssertMeta(req);
        Assert.True(meta.ContainsKey("filter"),      "Metadata must contain 'filter'");
        Assert.True(meta.ContainsKey("temperature"), "Metadata must contain 'temperature'");
        Assert.True(meta.ContainsKey("position"),    "Metadata must contain 'position'");
    }

    [Fact]
    public void Build_Minimal_TypicalPayload_AllFieldsCorrect()
    {
        const string sessionId   = "ses-42";
        const string filter      = "OIII";
        const double temperature = -7.4;
        const int    position    = 37800;

        var req  = AutofocusEventBuilder.Build(sessionId, filter, temperature, position);
        var meta = AssertMeta(req);

        Assert.Equal("autofocus", req.EventType);
        Assert.Equal(sessionId,   req.SessionId);
        Assert.Equal(filter,      meta["filter"]);
        Assert.Equal(temperature, meta["temperature"]);
        Assert.Equal(position,    meta["position"]);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Enriched overload — non-HF object (standard NINA struct fallback)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_Enriched_StockObject_HasMinimalKeys()
    {
        // Reset detection cache so no injected mock assembly contaminates this test.
        ResetHFDetectionCache();

        // Pass a plain object that has no HF properties — HF assembly not present in test process.
        var plainObj = new PlainAutofocusInfo();
        var req      = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, plainObj);
        var meta     = AssertMeta(req);

        Assert.Equal("L",    meta["filter"]);
        Assert.Equal(-5.0,   meta["temperature"]);
        Assert.Equal(12000,  meta["position"]);
        // Without HF assembly, no source / enriched fields should appear.
        Assert.False(meta.ContainsKey("source"),       "source should not be present when HF not detected");
        Assert.False(meta.ContainsKey("achievedHfr"),  "achievedHfr should not be present when HF not detected");
    }

    [Fact]
    public void Build_Enriched_StockObject_EventType_IsAutofocus()
    {
        ResetHFDetectionCache();
        var req = AutofocusEventBuilder.Build("s", null, null, 0, new PlainAutofocusInfo());
        Assert.Equal("autofocus", req.EventType);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Enriched overload — mock HF object with all metrics present
    //    (simulates HF assembly present via reflection on the mock type)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_Enriched_HFObject_AchievedHfr_ExtractedCorrectly()
    {
        // Reset reflection cache so the mock type is resolved.
        ResetHFReflectionCache();

        var hfObj = new MockHFAutofocusInfo { AchievedHFR = 1.82, Succeeded = true, NumberOfStars = 127 };
        // Inject the mock assembly into the builder's cache.
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal(1.82, meta["achievedHfr"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_InitialHfr_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { InitialHFR = 3.45 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal(3.45, meta["initialHfr"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_RSquared_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { RSquared = 0.9987 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal(0.9987, meta["rSquared"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_CurveFittingMethod_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { CurveFittingMethod = "Hyperbolic" };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal("Hyperbolic", meta["curveFitting"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_Duration_ExtractedAsSeconds()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { Duration = TimeSpan.FromSeconds(45.2) };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal(45.2, (double)meta["duration"]!, 5);
    }

    [Fact]
    public void Build_Enriched_HFObject_StepCount_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { NumberOfSteps = 9 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal(9, meta["stepCount"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_Success_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { Succeeded = true };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal(true, meta["success"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_StarCount_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { NumberOfStars = 127 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal(127, meta["starCount"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_Source_IsHocusFocus()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { AchievedHFR = 1.5 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal("hocus_focus", meta["source"]);
    }

    [Fact]
    public void Build_Enriched_HFObject_SourceVersion_IsPresent()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { AchievedHFR = 1.5 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        // sourceVersion should be present (the test assembly has a version).
        Assert.True(meta.ContainsKey("sourceVersion"), "sourceVersion must be in metadata when HF detected");
    }

    [Fact]
    public void Build_Enriched_HFObject_AllFullFields_CorrectValues()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo
        {
            AchievedHFR        = 1.82,
            InitialHFR         = 3.45,
            RSquared           = 0.9987,
            CurveFittingMethod = "Hyperbolic",
            Duration           = TimeSpan.FromSeconds(45.2),
            NumberOfSteps      = 9,
            Succeeded          = true,
            NumberOfStars      = 127,
        };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("ses-99", "Ha", -3.0, 12450, hfObj);
        var meta = AssertMeta(req);

        Assert.Equal("autofocus",      req.EventType);
        Assert.Equal("ses-99",         req.SessionId);
        Assert.Equal("Ha",             meta["filter"]);
        Assert.Equal(-3.0,             meta["temperature"]);
        Assert.Equal(12450,            meta["position"]);
        Assert.Equal("hocus_focus",    meta["source"]);
        Assert.Equal(1.82,             meta["achievedHfr"]);
        Assert.Equal(3.45,             meta["initialHfr"]);
        Assert.Equal(0.9987,           meta["rSquared"]);
        Assert.Equal("Hyperbolic",     meta["curveFitting"]);
        Assert.Equal(45.2,             (double)meta["duration"]!, 5);
        Assert.Equal(9,                meta["stepCount"]);
        Assert.Equal(true,             meta["success"]);
        Assert.Equal(127,              meta["starCount"]);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Enriched overload — partial / invalid HF values (graceful degradation)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_Enriched_NegativeHfr_IsOmittedFromMetadata()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { AchievedHFR = -1.0 }; // NINA sentinel
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", null, 0, hfObj);
        var meta = AssertMeta(req);

        Assert.False(meta.ContainsKey("achievedHfr"), "Negative HFR sentinel should be omitted");
    }

    [Fact]
    public void Build_Enriched_NaNHfr_IsOmittedFromMetadata()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { AchievedHFR = double.NaN };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", null, 0, hfObj);
        var meta = AssertMeta(req);

        Assert.False(meta.ContainsKey("achievedHfr"), "NaN HFR should be omitted");
    }

    [Fact]
    public void Build_Enriched_NegativeStepCount_IsOmittedFromMetadata()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { NumberOfSteps = -1 }; // NINA sentinel
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", null, 0, hfObj);
        var meta = AssertMeta(req);

        Assert.False(meta.ContainsKey("stepCount"), "Negative step count should be omitted");
    }

    [Fact]
    public void Build_Enriched_EmptyCurveFitting_IsOmittedFromMetadata()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { CurveFittingMethod = "" };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", null, 0, hfObj);
        var meta = AssertMeta(req);

        Assert.False(meta.ContainsKey("curveFitting"), "Empty curve fitting name should be omitted");
    }

    [Fact]
    public void Build_Enriched_CoreFieldsAlwaysPresent_EvenWithAllNullHFValues()
    {
        ResetHFReflectionCache();
        // All HF props are default (0 / null / false) — negatives and zeros omitted,
        // but the three base fields must always be present.
        var hfObj = new MockHFAutofocusInfo();
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        var req  = AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);
        var meta = AssertMeta(req);

        Assert.True(meta.ContainsKey("filter"),      "filter must always be present");
        Assert.True(meta.ContainsKey("temperature"), "temperature must always be present");
        Assert.True(meta.ContainsKey("position"),    "position must always be present");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. DetectHocusFocus — assembly detection helper
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DetectHocusFocus_ReturnsNull_WhenNoHFAssemblyLoaded()
    {
        // In the test host, no "HocusFocus" assembly is loaded.
        // Ensure the cache is clear so a real scan runs.
        ResetHFDetectionCache();

        var result = AutofocusEventBuilder.DetectHocusFocus();
        Assert.Null(result);
    }

    [Fact]
    public void DetectHocusFocus_IsIdempotent_MultipleCallsReturnSame()
    {
        ResetHFDetectionCache();
        var first  = AutofocusEventBuilder.DetectHocusFocus();
        var second = AutofocusEventBuilder.DetectHocusFocus();
        Assert.Equal(first, second);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. GetLastHFResolvedDiagnostics — diagnostic info for troubleshooting
    //    (key tool for diagnosing the HF 4.x root cause: AutoFocusInfo never
    //     carries HFR, so enrichment must come from DataPoints instead)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GetLastHFResolvedDiagnostics_ReturnsNullBeforeAnyResolution()
    {
        ResetHFReflectionCache();
        // Reset the diagnostic fields too.
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_lastResolvedTypeName", null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_lastResolvedPropertySummary", null);

        var (typeName, propertySummary) = AutofocusEventBuilder.GetLastHFResolvedDiagnostics();
        Assert.Null(typeName);
        Assert.Null(propertySummary);
    }

    [Fact]
    public void GetLastHFResolvedDiagnostics_CapturesTypeNameAfterEnrichment()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { AchievedHFR = 1.5 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);

        var (typeName, _) = AutofocusEventBuilder.GetLastHFResolvedDiagnostics();
        Assert.NotNull(typeName);
        Assert.Contains("MockHFAutofocusInfo", typeName,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GetLastHFResolvedDiagnostics_CapturesPropertyNamesAfterEnrichment()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo { AchievedHFR = 1.5 };
        InjectMockHFAssembly(typeof(MockHFAutofocusInfo).Assembly);

        AutofocusEventBuilder.Build("s", "L", -5.0, 12000, hfObj);

        var (_, propertySummary) = AutofocusEventBuilder.GetLastHFResolvedDiagnostics();
        Assert.NotNull(propertySummary);
        // MockHFAutofocusInfo exposes AchievedHFR among its properties.
        Assert.Contains("AchievedHFR", propertySummary, StringComparison.Ordinal);
    }

    [Fact]
    public void GetLastHFResolvedDiagnostics_StockAutoFocusInfo_ShowsNoEnrichedProperties()
    {
        // This test confirms the root cause of SUB-1897:
        // When the object is the stock NINA AutoFocusInfo (no HF subclass),
        // the property summary will NOT contain AchievedHFR, showing why
        // the reflection approach yields no enriched fields for HF 4.x.
        ResetHFReflectionCache();
        var plainObj = new PlainAutofocusInfo();
        InjectMockHFAssembly(typeof(PlainAutofocusInfo).Assembly);

        AutofocusEventBuilder.Build("s", "L", -5.0, 12000, plainObj);

        var (typeName, propertySummary) = AutofocusEventBuilder.GetLastHFResolvedDiagnostics();
        Assert.NotNull(typeName);
        Assert.NotNull(propertySummary);
        Assert.DoesNotContain("AchievedHFR", propertySummary,
            StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static Dictionary<string, object?> AssertMeta(EventRequest req)
    {
        var meta = Assert.IsType<Dictionary<string, object?>>(req.Metadata);
        Assert.NotNull(meta);
        return meta;
    }

    /// <summary>
    /// Resets the private HF property-resolution cache via reflection so each
    /// test group starts from a known state.
    /// </summary>
    private static void ResetHFReflectionCache()
    {
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_hfPropsResolved", false);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_achievedHfrProp",  null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_initialHfrProp",   null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_rSquaredProp",     null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_curveFittingProp", null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_durationProp",     null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_stepCountProp",    null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_successProp",      null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_starCountProp",    null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_lastResolvedTypeName", null);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_lastResolvedPropertySummary", null);
    }

    /// <summary>Resets the HF assembly detection cache.</summary>
    private static void ResetHFDetectionCache()
    {
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_hfDetected",  false);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_hfAssembly",  null);
    }

    /// <summary>
    /// Injects a mock assembly into the builder's HF-detection cache, simulating
    /// a Hocus Focus assembly being present in the AppDomain.
    /// </summary>
    private static void InjectMockHFAssembly(System.Reflection.Assembly assembly)
    {
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_hfDetected", true);
        SetPrivateStatic(typeof(AutofocusEventBuilder), "_hfAssembly", assembly);
    }

    private static void SetPrivateStatic(Type type, string fieldName, object? value)
    {
        var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {type.Name}.");
        field.SetValue(null, value);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. BuildFocusPoints — focus-point array builder
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildFocusPoints_EmptyInput_ReturnsEmptyList()
    {
        var result = AutofocusEventBuilder.BuildFocusPoints(
            Array.Empty<(double, double)>());
        Assert.Empty(result);
    }

    [Fact]
    public void BuildFocusPoints_TypicalPoints_CountMatchesInput()
    {
        var points = new[]
        {
            (15500.0, 8.2),
            (15700.0, 5.1),
            (15900.0, 3.4),
            (16037.0, 2.97),
            (16200.0, 3.6),
            (16400.0, 5.3),
            (16600.0, 8.5),
        };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Equal(7, result.Count);
    }

    [Fact]
    public void BuildFocusPoints_EachEntry_HasPositionAndHfrKeys()
    {
        var points = new[] { (16037.0, 2.97) };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Single(result);
        Assert.True(result[0].ContainsKey("position"), "Entry must contain 'position'");
        Assert.True(result[0].ContainsKey("hfr"),      "Entry must contain 'hfr'");
    }

    [Fact]
    public void BuildFocusPoints_Position_IsRoundedToInteger()
    {
        // 16037.6 should round to 16038.
        var points = new[] { (16037.6, 2.97) };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Equal(16038, result[0]["position"]);
    }

    [Fact]
    public void BuildFocusPoints_Hfr_IsPreserved()
    {
        var points = new[] { (16000.0, 2.97) };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Equal(2.97, result[0]["hfr"]);
    }

    [Fact]
    public void BuildFocusPoints_NegativeHfr_IsExcluded()
    {
        var points = new[]
        {
            (15500.0, -1.0),  // NINA sentinel — should be excluded
            (16000.0, 2.97),  // valid
        };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Single(result);
        Assert.Equal(16000, result[0]["position"]);
    }

    [Fact]
    public void BuildFocusPoints_ZeroHfr_IsExcluded()
    {
        var points = new[]
        {
            (15500.0, 0.0),   // zero HFR — invalid
            (16000.0, 3.14),  // valid
        };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Single(result);
    }

    [Fact]
    public void BuildFocusPoints_NaNHfr_IsExcluded()
    {
        var points = new[]
        {
            (15500.0, double.NaN),
            (16000.0, 3.14),
        };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Single(result);
    }

    [Fact]
    public void BuildFocusPoints_InfinitePosition_IsExcluded()
    {
        var points = new[]
        {
            (double.PositiveInfinity, 3.14),
            (16000.0, 2.97),
        };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Single(result);
    }

    [Fact]
    public void BuildFocusPoints_TypicalVCurveDataset_CorrectOrder()
    {
        var points = new[]
        {
            (15500.0, 8.2),
            (15700.0, 5.1),
            (16037.0, 2.97),
            (16600.0, 8.5),
        };
        var result = AutofocusEventBuilder.BuildFocusPoints(points);
        Assert.Equal(4,     result.Count);
        Assert.Equal(15500, result[0]["position"]);
        Assert.Equal(8.2,   result[0]["hfr"]);
        Assert.Equal(16600, result[3]["position"]);
        Assert.Equal(8.5,   result[3]["hfr"]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. TryAddHFCurveMetrics — HF curve-fitting field extractor
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TryAddHFCurveMetrics_RSquared_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj   = new MockHFAutofocusInfo { RSquared = 0.9987 };
        var metadata = new Dictionary<string, object?>();
        // Resolve properties from mock HF type.
        AutofocusEventBuilder.TryAddHFCurveMetrics(hfObj, metadata);
        Assert.Equal(0.9987, metadata["rSquared"]);
    }

    [Fact]
    public void TryAddHFCurveMetrics_CurveFitting_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj    = new MockHFAutofocusInfo { CurveFittingMethod = "Hyperbolic" };
        var metadata = new Dictionary<string, object?>();
        AutofocusEventBuilder.TryAddHFCurveMetrics(hfObj, metadata);
        Assert.Equal("Hyperbolic", metadata["curveFitting"]);
    }

    [Fact]
    public void TryAddHFCurveMetrics_StarCount_ExtractedCorrectly()
    {
        ResetHFReflectionCache();
        var hfObj    = new MockHFAutofocusInfo { NumberOfStars = 42 };
        var metadata = new Dictionary<string, object?>();
        AutofocusEventBuilder.TryAddHFCurveMetrics(hfObj, metadata);
        Assert.Equal(42, metadata["starCount"]);
    }

    [Fact]
    public void TryAddHFCurveMetrics_AllThree_ExtractedTogether()
    {
        ResetHFReflectionCache();
        var hfObj = new MockHFAutofocusInfo
        {
            RSquared           = 0.9987,
            CurveFittingMethod = "Hyperbolic",
            NumberOfStars      = 42,
        };
        var metadata = new Dictionary<string, object?>();
        AutofocusEventBuilder.TryAddHFCurveMetrics(hfObj, metadata);
        Assert.Equal(0.9987,        metadata["rSquared"]);
        Assert.Equal("Hyperbolic",  metadata["curveFitting"]);
        Assert.Equal(42,             metadata["starCount"]);
    }

    [Fact]
    public void TryAddHFCurveMetrics_PlainObject_NoFieldsAdded()
    {
        ResetHFReflectionCache();
        var plainObj = new PlainAutofocusInfo();
        var metadata = new Dictionary<string, object?>();
        AutofocusEventBuilder.TryAddHFCurveMetrics(plainObj, metadata);
        Assert.False(metadata.ContainsKey("rSquared"),     "rSquared should not be present for plain objects");
        Assert.False(metadata.ContainsKey("curveFitting"), "curveFitting should not be present for plain objects");
        Assert.False(metadata.ContainsKey("starCount"),    "starCount should not be present for plain objects");
    }

    [Fact]
    public void TryAddHFCurveMetrics_NegativeRSquared_IsOmitted()
    {
        ResetHFReflectionCache();
        var hfObj    = new MockHFAutofocusInfo { RSquared = -0.1 };
        var metadata = new Dictionary<string, object?>();
        AutofocusEventBuilder.TryAddHFCurveMetrics(hfObj, metadata);
        Assert.False(metadata.ContainsKey("rSquared"), "Negative rSquared should be omitted");
    }

    [Fact]
    public void TryAddHFCurveMetrics_EmptyCurveFitting_IsOmitted()
    {
        ResetHFReflectionCache();
        var hfObj    = new MockHFAutofocusInfo { CurveFittingMethod = "" };
        var metadata = new Dictionary<string, object?>();
        AutofocusEventBuilder.TryAddHFCurveMetrics(hfObj, metadata);
        Assert.False(metadata.ContainsKey("curveFitting"), "Empty curveFitting should be omitted");
    }

    [Fact]
    public void TryAddHFCurveMetrics_DoesNotOverwriteExistingMetadataKeys()
    {
        // Existing keys set by SessionService (e.g. achievedHfr from DataPoints)
        // must not be clobbered by the HF reflection path.
        ResetHFReflectionCache();
        var hfObj    = new MockHFAutofocusInfo { RSquared = 0.99, NumberOfStars = 10 };
        var metadata = new Dictionary<string, object?>
        {
            ["achievedHfr"] = 2.5,
            ["stepCount"]   = 7,
        };
        AutofocusEventBuilder.TryAddHFCurveMetrics(hfObj, metadata);
        // Original keys must be untouched.
        Assert.Equal(2.5, metadata["achievedHfr"]);
        Assert.Equal(7,   metadata["stepCount"]);
        // New HF-specific keys must be added.
        Assert.Equal(0.99, metadata["rSquared"]);
        Assert.Equal(10,   metadata["starCount"]);
    }

    // ── Mock types ───────────────────────────────────────────────────────────────────

    /// <summary>Plain object with no Hocus Focus properties (simulates stock NINA struct).</summary>
    private sealed class PlainAutofocusInfo { }

    /// <summary>
    /// Simulates the enriched concrete type that Hocus Focus provides when
    /// it replaces NINA's stock autofocus routine.
    /// Property names match the candidates the builder probes for.
    /// </summary>
    private sealed class MockHFAutofocusInfo
    {
        public double      AchievedHFR        { get; init; }
        public double      InitialHFR         { get; init; }
        public double      RSquared           { get; init; }
        public string?     CurveFittingMethod { get; init; }
        public TimeSpan    Duration           { get; init; }
        public int         NumberOfSteps      { get; init; }
        public bool        Succeeded          { get; init; }
        public int         NumberOfStars      { get; init; }
    }
}
