using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Xunit;

namespace NetworkingLibrary.Tests;

public class OfflineNetworkingServiceTests
{
    const uint TestModId = 777;

    sealed class RpcReceiver
    {
        public int LastValue { get; private set; } = -1;

        [CustomRPC]
        void OnPing(int value)
        {
            LastValue = value;
        }
    }

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
    public void Shutdown_ResetsLobbyScopedState()
    {
        var service = new OfflineNetworkingService();

        service.Initialize();
        service.CreateLobby();
        service.RegisterLobbyDataKey("map");
        service.SetLobbyData("map", "forest");
        service.RegisterPlayerDataKey("rank");
        service.SetPlayerData("rank", 12);

        service.Shutdown();

        Assert.False(service.IsInitialized);
        Assert.False(service.InLobby);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
        Assert.Empty(service.GetLobbyMemberSteamIds());
    }

    [Fact]
    public void Shutdown_PreservesRpcRegistrationsAcrossReinitializeAndLobbyCreate()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.CreateLobby();
        service.Shutdown();

        service.Initialize();
        service.CreateLobby();
        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 42);

        Assert.Equal(42, receiver.LastValue);
    }

    [Fact]
    public void CreateLobby_RaisesDeterministicEventSequence()
    {
        var service = new OfflineNetworkingService();
        var events = new List<string>();

        service.LobbyCreated += () => events.Add("LobbyCreated");
        service.LobbyEntered += () => events.Add("LobbyEntered");
        service.PlayerEntered += steamId => events.Add($"PlayerEntered:{steamId}");

        service.CreateLobby();

        Assert.Equal(
            new[]
            {
                "LobbyCreated",
                "LobbyEntered",
                $"PlayerEntered:{service.LocalSteamId}"
            },
            events);
    }

    [Fact]
    public void JoinLobby_RaisesDeterministicEventSequence()
    {
        var service = new OfflineNetworkingService();
        var events = new List<string>();

        service.LobbyCreated += () => events.Add("LobbyCreated");
        service.LobbyEntered += () => events.Add("LobbyEntered");
        service.PlayerEntered += steamId => events.Add($"PlayerEntered:{steamId}");

        service.JoinLobby(4242UL);

        Assert.Equal(
            new[]
            {
                "LobbyEntered",
                $"PlayerEntered:{service.LocalSteamId}"
            },
            events);
    }
}
