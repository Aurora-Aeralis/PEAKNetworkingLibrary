using NetworkingLibrary.Modules;
using Steamworks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class RpcInfoSteamFallbackTests
{
    [Fact]
    public void Constructor_FromCSteamId_DoesNotThrow_WhenSteamApiUnavailable()
    {
        var sid = new CSteamID(12345678901234567UL);
        var ex = Record.Exception(() => new RPCInfo(sid));

        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_FromCSteamId_PreservesIdentifiers_WhenSteamApiUnavailable()
    {
        var sid = new CSteamID(12345678901234567UL);
        var info = new RPCInfo(sid);

        Assert.Equal(sid.m_SteamID, info.SteamId64);
        Assert.Equal(sid.ToString(), info.SteamIdString);
        Assert.False(info.IsLocalLoopback);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_UsesNumericIdentifier_WhenStringIdentifierIsMissing(string? steamIdString)
    {
        var info = new RPCInfo(42UL, steamIdString!);

        Assert.Equal(42UL, info.SteamId64);
        Assert.Equal("42", info.SteamIdString);
    }
}
