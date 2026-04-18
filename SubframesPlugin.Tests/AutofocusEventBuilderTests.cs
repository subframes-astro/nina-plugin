using System.Collections.Generic;
using Subframes.NinaPlugin;
using Subframes.NinaPlugin.Api;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Verifies that <see cref="AutofocusEventBuilder.Build"/> produces an
/// <see cref="EventRequest"/> with the exact payload the Subframes backend
/// expects for an autofocus completion event.
///
/// This covers the payload path that <c>SessionService.UpdateEndAutoFocusRun</c>
/// uses when NINA fires the IFocuserConsumer callback after an autofocus run.
/// </summary>
public sealed class AutofocusEventBuilderTests
{
    // ── EventType ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_EventType_IsAutofocus()
    {
        var req = AutofocusEventBuilder.Build("session-1", "Ha", 10.0, 45000);
        Assert.Equal("autofocus", req.EventType);
    }

    // ── SessionId ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_SessionId_IsPreserved()
    {
        var req = AutofocusEventBuilder.Build("abc-session", "L", null, 12345);
        Assert.Equal("abc-session", req.SessionId);
    }

    // ── Timestamp ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Timestamp_IsValidIso8601()
    {
        var req = AutofocusEventBuilder.Build("s", null, null, 0);
        Assert.True(
            DateTime.TryParse(req.Timestamp, null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _),
            $"Timestamp '{req.Timestamp}' is not a valid ISO-8601 string.");
    }

    // ── Metadata: filter ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ha")]
    [InlineData("OIII")]
    [InlineData("L")]
    [InlineData(null)]
    public void Build_Metadata_FilterMatchesInput(string? filter)
    {
        var req = AutofocusEventBuilder.Build("s", filter, null, 0);
        var meta = AssertMetadata(req);
        Assert.Equal(filter, meta["filter"]);
    }

    // ── Metadata: temperature ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(-12.5)]
    [InlineData(25.3)]
    [InlineData(null)]
    public void Build_Metadata_TemperatureMatchesInput(double? temperature)
    {
        var req = AutofocusEventBuilder.Build("s", null, temperature, 0);
        var meta = AssertMetadata(req);
        Assert.Equal(temperature, meta["temperature"]);
    }

    // ── Metadata: position ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(45000)]
    [InlineData(100000)]
    public void Build_Metadata_PositionMatchesInput(int position)
    {
        var req = AutofocusEventBuilder.Build("s", null, null, position);
        var meta = AssertMetadata(req);
        Assert.Equal(position, meta["position"]);
    }

    // ── Metadata keys are present even when values are null ──────────────────

    [Fact]
    public void Build_Metadata_ContainsAllExpectedKeys()
    {
        var req = AutofocusEventBuilder.Build("s", null, null, 0);
        var meta = AssertMetadata(req);
        Assert.True(meta.ContainsKey("filter"),      "Metadata must contain 'filter'");
        Assert.True(meta.ContainsKey("temperature"), "Metadata must contain 'temperature'");
        Assert.True(meta.ContainsKey("position"),    "Metadata must contain 'position'");
    }

    // ── Typical full payload ──────────────────────────────────────────────────

    [Fact]
    public void Build_TypicalPayload_AllFieldsCorrect()
    {
        const string sessionId   = "ses-42";
        const string filter      = "OIII";
        const double temperature = -7.4;
        const int    position    = 37800;

        var req = AutofocusEventBuilder.Build(sessionId, filter, temperature, position);
        var meta = AssertMetadata(req);

        Assert.Equal("autofocus", req.EventType);
        Assert.Equal(sessionId,   req.SessionId);
        Assert.Equal(filter,      meta["filter"]);
        Assert.Equal(temperature, meta["temperature"]);
        Assert.Equal(position,    meta["position"]);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> AssertMetadata(EventRequest req)
    {
        var meta = Assert.IsType<Dictionary<string, object?>>(req.Metadata);
        Assert.NotNull(meta);
        return meta;
    }
}
