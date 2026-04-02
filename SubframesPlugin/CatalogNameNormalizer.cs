using System.Text.RegularExpressions;

namespace Subframes.NinaPlugin;

/// <summary>
/// Normalizes DSO target names from NINA into the compact ID format used by
/// the Subframes catalog (e.g. "NGC 7000" → "NGC7000", "M 42" → "M42").
///
/// <para>
/// NINA users enter target names in a variety of formats: the framing
/// assistant may produce "NGC 7000", the sequencer may inherit "M 42", and
/// manual entries can be anything.  The Subframes catalog stores compact IDs
/// with no space between the prefix and number (M42, NGC7000, IC1396,
/// Sh2-240, LBN123).  This class applies deterministic, lossless
/// transformations so the plugin always sends catalog-compatible identifiers.
/// </para>
///
/// <para>
/// Common-name inputs (e.g. "Orion Nebula") are returned unchanged — the
/// backend full-text search can match them via <c>common_name</c>.
/// </para>
/// </summary>
public static class CatalogNameNormalizer
{
    // Matches: optional leading whitespace, a known prefix (case-insensitive),
    // optional internal whitespace, a digit sequence, optional trailing qualifier.
    // Groups: 1=prefix, 2=number+suffix.
    //
    // Supported prefixes (covers OpenNGC, Messier, Sharpless, LBN):
    //   NGC, IC, M, Sh2, LBN, Ced, VdB, Cr, Tr, B
    //
    // Sharpless ("Sh2-240") keeps the hyphen — OpenNGC stores it as "Sh2-240".
    // All other prefixes use plain concatenation.
    private static readonly Regex _catalogPattern = new(
        @"^\s*(NGC|IC|M|Sh2|LBN|Ced|VdB|Cr|Tr|B)\s*(-?\d+[A-Za-z]?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Sharpless uses a hyphen separator in the canonical OpenNGC ID.
    private static readonly HashSet<string> _hyphenPrefixes =
        new(StringComparer.OrdinalIgnoreCase) { "Sh2" };

    /// <summary>
    /// Returns the catalog-canonical form of <paramref name="rawName"/>.
    /// If the name does not match a known catalog pattern it is returned
    /// trimmed but otherwise unchanged.
    /// </summary>
    /// <param name="rawName">Target name as entered by the user or produced by NINA.</param>
    /// <returns>Normalized catalog ID, or the original trimmed string.</returns>
    public static string Normalize(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return rawName?.Trim() ?? string.Empty;

        var m = _catalogPattern.Match(rawName);
        if (!m.Success)
            return rawName.Trim();

        var prefix = m.Groups[1].Value;
        var number = m.Groups[2].Value;

        // Canonicalize prefix casing (NGC, IC, M stay upper; Sh2 mixed-case).
        var canonicalPrefix = NormalizePrefix(prefix);

        if (_hyphenPrefixes.Contains(prefix))
        {
            // Sharpless: ensure exactly one hyphen separator.
            var digits = number.TrimStart('-');
            return $"{canonicalPrefix}-{digits}";
        }

        return $"{canonicalPrefix}{number}";
    }

    private static string NormalizePrefix(string prefix)
    {
        return prefix.ToUpperInvariant() switch
        {
            "SH2" => "Sh2",
            "VDB" => "VdB",
            var p => p   // NGC, IC, M, LBN, CED, CR, TR, B already look fine in upper-case
        };
    }
}
