using Subframes.NinaPlugin;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Verifies that <see cref="FrameTypeFilter.IsLightFrame"/> correctly classifies
/// LIGHT frames as ingestible and all other types (DARK, BIAS, FLAT, SNAPSHOT,
/// null, empty) as non-ingestible.
/// </summary>
public sealed class FrameTypeFilterTests
{
    // ── LIGHT passes ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LIGHT")]
    [InlineData("light")]
    [InlineData("Light")]
    [InlineData("LiGhT")]
    public void IsLightFrame_LightVariantCasings_ReturnsTrue(string imageType)
    {
        Assert.True(FrameTypeFilter.IsLightFrame(imageType));
    }

    // ── Calibration frame types must be skipped ──────────────────────────

    [Theory]
    [InlineData("DARK")]
    [InlineData("dark")]
    [InlineData("BIAS")]
    [InlineData("Bias")]
    [InlineData("FLAT")]
    [InlineData("flat")]
    [InlineData("SNAPSHOT")]
    [InlineData("snapshot")]
    [InlineData("DARKFLAT")]
    [InlineData("UNKNOWN_TYPE")]
    public void IsLightFrame_NonLightTypes_ReturnsFalse(string imageType)
    {
        Assert.False(FrameTypeFilter.IsLightFrame(imageType));
    }

    // ── Null / empty / whitespace must be skipped ────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsLightFrame_NullOrEmpty_ReturnsFalse(string? imageType)
    {
        Assert.False(FrameTypeFilter.IsLightFrame(imageType));
    }
}
