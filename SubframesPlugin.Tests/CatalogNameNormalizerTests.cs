using Subframes.NinaPlugin;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Verifies that CatalogNameNormalizer converts the naming conventions
/// produced by NINA into the compact catalog IDs used by the Subframes
/// backend (e.g. dso_catalog.id = 'NGC7000', not 'NGC 7000').
///
/// Test report — naming convention compatibility with OpenNGC catalog:
///
///   Catalog type   Example input      Normalized output   Match?
///   ───────────────────────────────────────────────────────────
///   Messier        "M42"              "M42"               ✓ (exact)
///   Messier        "M 42"             "M42"               ✓ (space stripped)
///   Messier        "m42"              "M42"               ✓ (case fixed)
///   NGC            "NGC7000"          "NGC7000"           ✓ (exact)
///   NGC            "NGC 7000"         "NGC7000"           ✓ (space stripped)
///   NGC            "ngc 7000"         "NGC7000"           ✓ (case + space)
///   IC             "IC1396"           "IC1396"            ✓ (exact)
///   IC             "IC 1396"          "IC1396"            ✓ (space stripped)
///   Sharpless      "Sh2-240"          "Sh2-240"           ✓ (exact)
///   Sharpless      "Sh2 240"          "Sh2-240"           ✓ (space → hyphen)
///   Sharpless      "SH2-240"          "Sh2-240"           ✓ (case fixed)
///   LBN            "LBN123"           "LBN123"            ✓ (exact)
///   LBN            "LBN 123"          "LBN123"            ✓ (space stripped)
///   Common name    "Orion Nebula"     "Orion Nebula"      ~ (pass-through; backend full-text)
///   Cross-catalog  "NGC 1976"         "NGC1976"           ✓ (M42 alias in catalog)
///   Empty/null     ""                 ""                  ✓ (no-op)
///   Whitespace     "  NGC 7000  "     "NGC7000"           ✓ (trimmed + normalised)
/// </summary>
public sealed class CatalogNameNormalizerTests
{
    // ── Messier ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("M42",   "M42")]
    [InlineData("M 42",  "M42")]
    [InlineData("m42",   "M42")]
    [InlineData("m 42",  "M42")]
    [InlineData("M1",    "M1")]
    [InlineData("M 110", "M110")]
    public void Normalize_Messier(string input, string expected)
        => Assert.Equal(expected, CatalogNameNormalizer.Normalize(input));

    // ── NGC ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("NGC7000",    "NGC7000")]
    [InlineData("NGC 7000",   "NGC7000")]
    [InlineData("ngc 7000",   "NGC7000")]
    [InlineData("NGC253",     "NGC253")]
    [InlineData("NGC 253",    "NGC253")]
    [InlineData("NGC1976",    "NGC1976")]  // M42 NGC alias
    [InlineData("  NGC 7000 ","NGC7000")]  // leading/trailing whitespace
    public void Normalize_NGC(string input, string expected)
        => Assert.Equal(expected, CatalogNameNormalizer.Normalize(input));

    // ── IC ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("IC1396",  "IC1396")]
    [InlineData("IC 1396", "IC1396")]
    [InlineData("ic 1396", "IC1396")]
    [InlineData("IC434",   "IC434")]
    [InlineData("IC 434",  "IC434")]
    public void Normalize_IC(string input, string expected)
        => Assert.Equal(expected, CatalogNameNormalizer.Normalize(input));

    // ── Sharpless ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Sh2-240",  "Sh2-240")]   // canonical OpenNGC form
    [InlineData("SH2-240",  "Sh2-240")]   // uppercase fix
    [InlineData("sh2-240",  "Sh2-240")]   // lowercase fix
    [InlineData("Sh2 240",  "Sh2-240")]   // space → hyphen
    [InlineData("SH2 240",  "Sh2-240")]   // uppercase + space
    [InlineData("Sh2-132",  "Sh2-132")]
    public void Normalize_Sharpless(string input, string expected)
        => Assert.Equal(expected, CatalogNameNormalizer.Normalize(input));

    // ── LBN ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LBN123",  "LBN123")]
    [InlineData("LBN 123", "LBN123")]
    [InlineData("lbn 123", "LBN123")]
    public void Normalize_LBN(string input, string expected)
        => Assert.Equal(expected, CatalogNameNormalizer.Normalize(input));

    // ── Pass-through (common names, custom labels) ───────────────────────────

    [Theory]
    [InlineData("Orion Nebula",      "Orion Nebula")]
    [InlineData("Heart Nebula",      "Heart Nebula")]
    [InlineData("My Custom Target",  "My Custom Target")]
    [InlineData("Unknown Target",    "Unknown Target")]
    public void Normalize_CommonNames_PassThrough(string input, string expected)
        => Assert.Equal(expected, CatalogNameNormalizer.Normalize(input));

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_EmptyString_ReturnsEmpty()
        => Assert.Equal(string.Empty, CatalogNameNormalizer.Normalize(""));

    [Fact]
    public void Normalize_WhitespaceOnly_ReturnsEmpty()
        => Assert.Equal(string.Empty, CatalogNameNormalizer.Normalize("   "));

    [Fact]
    public void Normalize_Null_ReturnsEmpty()
        => Assert.Equal(string.Empty, CatalogNameNormalizer.Normalize(null!));
}
