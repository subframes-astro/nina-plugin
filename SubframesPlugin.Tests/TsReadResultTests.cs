using System;
using Subframes.NinaPlugin;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Tests for <see cref="TsReadResult{T}.ToWireError"/> length capping. TS reader errors are sent
/// on every station heartbeat while a read keeps failing, so the wire string must stay bounded
/// even if the underlying exception message is arbitrarily long.
/// </summary>
public class TsReadResultTests
{
    [Fact]
    public void ToWireError_ShortMessage_ReturnedVerbatim()
    {
        var result = TsReadResult<int>.Error(new InvalidOperationException("database is locked"));

        Assert.Equal("InvalidOperationException: database is locked", result.ToWireError());
    }

    [Fact]
    public void ToWireError_OverLongMessage_IsTruncatedWithEllipsis()
    {
        var longMessage = new string('x', 1000);
        var result = TsReadResult<int>.Error(new InvalidOperationException(longMessage));

        var wireError = result.ToWireError();

        Assert.NotNull(wireError);
        Assert.Equal(TsReadResult<int>.MaxWireErrorLength, wireError!.Length);
        Assert.EndsWith("...", wireError);
        Assert.StartsWith("InvalidOperationException: ", wireError);
    }

    [Fact]
    public void ToWireError_NonErrorStatus_ReturnsNull()
    {
        Assert.Null(TsReadResult<int>.Ok(0).ToWireError());
        Assert.Null(TsReadResult<int>.NotInstalled().ToWireError());
    }
}
