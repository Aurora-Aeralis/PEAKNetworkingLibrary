using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class OfflineNetworkingServiceTests
{
    const uint TestModId = 777;

    enum DataKind { Unknown = 0, Scout = 1, Runner = 2 }

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

    sealed class RpcInfoReceiver
    {
        public RPCInfo LastInfo { get; private set; }
        public int CallCount { get; private set; }

        [CustomRPC]
        void OnInfo(RPCInfo info)
        {
            LastInfo = info;
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

    sealed class MixedRpcReceiver
    {
        public static int StaticCallCount { get; private set; }

        [CustomRPC]
        static void StaticPing(int value) => StaticCallCount += value;

        [CustomRPC]
        void InstancePing(int value) { }

        public static void Reset() => StaticCallCount = 0;
    }

    sealed class VisibilityRpcReceiver
    {
        public int PublicCallCount { get; private set; }
        public int PrivateCallCount { get; private set; }

        [CustomRPC]
        public void PublicPing(int value) => PublicCallCount += value;

        [CustomRPC]
        void PrivatePing(int value) => PrivateCallCount += value;

        public void NotRpc(int value) => PublicCallCount += value * 1000;
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

    sealed class ThrowingTransferTarget : INetworkingService
    {
        public int RegisterNetworkObjectCalls { get; private set; }
        public int DisposedRegistrationCount { get; private set; }
        public bool ThrowOnRegisterLobbyDataKey { get; set; }

        public bool IsInitialized => true;
        public bool InLobby => false;
        public ulong HostSteamId64 => 0;
        public string HostIdString => string.Empty;
        public bool IsHost => false;
        public Func<Message, ulong, bool>? IncomingValidator { get; set; }

        public event Action? LobbyCreated;
        public event Action? LobbyEntered;
        public event Action? LobbyLeft;
        public event Action<ulong>? PlayerEntered;
        public event Action<ulong>? PlayerLeft;
        public event Action<string[]>? LobbyDataChanged;
        public event Action<ulong, string[]>? PlayerDataChanged;

        public void Initialize() { }
        public void Shutdown() { }
        public void CreateLobby(int maxPlayers = 8) { }
        public void JoinLobby(ulong lobbySteamId64) { }
        public void LeaveLobby() { }
        public void InviteToLobby(ulong steamId64) { }
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            RegisterNetworkObjectCalls++;
            return new ScopeAction(() => DisposedRegistrationCount++);
        }
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0) => new ScopeAction(() => DisposedRegistrationCount++);
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0) { }
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0) { }
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters) { }
        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) { }
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters) { }
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) { }
        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters) { }
        public void RegisterLobbyDataKey(string key)
        {
            if (ThrowOnRegisterLobbyDataKey) throw new InvalidOperationException("target lobby key failed");
        }
        public void SetLobbyData(string key, object value) { }
        public T GetLobbyData<T>(string key) => default!;
        public void RegisterPlayerDataKey(string key) { }
        public void SetPlayerData(string key, object value) { }
        public T GetPlayerData<T>(ulong steamId64, string key) => default!;
        public void PollReceive() { }
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate) { }
        public void RegisterModPublicKey(uint modId, RSAParameters pub) { }
        public ulong GetLocalSteam64() => 0;
        public ulong[] GetLobbyMemberSteamIds() => Array.Empty<ulong>();
    }

    static IDisposable WithUnavailableNetLogger()
    {
        var loggerField = typeof(Net).GetField("<Logger>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        if (loggerField == null) return new ScopeAction(() => { });
        var original = loggerField.GetValue(null);
        loggerField.SetValue(null, null);
        return new ScopeAction(() => loggerField.SetValue(null, original));
    }

    static IDisposable WithOfflineUtcNow(Func<DateTime> utcNow)
    {
        var original = OfflineNetworkingService.UtcNow;
        OfflineNetworkingService.UtcNow = utcNow;
        return new ScopeAction(() => OfflineNetworkingService.UtcNow = original);
    }

    [Fact]
    public void LeaveLobby_ResetsLobbyState_AndHostIdentity()
    {
        var service = new OfflineNetworkingService();

        service.Initialize();
        service.JoinLobby(424242UL);
        Assert.True(service.InLobby);
        Assert.True(service.IsHost);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);

        service.LeaveLobby();

        Assert.False(service.InLobby);
        Assert.False(service.IsHost);
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
    public void JoinLobby_LocalHostId_SetsHostRole_AndRpcToHostDispatchesToLocalRegisteredHandler()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.JoinLobby(service.LocalSteamId);
        service.RPCToHost(TestModId, "OnPing", ReliableType.Reliable, 99);

        Assert.True(service.InLobby);
        Assert.True(service.IsHost);
        Assert.Equal(service.LocalSteamId, service.HostSteamId64);
        Assert.Equal(99, receiver.LastValue);
    }

    [Fact]
    public void JoinLobby_NonLocalLobbyId_PreservesLocalHostRole_AndRpcToHostDispatchesToLocalRegisteredHandler()
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
    public void Rpc_LocalDispatch_SetsRpcInfoLoopbackTrue()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcInfoReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.CreateLobby();
        service.RPC(TestModId, "OnInfo", ReliableType.Reliable);

        Assert.Equal(1, receiver.CallCount);
        Assert.True(receiver.LastInfo.IsLocalLoopback);
        Assert.Equal(service.LocalSteamId, receiver.LastInfo.SteamId64);
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

    [Theory]
    [InlineData("LogError")]
    [InlineData("LogWarning")]
    public void LoggerFallback_IsUsed_WhenNetLoggerIsUnavailable(string methodName)
    {
        ResetOfflineLogThrottleState();
        using var _ = WithUnavailableNetLogger();
        var now = new DateTime(2026, 1, 1, 0, 1, 40, DateTimeKind.Utc);
        using var __ = WithOfflineUtcNow(() => now);

        InvokeOfflineStatic(methodName, "first");
        now = now.AddSeconds(1);
        InvokeOfflineStatic(methodName, "second");

        Assert.Equal(new DateTime(2026, 1, 1, 0, 1, 40, DateTimeKind.Utc), GetOfflineStaticField<DateTime>("lastLogFallbackUtc"));
        Assert.Equal(1, GetOfflineStaticField<int>("suppressedLogFallbackCount"));
    }

    [Fact]
    public void DeserializeFailureThrottle_AllowsEmission_WhenClockMovesBackward()
    {
        ResetOfflineLogThrottleState();
        using var _ = WithUnavailableNetLogger();
        var now = new DateTime(2026, 1, 1, 0, 1, 40, DateTimeKind.Utc);
        using var __ = WithOfflineUtcNow(() => now);

        InvokeOfflineStatic("LogDeserializeFailureThrottled", new InvalidOperationException("first"), "first");
        now = now.AddSeconds(1);
        InvokeOfflineStatic("LogDeserializeFailureThrottled", new InvalidOperationException("second"), "second");

        Assert.Equal(1, GetOfflineStaticField<int>("suppressedDeserializeFailureCount"));

        now = now.AddSeconds(-50);
        InvokeOfflineStatic("LogDeserializeFailureThrottled", new InvalidOperationException("after reset"), "after reset");

        Assert.Equal(0, GetOfflineStaticField<int>("suppressedDeserializeFailureCount"));
        Assert.Equal(now, GetOfflineStaticField<DateTime>("lastDeserializeFailureUtc"));
    }

    [Fact]
    public void ExceptionThrottle_AllowsEmission_WhenClockMovesBackward()
    {
        ResetOfflineLogThrottleState();
        using var _ = WithUnavailableNetLogger();
        var now = new DateTime(2026, 1, 1, 0, 1, 40, DateTimeKind.Utc);
        using var __ = WithOfflineUtcNow(() => now);

        InvokeOfflineStatic("LogExceptionThrottled", "clock-key", false, "first", new InvalidOperationException("first"));
        now = now.AddSeconds(1);
        InvokeOfflineStatic("LogExceptionThrottled", "clock-key", false, "second", new InvalidOperationException("second"));

        Assert.Equal(1, GetOfflineStaticField<Dictionary<string, int>>("suppressedExceptionLogByKey")["clock-key"]);

        now = now.AddSeconds(-50);
        InvokeOfflineStatic("LogExceptionThrottled", "clock-key", false, "after reset", new InvalidOperationException("after reset"));

        Assert.False(GetOfflineStaticField<Dictionary<string, int>>("suppressedExceptionLogByKey").ContainsKey("clock-key"));
        Assert.Equal(now, GetOfflineStaticField<Dictionary<string, DateTime>>("lastExceptionLogByKey")["clock-key"]);
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
    public void LeaveLobby_PreservesRegisteredLobbyAndPlayerKeys()
    {
        var service = new OfflineNetworkingService();
        service.Initialize();
        service.CreateLobby();
        service.RegisterLobbyDataKey("map");
        service.RegisterPlayerDataKey("rank");

        service.LeaveLobby();
        service.CreateLobby();

        Assert.True(IsLobbyDataKeyRegistered(service, "map"));
        Assert.True(IsPlayerDataKeyRegistered(service, "rank"));
    }

    [Fact]
    public void Shutdown_ClearsRegisteredLobbyAndPlayerKeys_EvenWhenAlreadyOutOfLobby()
    {
        var service = new OfflineNetworkingService();
        service.Initialize();
        service.CreateLobby();
        service.RegisterLobbyDataKey("map");
        service.RegisterPlayerDataKey("rank");
        service.LeaveLobby();

        service.Shutdown();
        service.Initialize();
        service.CreateLobby();

        Assert.False(IsLobbyDataKeyRegistered(service, "map"));
        Assert.False(IsPlayerDataKeyRegistered(service, "rank"));
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
    public void Shutdown_ReinitializeRequiresRpcReregistration()
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

        Assert.Equal(-1, receiver.LastValue);
        Assert.Equal(0, receiver.CallCount);
    }

    [Fact]
    public void Shutdown_ClearsIncomingValidator_AndReinitializeRequiresExplicitReconfiguration()
    {
        var service = new OfflineNetworkingService();
        service.Initialize();
        service.IncomingValidator = (_, _) => false;

        service.Shutdown();

        Assert.Null(service.IncomingValidator);

        service.Initialize();
        Assert.Null(service.IncomingValidator);

        service.IncomingValidator = (_, _) => true;
        Assert.NotNull(service.IncomingValidator);
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
    public void LobbyAndPlayerData_ReadsBackEnumsAndNullableValues()
    {
        var service = new OfflineNetworkingService();

        service.Initialize();
        service.CreateLobby();
        service.RegisterLobbyDataKey("kind");
        service.RegisterPlayerDataKey("score");

        service.SetLobbyData("kind", DataKind.Runner);
        service.SetPlayerData("score", 42);

        Assert.Equal(DataKind.Runner, service.GetLobbyData<DataKind>("kind"));
        Assert.Equal(DataKind.Runner, service.GetLobbyData<DataKind?>("kind"));
        Assert.Equal(42, service.GetPlayerData<int?>(service.LocalSteamId, "score"));
    }

    [Fact]
    public void LobbyDataChanged_OnlyEmits_WhenValueChanges()
    {
        var service = new OfflineNetworkingService();
        var events = new List<string[]>();

        service.Initialize();
        service.CreateLobby();
        service.RegisterLobbyDataKey("map");
        service.LobbyDataChanged += keys => events.Add(keys);

        service.SetLobbyData("map", "forest");
        service.SetLobbyData("map", "forest");
        service.SetLobbyData("map", "shore");

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "map" }, events[0]);
        Assert.Equal(new[] { "map" }, events[1]);
    }

    [Fact]
    public void PlayerDataChanged_OnlyEmits_WhenValueChanges()
    {
        var service = new OfflineNetworkingService();
        var events = new List<(ulong playerId, string[] keys)>();

        service.Initialize();
        service.CreateLobby();
        service.RegisterPlayerDataKey("rank");
        service.PlayerDataChanged += (playerId, keys) => events.Add((playerId, keys));

        service.SetPlayerData("rank", 12);
        service.SetPlayerData("rank", 12);
        service.SetPlayerData("rank", 13);

        Assert.Equal(2, events.Count);
        Assert.Equal(service.LocalSteamId, events[0].playerId);
        Assert.Equal(new[] { "rank" }, events[0].keys);
        Assert.Equal(service.LocalSteamId, events[1].playerId);
        Assert.Equal(new[] { "rank" }, events[1].keys);
    }

    [Fact]
    public void RegisterModPublicKey_EmptyParameters_ThrowsArgumentException()
    {
        var service = new OfflineNetworkingService();
        Assert.Throws<ArgumentException>(() => service.RegisterModPublicKey(TestModId, new RSAParameters()));
    }

    [Fact]
    public void RegisterModPublicKey_ClonesMutableParameters()
    {
        var service = new OfflineNetworkingService();
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportParameters(false);
        var modulus = pub.Modulus!;
        var exponent = pub.Exponent!;
        var expectedModulusFirstByte = modulus[0];
        var expectedExponentFirstByte = exponent[0];

        service.RegisterModPublicKey(TestModId, pub);
        modulus[0] = (byte)(modulus[0] ^ 0x7f);
        exponent[0] = (byte)(exponent[0] ^ 0x7f);

        var stored = GetModPublicKeys(service)[TestModId];
        Assert.NotSame(modulus, stored.Modulus);
        Assert.NotSame(exponent, stored.Exponent);
        Assert.Equal(expectedModulusFirstByte, stored.Modulus![0]);
        Assert.Equal(expectedExponentFirstByte, stored.Exponent![0]);
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
    public void RegisterNetworkType_WithInstanceRpc_ThrowsAndDoesNotCreateHandlers()
    {
        var service = new OfflineNetworkingService();
        MixedRpcReceiver.Reset();

        var ex = Assert.Throws<InvalidOperationException>(() => service.RegisterNetworkType(typeof(MixedRpcReceiver), TestModId));
        Assert.Contains("Cannot register instance RPC method", ex.Message);

        service.Initialize();
        service.CreateLobby();
        service.RPC(TestModId, "StaticPing", ReliableType.Reliable, 3);

        Assert.Equal(0, GetRegisteredHandlerCount(service, TestModId));
        Assert.Equal(0, MixedRpcReceiver.StaticCallCount);
    }

    [Fact]
    public void RegisterNetworkObject_RegistersOnlyAttributedInstanceMethods_AndDispatchParityHolds()
    {
        var service = new OfflineNetworkingService();
        var receiver = new VisibilityRpcReceiver();

        service.Initialize();
        service.CreateLobby();
        using var _ = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        Assert.Equal(2, GetRegisteredHandlerCount(service, TestModId));

        service.RPC(TestModId, nameof(VisibilityRpcReceiver.PublicPing), ReliableType.Reliable, 2);
        service.RPC(TestModId, "PrivatePing", ReliableType.Reliable, 3);
        service.RPC(TestModId, nameof(VisibilityRpcReceiver.NotRpc), ReliableType.Reliable, 4);

        Assert.Equal(2, receiver.PublicCallCount);
        Assert.Equal(3, receiver.PrivateCallCount);
    }

    [Fact]
    public void DeregisterNetworkType_DoesNotRemoveObjectHandlersForSameDeclaringType()
    {
        var service = new OfflineNetworkingService();
        var receiver = new VisibilityRpcReceiver();

        service.Initialize();
        service.CreateLobby();
        using var _ = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        service.DeregisterNetworkType(typeof(VisibilityRpcReceiver), TestModId, mask: 0);
        service.RPC(TestModId, nameof(VisibilityRpcReceiver.PublicPing), ReliableType.Reliable, 5);

        Assert.Equal(5, receiver.PublicCallCount);
        Assert.Equal(2, GetRegisteredHandlerCount(service, TestModId));
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
    public void CopyRuntimeStateTo_PreservesRegistrationDisposableAcrossServicePromotion()
    {
        var source = new OfflineNetworkingService();
        var target = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        source.Initialize();
        source.CreateLobby();
        target.Initialize();
        target.CreateLobby();

        var registration = source.RegisterNetworkObject(receiver, TestModId);

        ((INetworkingServiceStateTransfer)source).CopyRuntimeStateTo(target);
        source.Shutdown();

        target.RPC(TestModId, "OnPing", ReliableType.Reliable, 5);
        Assert.Equal(1, receiver.CallCount);
        Assert.Equal(5, receiver.LastValue);

        registration.Dispose();

        target.RPC(TestModId, "OnPing", ReliableType.Reliable, 9);
        Assert.Equal(1, receiver.CallCount);
        Assert.Equal(5, receiver.LastValue);
    }

    [Fact]
    public void CopyRuntimeStateTo_FailedTargetCopy_KeepsSourceRegistrationToken()
    {
        var source = new OfflineNetworkingService();
        var target = new ThrowingTransferTarget { ThrowOnRegisterLobbyDataKey = true };
        var receiver = new RpcReceiver();

        source.Initialize();
        source.CreateLobby();
        var registration = source.RegisterNetworkObject(receiver, TestModId);
        source.RegisterLobbyDataKey("round");

        var exception = Assert.Throws<InvalidOperationException>(() => ((INetworkingServiceStateTransfer)source).CopyRuntimeStateTo(target));

        Assert.Equal("target lobby key failed", exception.Message);
        Assert.Equal(1, target.RegisterNetworkObjectCalls);
        Assert.Equal(1, target.DisposedRegistrationCount);

        source.RPC(TestModId, "OnPing", ReliableType.Reliable, 5);
        Assert.Equal(1, receiver.CallCount);
        Assert.Equal(5, receiver.LastValue);

        registration.Dispose();
        source.RPC(TestModId, "OnPing", ReliableType.Reliable, 9);
        Assert.Equal(1, receiver.CallCount);
        Assert.Equal(5, receiver.LastValue);
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

    [Fact]
    public void Shutdown_ClearsRegisteredRpcHandlers_AndDataKeyRegistrations()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.RegisterLobbyDataKey("round");
        service.RegisterPlayerDataKey("role");

        service.Shutdown();
        Assert.Equal(0, GetRegisteredHandlerCount(service, TestModId));

        var lobbyKeys = (ICollection<string>)typeof(OfflineNetworkingService)
            .GetField("lobbyKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var playerKeys = (ICollection<string>)typeof(OfflineNetworkingService)
            .GetField("playerKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        Assert.Empty(lobbyKeys);
        Assert.Empty(playerKeys);
    }

    [Fact]
    public async Task Concurrent_RegisterDeregister_AndDispatch_DoesNotThrow()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();
        var exceptions = new ConcurrentQueue<Exception>();

        service.Initialize();
        service.CreateLobby();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;
        var registrationTask = Task.Run(() =>
        {
            var iteration = 0;
            while (!token.IsCancellationRequested)
            {
                IDisposable? registration = null;
                try
                {
                    registration = service.RegisterNetworkObject(receiver, TestModId);
                    if ((iteration & 1) == 0) service.DeregisterNetworkObject(receiver, TestModId);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
                finally
                {
                    registration?.Dispose();
                    iteration++;
                }
            }
        });

        var dispatchTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    service.RPC(TestModId, "OnPing", ReliableType.Reliable, 42);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        });

        await Task.WhenAll(registrationTask, dispatchTask);

        if (exceptions.TryPeek(out var ex))
            Assert.Fail($"Encountered exception during concurrent RPC churn: {ex}");
    }

    [Fact]
    public async Task Concurrent_ModSecurityRegister_Send_AndShutdownChurn_DoesNotThrow()
    {
        var service = new OfflineNetworkingService();
        var receiver = new RpcReceiver();
        var exceptions = new ConcurrentQueue<Exception>();
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportParameters(false);

        service.Initialize();
        service.CreateLobby();
        service.RegisterNetworkObject(receiver, TestModId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;

        var registerTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    service.RegisterModSigner(TestModId, bytes => bytes);
                    service.RegisterModPublicKey(TestModId, pub);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        });

        var sendTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    service.RPC(TestModId, "OnPing", ReliableType.Reliable, 7);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        });

        var shutdownTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    service.Shutdown();
                    service.Initialize();
                    service.CreateLobby();
                    service.RegisterNetworkObject(receiver, TestModId);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        });

        await Task.WhenAll(registerTask, sendTask, shutdownTask);

        if (exceptions.TryPeek(out var ex))
            Assert.Fail($"Encountered exception during concurrent crypto/register/shutdown churn: {ex}");
    }

    static int GetRegisteredHandlerCount(OfflineNetworkingService service, uint modId)
    {
        var rpcs = (IDictionary)typeof(OfflineNetworkingService)
            .GetField("rpcs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        if (!rpcs.Contains(modId)) return 0;
        var methods = (IDictionary)rpcs[modId]!;
        var count = 0;
        foreach (DictionaryEntry entry in methods) count += ((ICollection)entry.Value!).Count;
        return count;
    }

    static bool IsLobbyDataKeyRegistered(OfflineNetworkingService service, string key)
    {
        var lobbyKeys = (HashSet<string>)typeof(OfflineNetworkingService)
            .GetField("lobbyKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        return lobbyKeys.Contains(key);
    }

    static bool IsPlayerDataKeyRegistered(OfflineNetworkingService service, string key)
    {
        var playerKeys = (HashSet<string>)typeof(OfflineNetworkingService)
            .GetField("playerKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        return playerKeys.Contains(key);
    }

    static Dictionary<uint, RSAParameters> GetModPublicKeys(OfflineNetworkingService service)
    {
        return (Dictionary<uint, RSAParameters>)typeof(OfflineNetworkingService)
            .GetField("modPublicKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
    }

    static void ResetOfflineLogThrottleState()
    {
        SetOfflineStaticField("lastLogFallbackUtc", DateTime.MinValue);
        SetOfflineStaticField("suppressedLogFallbackCount", 0);
        SetOfflineStaticField("lastDeserializeFailureUtc", DateTime.MinValue);
        SetOfflineStaticField("suppressedDeserializeFailureCount", 0);
        GetOfflineStaticField<Dictionary<string, DateTime>>("lastExceptionLogByKey").Clear();
        GetOfflineStaticField<Dictionary<string, int>>("suppressedExceptionLogByKey").Clear();
        OfflineNetworkingService.UtcNow = () => DateTime.UtcNow;
    }

    static void InvokeOfflineStatic(string name, params object[] args)
    {
        typeof(OfflineNetworkingService).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    }

    static T GetOfflineStaticField<T>(string name)
    {
        return (T)typeof(OfflineNetworkingService).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    static void SetOfflineStaticField<T>(string name, T value)
    {
        typeof(OfflineNetworkingService).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
    }
}
