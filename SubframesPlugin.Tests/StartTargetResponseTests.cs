using System.Text.Json;
using Subframes.NinaPlugin.Api;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>Deserialization of the target/start response (SUB-2184 root cause).</summary>
public sealed class StartTargetResponseTests
{
    [Fact]
    public void RealShapedResponse_ResolvesIdFromDataId()
    {
        const string json = "{\"data\":{\"id\":\"3f0c1b9e-1111-2222-3333-444455556666\",\"sessionId\":\"s1\",\"targetName\":\"WR134\",\"sortOrder\":0}}";
        var env = JsonSerializer.Deserialize<ApiEnvelope<StartSessionTargetData>>(json);
        Assert.Equal("3f0c1b9e-1111-2222-3333-444455556666", env?.Data?.ResolvedId);
    }

    [Fact]
    public void LegacyShape_FallsBackToSessionTargetId()
    {
        const string json = "{\"data\":{\"sessionTargetId\":\"abc\"}}";
        var env = JsonSerializer.Deserialize<ApiEnvelope<StartSessionTargetData>>(json);
        Assert.Equal("abc", env?.Data?.ResolvedId);
    }
}
