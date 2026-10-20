
using Xunit;

namespace Subframes.NinaPlugin.Tests;

public class TargetReplayPlannerTests
{
    [Fact]
    public void PerExposureRowsCollapseToOneLeader()
    {
        var rows = Enumerable.Range(0, 5).Select(_ => ((string?)"M31", 10.68, 41.27)).ToList();
        Assert.All(TargetReplayPlanner.GroupLeaders(rows), l => Assert.Equal(0, l));
    }

    [Fact]
    public void RealTargetChangeStartsNewBlock()
    {
        var rows = new List<(string?, double, double)>
            { ("M31", 10.68, 41.27), ("M31", 10.68, 41.27), ("M42", 83.8, -5.4), ("M42", 83.8, -5.4), ("M31", 10.68, 41.27) };
        Assert.Equal(new[] { 0, 0, 2, 2, 4 }, TargetReplayPlanner.GroupLeaders(rows));
    }
}
