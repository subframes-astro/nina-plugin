using System.Text.Json;
using Subframes.NinaPlugin.Guiding;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Unit tests for PHD2 guide sample mapping and request payload shape.
/// These tests do NOT require the NINA SDK.
/// </summary>
public class GuideSampleMappingTests
{
    // -------------------------------------------------------------------------
    // MappedGuideStep.FromArcseconds
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(1.0, 0.0, 1.0)]
    [InlineData(0.0, 1.0, 1.0)]
    [InlineData(-0.42, 0.31, 0.521)] // typical PHD2 values
    [InlineData(3.0, 4.0, 5.0)]     // Pythagorean triple
    public void FromArcseconds_ComputesTotalRms(double ra, double dec, double expectedTotal)
    {
        var step = MappedGuideStep.FromArcseconds(DateTimeOffset.UtcNow, ra, dec);

        Assert.Equal(ra, step.RaErrorArcsec);
        Assert.Equal(dec, step.DecErrorArcsec);
        Assert.Equal(expectedTotal, step.TotalRmsArcsec, precision: 2);
    }

    [Fact]
    public void FromArcseconds_PreservesSignedValues()
    {
        var step = MappedGuideStep.FromArcseconds(DateTimeOffset.UtcNow, raArcsec: -1.5, decArcsec: 0.8);

        Assert.Equal(-1.5, step.RaErrorArcsec);
        Assert.Equal(0.8, step.DecErrorArcsec);
    }

    [Fact]
    public void FromArcsecondsWithTotal_UsesProvidedTotal()
    {
        var step = MappedGuideStep.FromArcsecondsWithTotal(
            DateTimeOffset.UtcNow, raArcsec: 1.0, decArcsec: 0.0, totalRmsArcsec: 1.23);

        Assert.Equal(1.23, step.TotalRmsArcsec);
    }

    [Fact]
    public void FromArcseconds_CapturedAtIsPreserved()
    {
        var ts = new DateTimeOffset(2026, 7, 11, 4, 15, 3, 125, TimeSpan.Zero);
        var step = MappedGuideStep.FromArcseconds(ts, 0.1, 0.2);

        Assert.Equal(ts, step.CapturedAt);
    }

    // -------------------------------------------------------------------------
    // GuideSample JSON serialisation (request payload shape)
    // -------------------------------------------------------------------------

    [Fact]
    public void GuideSample_SerializesToExpectedShape()
    {
        var sample = new GuideSample
        {
            CapturedAt = new DateTimeOffset(2026, 7, 11, 4, 15, 3, 125, TimeSpan.Zero),
            RaErrorArcsec = -0.42,
            DecErrorArcsec = 0.31,
            TotalRmsArcsec = 0.52
        };

        var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify all required fields are present with correct camelCase names.
        Assert.True(root.TryGetProperty("capturedAt", out var capturedAt));
        Assert.True(root.TryGetProperty("raErrorArcsec", out var raError));
        Assert.True(root.TryGetProperty("decErrorArcsec", out var decError));
        Assert.True(root.TryGetProperty("totalRmsArcsec", out var totalRms));

        // capturedAt must be ISO-8601 / UTC
        Assert.Equal(
            "2026-07-11T04:15:03.125+00:00",
            capturedAt.GetString());

        Assert.Equal(-0.42, raError.GetDouble());
        Assert.Equal(0.31, decError.GetDouble());
        Assert.Equal(0.52, totalRms.GetDouble());
    }

    [Fact]
    public void GuideSampleBatchRequest_SerializesWrappedInSamplesArray()
    {
        var batch = new GuideSampleBatchRequest
        {
            SessionId = "00000000-0000-0000-0000-000000000001",
            Samples = new List<GuideSample>
            {
                new()
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    RaErrorArcsec = 0.1,
                    DecErrorArcsec = 0.2,
                    TotalRmsArcsec = 0.22
                }
            }
        };

        var json = JsonSerializer.Serialize(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("sessionId", out var sessionIdEl));
        Assert.Equal("00000000-0000-0000-0000-000000000001", sessionIdEl.GetString());
        Assert.True(root.TryGetProperty("samples", out var samples));
        Assert.Equal(JsonValueKind.Array, samples.ValueKind);
        Assert.Equal(1, samples.GetArrayLength());
    }

    [Fact]
    public void GuideSampleBatchRequest_AllowsMultipleSamples()
    {
        const int count = 60; // one minute of ~1Hz data
        var ts = DateTimeOffset.UtcNow;

        var sampleList = Enumerable.Range(0, count).Select(i => new GuideSample
        {
            CapturedAt = ts.AddSeconds(i),
            RaErrorArcsec = i * 0.01,
            DecErrorArcsec = -i * 0.01,
            TotalRmsArcsec = i * 0.014
        }).ToList();

        var batch = new GuideSampleBatchRequest
        {
            SessionId = "00000000-0000-0000-0000-000000000002",
            Samples = sampleList
        };

        var json = JsonSerializer.Serialize(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(count, doc.RootElement.GetProperty("samples").GetArrayLength());
    }

    // -------------------------------------------------------------------------
    // MappedGuideStep.ToGuideSample
    // -------------------------------------------------------------------------

    [Fact]
    public void ToGuideSample_CopiesAllFields()
    {
        var ts = DateTimeOffset.UtcNow;
        var mapped = MappedGuideStep.FromArcseconds(ts, raArcsec: 0.5, decArcsec: -0.3);
        var sample = mapped.ToGuideSample();

        Assert.Equal(ts, sample.CapturedAt);
        Assert.Equal(0.5, sample.RaErrorArcsec);
        Assert.Equal(-0.3, sample.DecErrorArcsec);
        Assert.Equal(mapped.TotalRmsArcsec, sample.TotalRmsArcsec);
    }
}
