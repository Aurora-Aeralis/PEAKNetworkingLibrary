using NetworkingLibrary.Services;
using Xunit;

namespace NetworkingLibrary.Tests;

public class OfflineNetworkingServiceTests
{
    [Fact]
    public void LeaveLobby_ResetsLobbyState_AndHostIdentity()
    {
        var service = new OfflineNetworkingService();
        var previousHost = 424242UL;

        service.JoinLobby(previousHost);
        Assert.True(service.InLobby);
        Assert.Equal(previousHost, service.HostSteamId64);

        service.LeaveLobby();

        Assert.False(service.InLobby);
        Assert.NotEqual(previousHost, service.HostSteamId64);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
    }
}
