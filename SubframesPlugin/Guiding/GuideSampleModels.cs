using System.Text.Json.Serialization;

namespace Subframes.NinaPlugin.Guiding;

/// <summary>
/// A single PHD2 guide sample ready for transmission to the Subframes API.
/// Values are in arcseconds using PHD2's native sign convention.
/// </summary>
public sealed record GuideSample
{
    /// <summary>UTC timestamp of this guide step as reported by PHD2.</summary>
    [JsonPropertyName("capturedAt")]
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Signed RA error in arcseconds (east positive).</summary>
    [JsonPropertyName("raErrorArcsec")]
    public required double RaErrorArcsec { get; init; }

    /// <summary>Signed Dec error in arcseconds (north positive).</summary>
    [JsonPropertyName("decErrorArcsec")]
    public required double DecErrorArcsec { get; init; }

    /// <summary>
    /// Combined total RMS in arcseconds: sqrt(ra² + dec²).
    /// If PHD2 provides a better simultaneous total, that value should be used instead.
    /// </summary>
    [JsonPropertyName("totalRmsArcsec")]
    public required double TotalRmsArcsec { get; init; }
}

/// <summary>
/// Batch payload sent to POST /api/v1/ingest/guide-samples.
/// </summary>
public sealed record GuideSampleBatchRequest
{
    /// <summary>The Subframes session UUID these samples belong to.</summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("samples")]
    public required IReadOnlyList<GuideSample> Samples { get; init; }
}

/// <summary>
/// Represents a NINA IGuideStep value mapped to Subframes types.
/// Internal intermediate type — not serialized.
/// </summary>
internal sealed record MappedGuideStep
{
    public DateTimeOffset CapturedAt { get; init; }
    public double RaErrorArcsec { get; init; }
    public double DecErrorArcsec { get; init; }
    public double TotalRmsArcsec { get; init; }

    /// <summary>
    /// Build from raw PHD2 values. All inputs are in arcseconds.
    /// </summary>
    public static MappedGuideStep FromArcseconds(
        DateTimeOffset capturedAt,
        double raArcsec,
        double decArcsec)
    {
        var total = Math.Sqrt(raArcsec * raArcsec + decArcsec * decArcsec);
        return new MappedGuideStep
        {
            CapturedAt = capturedAt,
            RaErrorArcsec = raArcsec,
            DecErrorArcsec = decArcsec,
            TotalRmsArcsec = total
        };
    }

    /// <summary>
    /// Build from raw PHD2 values where a total RMS is already available.
    /// </summary>
    public static MappedGuideStep FromArcsecondsWithTotal(
        DateTimeOffset capturedAt,
        double raArcsec,
        double decArcsec,
        double totalRmsArcsec)
    {
        return new MappedGuideStep
        {
            CapturedAt = capturedAt,
            RaErrorArcsec = raArcsec,
            DecErrorArcsec = decArcsec,
            TotalRmsArcsec = totalRmsArcsec
        };
    }

    /// <summary>Converts to the serializable <see cref="GuideSample"/> API type.</summary>
    public GuideSample ToGuideSample() => new()
    {
        CapturedAt = CapturedAt,
        RaErrorArcsec = RaErrorArcsec,
        DecErrorArcsec = DecErrorArcsec,
        TotalRmsArcsec = TotalRmsArcsec
    };
}
