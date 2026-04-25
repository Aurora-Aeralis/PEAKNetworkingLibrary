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
}
