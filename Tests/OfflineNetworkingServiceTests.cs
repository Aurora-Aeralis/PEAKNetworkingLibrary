using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using System;
using System.Security.Cryptography;
using Xunit;

namespace NetworkingLibrary.Tests;

public class OfflineNetworkingServiceTests
{
    const uint TestModId = 777;

    sealed class RpcReceiver
    {
        public int LastValue { get; private set; } = -1;
        public int CallCount { get; private set; }
        public int LastBytesLength { get; private set; } = -1;

        [CustomRPC]
        void OnPing(int value)
        {
            LastValue = value;
            CallCount++;
        }

        [CustomRPC]
        void OnBytes(byte[] payload)
        {
            LastBytesLength = payload.Length;
            CallCount++;
        }
    }

    [Fact]
    public void LeaveLobby_ResetsLobbyState_AndHostIdentity()
    {
        var service = new OfflineNetworkingService();
        var hostId = 424242UL;

        service.JoinLobby(hostId);
        Assert.True(service.InLobby);
        Assert.Equal(hostId, service.HostSteamId64);

        service.LeaveLobby();

        Assert.False(service.InLobby);
        Assert.NotEqual(hostId, service.HostSteamId64);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
    }

    [Fact]
    public void LeaveLobby_CalledTwice_RaisesLobbyLeftOnce()
    {
        var service = new OfflineNetworkingService();
        var lobbyLeftCount = 0;
        service.LobbyLeft += () => lobbyLeftCount++;

        service.CreateLobby();
        service.LeaveLobby();
        service.LeaveLobby();

        Assert.Equal(1, lobbyLeftCount);
        Assert.False(service.InLobby);
    }

    [Fact]
    public void JoinLobby_RemoteHost_ThenRpcToHost_DoesNotDispatchToLocalRegisteredHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();
        var remoteHostId = 987654321UL;

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.JoinLobby(remoteHostId);
        service.RPCToHost(TestModId, "OnPing", ReliableType.Reliable, 99);

        Assert.Equal(remoteHostId, service.HostSteamId64);
        Assert.Equal(-1, receiver.LastValue);
    }

    [Fact]
    public void JoinLobby_LocalHost_ThenRpcToHost_DispatchesToLocalRegisteredHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.JoinLobby(service.LocalSteamId);
        service.RPCToHost(TestModId, "OnPing", ReliableType.Reliable, 99);

        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
        Assert.Equal(99, receiver.LastValue);
    }

    [Fact]
    public void RpcToHost_WithoutLobby_DoesNotDispatchToLocalHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.RPCToHost(TestModId, "OnPing", ReliableType.Reliable, 55);

        Assert.False(service.InLobby);
        Assert.Equal(-1, receiver.LastValue);
    }

    [Fact]
    public void Rpc_WithoutLobby_DoesNotDispatchToLocalHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 12);

        Assert.False(service.InLobby);
        Assert.Equal(-1, receiver.LastValue);
    }

    [Fact]
    public void RpcTarget_WithoutLobby_DoesNotDispatchToLocalHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.RPCTarget(TestModId, "OnPing", service.LocalSteamId, ReliableType.Reliable, 12);

        Assert.False(service.InLobby);
        Assert.Equal(-1, receiver.LastValue);
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
    public void SetLobbyData_NullValue_DoesNotThrow_AndReadsBackAsEmptyString()
    {
        var service = new OfflineNetworkingService();

        service.Initialize();
        service.CreateLobby();
        service.RegisterLobbyDataKey("nullable");

        var exception = Record.Exception(() => service.SetLobbyData("nullable", null!));

        Assert.Null(exception);
        Assert.Equal(string.Empty, service.GetLobbyData<string>("nullable"));
    }

    [Fact]
    public void SetPlayerData_NullValue_DoesNotThrow_AndReadsBackAsEmptyString()
    {
        var service = new OfflineNetworkingService();

        service.Initialize();
        service.CreateLobby();
        service.RegisterPlayerDataKey("nullable");

        var exception = Record.Exception(() => service.SetPlayerData("nullable", null!));

        Assert.Null(exception);
        Assert.Equal(string.Empty, service.GetPlayerData<string>(service.LocalSteamId, "nullable"));
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

    [Fact]
    public void RegisterSameObjectTwice_DisposeTokens_RemovesOnlyCapturedHandlers()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.CreateLobby();

        var first = service.RegisterNetworkObject(receiver, TestModId, mask: 0);
        var second = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 1);
        Assert.Equal(2, receiver.CallCount);

        first.Dispose();
        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 2);
        Assert.Equal(3, receiver.CallCount);
        Assert.Equal(2, receiver.LastValue);

        second.Dispose();
        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 3);
        Assert.Equal(3, receiver.CallCount);
    }

    [Fact]
    public void Rpc_RegisteredHandler_OversizedPayload_IsRejected()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.CreateLobby();
        service.RegisterNetworkObject(receiver, TestModId);

        var oversizedPayload = new byte[Message.MaxLogicalSize];
        service.RPC(TestModId, "OnBytes", ReliableType.Reliable, oversizedPayload);

        Assert.Equal(0, receiver.CallCount);
        Assert.Equal(-1, receiver.LastBytesLength);
    }

    [Fact]
    public void Rpc_UnregisteredPath_OversizedPayload_IsRejected()
    {
        var service = new OfflineNetworkingService();
        var dispatchCount = 0;

        service.Initialize();
        service.CreateLobby();
        service.IncomingValidator = (_, _) =>
        {
            dispatchCount++;
            return true;
        };

        var oversizedPayload = new byte[Message.MaxLogicalSize];
        service.RPC(TestModId + 1, "UnregisteredBytes", ReliableType.Reliable, new[] { typeof(byte[]) }, oversizedPayload);

        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public void RegisterModSigner_RejectsNullDelegate()
    {
        var service = new OfflineNetworkingService();
        Assert.Throws<ArgumentNullException>(() => service.RegisterModSigner(TestModId, null!));
    }

    [Fact]
    public void RegisterModSecurityArtifacts_IsNoopCompatibleAcrossShutdown()
    {
        var service = new OfflineNetworkingService();
        using var rsa = RSA.Create(2048);
        service.RegisterModSigner(TestModId, bytes => bytes);
        service.RegisterModPublicKey(TestModId, rsa.ExportParameters(false));

        var ex = Record.Exception(service.Shutdown);
        Assert.Null(ex);
    }
}
