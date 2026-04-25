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

    [Fact]
    public void Shutdown_ClearsPlayerData_AndReenterStartsClean()
    {
        var service = new OfflineNetworkingService();
        const string playerKey = "DisplayName";
        var localSteamId = service.LocalSteamId;

        service.CreateLobby();
        service.RegisterPlayerDataKey(playerKey);
        service.SetPlayerData(playerKey, "Aurora");
        Assert.Equal("Aurora", service.GetPlayerData<string>(localSteamId, playerKey));

        service.Shutdown();

        Assert.False(service.InLobby);
        Assert.Empty(service.GetLobbyMemberSteamIds());

        service.JoinLobby(12345UL);

        Assert.True(service.InLobby);
        Assert.Equal(new[] { localSteamId }, service.GetLobbyMemberSteamIds());
        Assert.Null(service.GetPlayerData<string>(localSteamId, playerKey));
    }
}
