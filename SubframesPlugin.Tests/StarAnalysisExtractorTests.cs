using System.Collections;
using Subframes.NinaPlugin.Imaging;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Unit tests for <see cref="StarAnalysisExtractor"/> (SUB-2054/SUB-2057).
///
/// Uses synthetic types (no real Hocus Focus DLL dependency) to validate the three
/// extraction shapes:
///   1. Stock analysis object — no FWHM/Eccentricity properties → both null.
///   2. HF-shape with aggregate AverageFWHM/AverageEccentricity → values propagated.
///   3. HF-shape with only DetectedStars per-star PSF → per-star median is used.
/// </summary>
public class StarAnalysisExtractorTests
{
    // -------------------------------------------------------------------------
    // Synthetic types — mimic Hocus Focus shapes without referencing HF DLL
    // -------------------------------------------------------------------------

    /// <summary>Stock NINA analysis — no FWHM or Eccentricity properties.</summary>
    private sealed class StockAnalysis
    {
        public double Hfr { get; init; } = 1.5;
        public int DetectedStarCount { get; init; } = 42;
    }

    /// <summary>HF-shape: has aggregate FWHM/Eccentricity and PSFType at top level.</summary>
    private sealed class HfAggregateAnalysis
    {
        public double AverageFWHM { get; init; }
        public double AverageEccentricity { get; init; }
        public string? PSFType { get; init; }
    }

    /// <summary>HF-shape: no aggregates; has DetectedStars with per-star values.</summary>
    private sealed class HfDetectedStarsAnalysis
    {
        public IEnumerable DetectedStars { get; init; } = Array.Empty<object>();
    }

    private sealed class HfStar
    {
        public double FWHMArcsecs { get; init; }
        public double Eccentricity { get; init; }
    }

    // Fake HF type name used to trigger the HF extraction path
    private const string FakeHfTypeName = "NINA.Joko.Plugin.HocusFocus.FakeAnalysisType";

    // -------------------------------------------------------------------------
    // 1. Stock analysis — null FWHM/Eccentricity
    // -------------------------------------------------------------------------

    [Fact]
    public void StockAnalysis_FwhmAndEccentricityAreNull()
    {
        var extractor = new StarAnalysisExtractor();
        var result = extractor.Extract(new StockAnalysis(), sessionId: "sess-001");

        Assert.Null(result.Fwhm);
        Assert.Null(result.Eccentricity);
        Assert.Null(result.PsfType);
    }

    [Fact]
    public void StockAnalysis_NoException()
    {
        var extractor = new StarAnalysisExtractor();
        var ex = Record.Exception(() => extractor.Extract(new StockAnalysis(), "sess-001"));
        Assert.Null(ex);
    }

    [Fact]
    public void NullAnalysis_ReturnsEmpty()
    {
        var extractor = new StarAnalysisExtractor();
        var result = extractor.Extract(null, "sess-001");
        Assert.Same(StarAnalysisResult.Empty, result);
    }

    // -------------------------------------------------------------------------
    // 2. HF aggregate FWHM/Eccentricity → values propagated; PSFType propagated
    // -------------------------------------------------------------------------

    [Fact]
    public void HfAggregates_FwhmAndEccentricityPropagated()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new HfAggregateAnalysis
        {
            AverageFWHM = 2.34,
            AverageEccentricity = 0.12,
            PSFType = "Gaussian"
        };

        var result = extractor.ExtractForTest(analysis, FakeHfTypeName, "sess-hf-01");

        Assert.Equal(2.34, result.Fwhm);
        Assert.Equal(0.12, result.Eccentricity);
        Assert.Equal("Gaussian", result.PsfType);
    }

    [Fact]
    public void HfAggregates_MoffatPsfType_Propagated()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new HfAggregateAnalysis
        {
            AverageFWHM = 1.8,
            AverageEccentricity = 0.08,
            PSFType = "Moffat"
        };

        var result = extractor.ExtractForTest(analysis, FakeHfTypeName, "sess-hf-02");

        Assert.Equal("Moffat", result.PsfType);
    }

    [Fact]
    public void HfAggregates_NullPsfType_PsfTypeIsNull()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new HfAggregateAnalysis
        {
            AverageFWHM = 2.0,
            AverageEccentricity = 0.1,
            PSFType = null
        };

        var result = extractor.ExtractForTest(analysis, FakeHfTypeName, "sess-hf-03");

        Assert.Null(result.PsfType);
        Assert.Equal(2.0, result.Fwhm);
    }

    // -------------------------------------------------------------------------
    // 3. HF DetectedStars only — per-star median is used
    // -------------------------------------------------------------------------

    [Fact]
    public void HfDetectedStars_MedianFwhmUsed()
    {
        var extractor = new StarAnalysisExtractor();

        // 5 stars — median of [1.0, 1.5, 2.0, 2.5, 3.0] = 2.0
        var analysis = new HfDetectedStarsAnalysis
        {
            DetectedStars = new[]
            {
                new HfStar { FWHMArcsecs = 1.0, Eccentricity = 0.1 },
                new HfStar { FWHMArcsecs = 1.5, Eccentricity = 0.15 },
                new HfStar { FWHMArcsecs = 2.0, Eccentricity = 0.2 },
                new HfStar { FWHMArcsecs = 2.5, Eccentricity = 0.25 },
                new HfStar { FWHMArcsecs = 3.0, Eccentricity = 0.3 },
            }
        };

        var result = extractor.ExtractForTest(analysis, FakeHfTypeName, "sess-hf-stars-01");

        Assert.Equal(2.0, result.Fwhm);
        Assert.Equal(0.2, result.Eccentricity, precision: 6);
    }

    [Fact]
    public void HfDetectedStars_EvenCount_MedianIsAverage()
    {
        var extractor = new StarAnalysisExtractor();

        // 4 stars — median of [1.0, 2.0, 3.0, 4.0] = 2.5
        var analysis = new HfDetectedStarsAnalysis
        {
            DetectedStars = new[]
            {
                new HfStar { FWHMArcsecs = 1.0, Eccentricity = 0.1 },
                new HfStar { FWHMArcsecs = 2.0, Eccentricity = 0.2 },
                new HfStar { FWHMArcsecs = 3.0, Eccentricity = 0.3 },
                new HfStar { FWHMArcsecs = 4.0, Eccentricity = 0.4 },
            }
        };

        var result = extractor.ExtractForTest(analysis, FakeHfTypeName, "sess-hf-stars-02");

        Assert.Equal(2.5, result.Fwhm);
        Assert.Equal(0.25, result.Eccentricity, precision: 6);
    }

    [Fact]
    public void HfDetectedStars_FewerThanThreeStars_FwhmNull()
    {
        var extractor = new StarAnalysisExtractor();

        // Only 2 stars — not enough to compute a median (minimum is 3)
        var analysis = new HfDetectedStarsAnalysis
        {
            DetectedStars = new[]
            {
                new HfStar { FWHMArcsecs = 1.0, Eccentricity = 0.1 },
                new HfStar { FWHMArcsecs = 2.0, Eccentricity = 0.2 },
            }
        };

        var result = extractor.ExtractForTest(analysis, FakeHfTypeName, "sess-hf-stars-03");

        Assert.Null(result.Fwhm);
        Assert.Null(result.Eccentricity);
    }

    [Fact]
    public void HfDetectedStars_NanAndZeroValuesIgnored()
    {
        var extractor = new StarAnalysisExtractor();

        // 2 invalid + 3 valid stars — median computed from valid ones only
        var analysis = new HfDetectedStarsAnalysis
        {
            DetectedStars = new[]
            {
                new HfStar { FWHMArcsecs = 0.0, Eccentricity = 0.1 },         // FWHM=0 invalid
                new HfStar { FWHMArcsecs = double.NaN, Eccentricity = 0.2 },  // NaN invalid
                new HfStar { FWHMArcsecs = 1.0, Eccentricity = 0.1 },
                new HfStar { FWHMArcsecs = 2.0, Eccentricity = 0.2 },
                new HfStar { FWHMArcsecs = 3.0, Eccentricity = 0.3 },
            }
        };

        var result = extractor.ExtractForTest(analysis, FakeHfTypeName, "sess-hf-stars-04");

        Assert.Equal(2.0, result.Fwhm);  // median of valid [1.0, 2.0, 3.0]
        // All 5 eccentricities are valid; sorted: [0.1, 0.1, 0.2, 0.2, 0.3] → median = 0.2
        Assert.Equal(0.2, result.Eccentricity, precision: 6);
    }

    [Fact]
    public void HfDetectedStars_EmptyCollection_FwhmNull()
    {
        var extractor = new StarAnalysisExtractor();

        var result = extractor.ExtractForTest(
            new HfDetectedStarsAnalysis { DetectedStars = Array.Empty<HfStar>() },
            FakeHfTypeName, "sess-hf-stars-05");

        Assert.Null(result.Fwhm);
        Assert.Null(result.Eccentricity);
    }

    // -------------------------------------------------------------------------
    // Generic fallback — known candidate property names
    // -------------------------------------------------------------------------

    [Fact]
    public void GenericFallback_FwhmProperty_Extracted()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new { FWHM = 1.75 };

        var result = extractor.Extract(analysis, "sess-gen-01");

        Assert.Equal(1.75, result.Fwhm);
    }

    [Fact]
    public void GenericFallback_MedianFwhm_Extracted()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new { MedianFWHM = 2.1 };

        var result = extractor.Extract(analysis, "sess-gen-02");

        Assert.Equal(2.1, result.Fwhm);
    }

    [Fact]
    public void GenericFallback_AverageFwhm_Extracted()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new { AverageFWHM = 1.9 };

        var result = extractor.Extract(analysis, "sess-gen-03");

        Assert.Equal(1.9, result.Fwhm);
    }

    [Fact]
    public void GenericFallback_EccentricityProperty_Extracted()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new { Eccentricity = 0.22 };

        var result = extractor.Extract(analysis, "sess-gen-04");

        Assert.Equal(0.22, result.Eccentricity);
    }

    [Fact]
    public void GenericFallback_AverageEccentricity_Extracted()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new { AverageEccentricity = 0.15 };

        var result = extractor.Extract(analysis, "sess-gen-05");

        Assert.Equal(0.15, result.Eccentricity);
    }

    [Fact]
    public void GenericFallback_StarFwhm_Extracted()
    {
        var extractor = new StarAnalysisExtractor();
        var analysis = new { StarFWHM = 1.55 };

        var result = extractor.Extract(analysis, "sess-gen-06");

        Assert.Equal(1.55, result.Fwhm);
    }

    // -------------------------------------------------------------------------
    // ComputeMedian static helper
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { 3.0, 1.0, 2.0 }, 2.0)]         // odd count
    [InlineData(new[] { 4.0, 1.0, 3.0, 2.0 }, 2.5)]     // even count
    [InlineData(new[] { 5.0 }, 5.0)]                      // single element
    [InlineData(new[] { 2.0, 2.0, 2.0 }, 2.0)]           // all same
    public void ComputeMedian_ReturnsCorrectValue(double[] input, double expected)
    {
        var list = new List<double>(input);
        Assert.Equal(expected, StarAnalysisExtractor.ComputeMedian(list));
    }
}
