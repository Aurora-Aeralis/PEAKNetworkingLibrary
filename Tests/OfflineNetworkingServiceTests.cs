using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using System;
using System.Reflection;
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
    
    sealed class StaticRpcReceiver
    {
        public static int LastValue { get; private set; } = -1;
        public static int CallCount { get; private set; }

        [CustomRPC]
        static void OnStaticPing(int value)
        {
            LastValue = value;
            CallCount++;
        }

        public static void Reset()
        {
            LastValue = -1;
            CallCount = 0;
        }
    }

    sealed class ScopeAction : IDisposable
    {
        Action? onDispose;
        public ScopeAction(Action onDispose) { this.onDispose = onDispose; }
        public void Dispose()
        {
            var action = onDispose;
            if (action == null) return;
            onDispose = null;
            action();
        }
    }

    static IDisposable WithUnavailableNetLogger()
    {
        var loggerField = typeof(Net).GetField("<Logger>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        if (loggerField == null) return new ScopeAction(() => { });
        var original = loggerField.GetValue(null);
        loggerField.SetValue(null, null);
        return new ScopeAction(() => loggerField.SetValue(null, original));
    }

    [Fact]
    public void LeaveLobby_ResetsLobbyState_AndHostIdentity()
    {
        var service = new OfflineNetworkingService();
        var lobbyId = 424242UL;

        service.Initialize();
        service.JoinLobby(lobbyId);
        Assert.True(service.InLobby);
        Assert.True(service.IsHost);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);

        service.LeaveLobby();

        Assert.False(service.InLobby);
        Assert.False(service.IsHost);
        Assert.NotEqual(lobbyId, service.HostSteamId64);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
    }

    [Fact]
    public void LeaveLobby_CalledTwice_RaisesLobbyLeftOnce()
    {
        var service = new OfflineNetworkingService();
        var lobbyLeftCount = 0;
        service.LobbyLeft += () => lobbyLeftCount++;

        service.Initialize();
        service.CreateLobby();
        service.LeaveLobby();
        service.LeaveLobby();

        Assert.Equal(1, lobbyLeftCount);
        Assert.False(service.InLobby);
    }

    [Fact]
    public void CreateLobby_WhileInLobby_LeavesExistingLobbyBeforeCreatingNewOne()
    {
        var service = new OfflineNetworkingService();
        var lobbyCreatedCount = 0;
        var lobbyLeftCount = 0;
        service.LobbyCreated += () => lobbyCreatedCount++;
        service.LobbyLeft += () => lobbyLeftCount++;

        service.Initialize();
        service.CreateLobby();
        service.CreateLobby();

        Assert.Equal(2, lobbyCreatedCount);
        Assert.Equal(1, lobbyLeftCount);
        Assert.True(service.InLobby);
    }

    [Fact]
    public void JoinLobby_WhileInLobby_LeavesExistingLobbyBeforeJoining()
    {
        var service = new OfflineNetworkingService();
        var lobbyEnteredCount = 0;
        var lobbyLeftCount = 0;
        service.LobbyEntered += () => lobbyEnteredCount++;
        service.LobbyLeft += () => lobbyLeftCount++;

        service.Initialize();
        service.CreateLobby();
        service.JoinLobby(123456UL);

        Assert.Equal(2, lobbyEnteredCount);
        Assert.Equal(1, lobbyLeftCount);
        Assert.True(service.InLobby);
    }

    [Fact]
    public void JoinLobby_RemoteLobbyId_UsesSinglePeerHostSimulation_AndRpcToHostDispatchesToLocalRegisteredHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();
        var lobbyId = 987654321UL;

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.JoinLobby(lobbyId);
        service.RPCToHost(TestModId, "OnPing", ReliableType.Reliable, 99);

        Assert.True(service.InLobby);
        Assert.True(service.IsHost);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
        Assert.Equal(99, receiver.LastValue);
    }

    [Fact]
    public void JoinLobby_LocalLobbyId_ThenRpcToHost_DispatchesToLocalRegisteredHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.JoinLobby(service.LocalSteamId);
        service.RPCToHost(TestModId, "OnPing", ReliableType.Reliable, 99);

        Assert.True(service.IsHost);
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
    public void RpcTarget_LocalPeer_InLobby_DispatchesToLocalHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.CreateLobby();

        service.RPCTarget(TestModId, "OnPing", service.LocalSteamId, ReliableType.Reliable, 27);

        Assert.Equal(1, receiver.CallCount);
        Assert.Equal(27, receiver.LastValue);
    }

    [Fact]
    public void RpcTarget_NonLocalInvitedPeer_StrictLoopback_IsNoOp()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();
        var invitedSteamId = 5252UL;

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.CreateLobby();
        service.InviteToLobby(invitedSteamId);

        service.RPCTarget(TestModId, "OnPing", invitedSteamId, ReliableType.Reliable, 27);

        Assert.Equal(new[] { service.LocalSteamId }, service.GetLobbyMemberSteamIds());
        Assert.Equal(0, receiver.CallCount);
        Assert.Equal(-1, receiver.LastValue);
    }

    [Fact]
    public void RpcPaths_WithoutLobby_DoNotThrow_WhenNetLoggerIsUnavailable()
    {
        using var _ = WithUnavailableNetLogger();
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);

        var ex = Record.Exception(() =>
        {
            service.RPC(TestModId, "OnPing", ReliableType.Reliable, 1);
            service.RPCTarget(TestModId, "OnPing", service.LocalSteamId, ReliableType.Reliable, 2);
            service.RPCToHost(TestModId, "OnPing", ReliableType.Reliable, 3);
        });

        Assert.Null(ex);
        Assert.Equal(0, receiver.CallCount);
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
    public void Shutdown_WhileInLobby_RaisesLobbyLeftOnce()
    {
        var service = new OfflineNetworkingService();
        var lobbyLeftCount = 0;
        service.LobbyLeft += () => lobbyLeftCount++;

        service.Initialize();
        service.CreateLobby();

        service.Shutdown();
        service.Shutdown();

        Assert.Equal(1, lobbyLeftCount);
        Assert.False(service.InLobby);
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
    public void RegisterModPublicKey_EmptyParameters_ThrowsArgumentException()
    {
        var service = new OfflineNetworkingService();
        Assert.Throws<ArgumentException>(() => service.RegisterModPublicKey(TestModId, new RSAParameters()));
    }

    [Fact]
    public void LobbyAndPlayerDataKeys_Reject_Whitespace()
    {
        var service = new OfflineNetworkingService();

        Assert.Throws<ArgumentException>(() => service.RegisterLobbyDataKey(" "));
        Assert.Throws<ArgumentException>(() => service.RegisterPlayerDataKey(""));
    }

    [Fact]
    public void CreateLobby_RaisesDeterministicEventSequence()
    {
        var service = new OfflineNetworkingService();
        var events = new List<string>();

        service.LobbyCreated += () => events.Add("LobbyCreated");
        service.LobbyEntered += () => events.Add("LobbyEntered");
        service.PlayerEntered += steamId => events.Add($"PlayerEntered:{steamId}");

        service.Initialize();
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

        service.Initialize();
        service.JoinLobby(4242UL);

        Assert.Equal(
            new[]
            {
                "LobbyEntered",
                $"PlayerEntered:{service.LocalSteamId}"
            },
            events);
        Assert.True(service.IsHost);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
    }

    [Fact]
    public void RegisterSameObjectTwice_IsIdempotent_AndSecondTokenDoesNotCaptureExistingHandlers()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.CreateLobby();

        var first = service.RegisterNetworkObject(receiver, TestModId, mask: 0);
        var second = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 1);
        Assert.Equal(1, receiver.CallCount);

        second.Dispose();
        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 2);
        Assert.Equal(2, receiver.CallCount);
        Assert.Equal(2, receiver.LastValue);

        first.Dispose();
        service.RPC(TestModId, "OnPing", ReliableType.Reliable, 3);
        Assert.Equal(2, receiver.CallCount);
    }

    [Fact]
    public void RegisterSameTypeTwice_IsIdempotent_AndSecondTokenDoesNotCaptureExistingHandlers()
    {
        var service = new OfflineNetworkingService();
        StaticRpcReceiver.Reset();

        service.Initialize();
        service.CreateLobby();

        var first = service.RegisterNetworkType(typeof(StaticRpcReceiver), TestModId, mask: 0);
        var second = service.RegisterNetworkType(typeof(StaticRpcReceiver), TestModId, mask: 0);

        service.RPC(TestModId, "OnStaticPing", ReliableType.Reliable, 1);
        Assert.Equal(1, StaticRpcReceiver.CallCount);

        second.Dispose();
        service.RPC(TestModId, "OnStaticPing", ReliableType.Reliable, 2);
        Assert.Equal(2, StaticRpcReceiver.CallCount);
        Assert.Equal(2, StaticRpcReceiver.LastValue);

        first.Dispose();
        service.RPC(TestModId, "OnStaticPing", ReliableType.Reliable, 3);
        Assert.Equal(2, StaticRpcReceiver.CallCount);
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
    public void Rpc_TypedOverload_IsAvailableThroughInterfaceReference()
    {
        var concrete = new OfflineNetworkingService();
        var receiver = new RpcReceiver();
        INetworkingService service = concrete;

        service.Initialize();
        service.CreateLobby();
        service.RegisterNetworkObject(receiver, TestModId);
        service.RPC(TestModId, "OnPing", ReliableType.Reliable, new[] { typeof(int) }, 31337);

        Assert.Equal(31337, receiver.LastValue);
        Assert.Equal(1, receiver.CallCount);
    }

    [Fact]
    public void RpcTarget_TypedOverload_IsAvailableThroughInterfaceReference()
    {
        var concrete = new OfflineNetworkingService();
        var receiver = new RpcReceiver();
        INetworkingService service = concrete;

        service.Initialize();
        service.CreateLobby();
        service.RegisterNetworkObject(receiver, TestModId);
        service.RPCTarget(TestModId, "OnPing", concrete.LocalSteamId, ReliableType.Reliable, new[] { typeof(int) }, 777);

        Assert.Equal(777, receiver.LastValue);
        Assert.Equal(1, receiver.CallCount);
    }

    [Fact]
    public void RegisterModSigner_RejectsNullDelegate()
    {
        var service = new OfflineNetworkingService();
        Assert.Throws<ArgumentNullException>(() => service.RegisterModSigner(TestModId, null!));
    }

    [Fact]
    public void LobbyLifecycleMethods_BeforeInitialize_AreNoOps()
    {
        var service = new OfflineNetworkingService();
        var lobbyCreatedCount = 0;
        var lobbyEnteredCount = 0;
        var playerEnteredCount = 0;

        service.LobbyCreated += () => lobbyCreatedCount++;
        service.LobbyEntered += () => lobbyEnteredCount++;
        service.PlayerEntered += _ => playerEnteredCount++;

        service.CreateLobby();
        service.JoinLobby(4242UL);
        service.InviteToLobby(5252UL);

        Assert.False(service.InLobby);
        Assert.False(service.IsInitialized);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
        Assert.Empty(service.GetLobbyMemberSteamIds());
        Assert.Equal(0, lobbyCreatedCount);
        Assert.Equal(0, lobbyEnteredCount);
        Assert.Equal(0, playerEnteredCount);
    }

    [Fact]
    public void JoinLobby_InvalidLobbyId_DoesNotEnterLobby()
    {
        var service = new OfflineNetworkingService();

        service.Initialize();
        service.JoinLobby(0UL);

        Assert.False(service.InLobby);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
        Assert.Empty(service.GetLobbyMemberSteamIds());
    }

    [Fact]
    public void InviteToLobby_InvalidSteamId_DoesNotAddMember()
    {
        var service = new OfflineNetworkingService();
        var playerEnteredCount = 0;
        service.PlayerEntered += _ => playerEnteredCount++;

        service.Initialize();
        service.CreateLobby();
        service.InviteToLobby(0UL);

        Assert.Equal(new[] { service.LocalSteamId }, service.GetLobbyMemberSteamIds());
        Assert.Equal(1, playerEnteredCount);
    }

    [Fact]
    public void InviteToLobby_NonLocalSteamId_IsIgnoredInStrictLoopbackMode()
    {
        var service = new OfflineNetworkingService();
        var invitedSteamId = 5252UL;
        var playerEnteredCount = 0;

        service.PlayerEntered += steamId =>
        {
            if (steamId == invitedSteamId) playerEnteredCount++;
        };

        service.Initialize();
        service.CreateLobby();
        service.InviteToLobby(invitedSteamId);
        service.InviteToLobby(invitedSteamId);

        Assert.Equal(new[] { service.LocalSteamId }, service.GetLobbyMemberSteamIds());
        Assert.Equal(0, playerEnteredCount);
    }

    [Fact]
    public void LeaveLobby_ThenCreateLobby_AfterInitialize_KeepsConsistentState()
    {
        var service = new OfflineNetworkingService();

        service.Initialize();
        service.CreateLobby();
        service.LeaveLobby();
        service.CreateLobby();

        Assert.True(service.IsInitialized);
        Assert.True(service.InLobby);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
        Assert.Equal(new[] { service.LocalSteamId }, service.GetLobbyMemberSteamIds());
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
