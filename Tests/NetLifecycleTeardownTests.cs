using HarmonyLib;
using NetworkingLibrary.Services;
using System;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace NetworkingLibrary.Tests;

public class NetLifecycleTeardownTests
{
    sealed class FakeNetworkingService : INetworkingService
    {
        public bool ShutdownCalled { get; private set; }
        public bool ThrowOnShutdown { get; set; }
        public bool ThrowOnInitialize { get; set; }
        public bool InitializeCalled { get; private set; }

        public bool IsInitialized => true;
        public bool InLobby => false;
        public ulong HostSteamId64 => 0;
        public string HostIdString => string.Empty;
        public bool IsHost => true;
        public Func<Modules.Message, ulong, bool>? IncomingValidator { get; set; }

        public event Action? LobbyCreated;
        public event Action? LobbyEntered;
        public event Action? LobbyLeft;
        public event Action<ulong>? PlayerEntered;
        public event Action<ulong>? PlayerLeft;
        public event Action<string[]>? LobbyDataChanged;
        public event Action<ulong, string[]>? PlayerDataChanged;

        public void Initialize()
        {
            InitializeCalled = true;
            if (ThrowOnInitialize) throw new InvalidOperationException("initialize failed");
        }
        public void Shutdown()
        {
            ShutdownCalled = true;
            if (ThrowOnShutdown) throw new InvalidOperationException("shutdown failed");
        }
        public void CreateLobby(int maxPlayers = 8) { }
        public void JoinLobby(ulong lobbySteamId64) { }
        public void LeaveLobby() { }
        public void InviteToLobby(ulong steamId64) { }
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0) => new NoopDisposable();
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0) => new NoopDisposable();
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0) { }
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0) { }
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters) { }
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters) { }
        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters) { }
        public void RegisterLobbyDataKey(string key) { }
        public void SetLobbyData(string key, object value) { }
        public T GetLobbyData<T>(string key) => default!;
        public void RegisterPlayerDataKey(string key) { }
        public void SetPlayerData(string key, object value) { }
        public T GetPlayerData<T>(ulong steamId64, string key) => default!;
        public void PollReceive() { }
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate) { }
        public void RegisterModPublicKey(uint modId, System.Security.Cryptography.RSAParameters pub) { }
        public ulong GetLocalSteam64() => 0;
        public ulong[] GetLobbyMemberSteamIds() => Array.Empty<ulong>();

        sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    static class TeardownPatchTarget
    {
        public static int Calls;

        public static void Ping()
        {
            Calls++;
        }
    }

    static class TeardownPatchPrefix
    {
        public static void Prefix()
        {
            TeardownPatchTarget.Calls += 100;
        }
    }

    [Fact]
    public void OnDestroy_ShutsDownService_ClearsStaticService_AndUnpatchesHarmony()
    {
        var harmony = GetHarmony();
        var method = AccessTools.Method(typeof(TeardownPatchTarget), nameof(TeardownPatchTarget.Ping));
        var prefix = AccessTools.Method(typeof(TeardownPatchPrefix), nameof(TeardownPatchPrefix.Prefix));
        harmony.Patch(method, prefix: new HarmonyMethod(prefix));

        var service = new FakeNetworkingService();
        SetService(service);

        var net = (Net)FormatterServices.GetUninitializedObject(typeof(Net));
        InvokeOnDestroy(net);

        Assert.True(service.ShutdownCalled);
        Assert.Null(GetService());

        TeardownPatchTarget.Calls = 0;
        TeardownPatchTarget.Ping();
        Assert.Equal(1, TeardownPatchTarget.Calls);
    }

    [Fact]
    public void OnDestroy_DoesNotThrow_WhenHarmonyReferenceIsNull()
    {
        var service = new FakeNetworkingService();
        SetService(service);

        var harmonyField = typeof(Net).GetField("Harmony", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalHarmony = harmonyField.GetValue(null);

        try
        {
            harmonyField.SetValue(null, null);

            var net = (Net)FormatterServices.GetUninitializedObject(typeof(Net));
            var ex = Record.Exception(() => InvokeOnDestroy(net));

            Assert.Null(ex);
            Assert.True(service.ShutdownCalled);
            Assert.Null(GetService());
        }
        finally
        {
            harmonyField.SetValue(null, originalHarmony);
        }
    }

    [Fact]
    public void OnDestroy_DoesNotThrow_WhenServiceShutdownThrows_AndStillClearsStaticService()
    {
        var service = new FakeNetworkingService { ThrowOnShutdown = true };
        SetService(service);

        var net = (Net)FormatterServices.GetUninitializedObject(typeof(Net));
        var ex = Record.Exception(() => InvokeOnDestroy(net));

        Assert.Null(ex);
        Assert.True(service.ShutdownCalled);
        Assert.Null(GetService());
    }

    [Fact]
    public void TryInitializeNetworkingService_UsesOfflineFallback_WhenDefaultServiceInitializeThrows()
    {
        var defaultService = new FakeNetworkingService { ThrowOnInitialize = true };
        var fallbackService = new FakeNetworkingService();

        Net.CreateDefaultNetworkingService = () => defaultService;
        Net.CreateOfflineNetworkingService = () => fallbackService;

        try
        {
            var initialized = Net.TryInitializeNetworkingService(null, out var service);

            Assert.True(initialized);
            Assert.Same(fallbackService, service);
            Assert.True(defaultService.InitializeCalled);
            Assert.True(fallbackService.InitializeCalled);
        }
        finally
        {
            Net.ResetNetworkingStartupHooks();
        }
    }

    [Fact]
    public void TryInitializeNetworkingService_ReturnsFalseAndNullService_WhenFallbackAlsoFails()
    {
        var defaultService = new FakeNetworkingService { ThrowOnInitialize = true };
        var fallbackService = new FakeNetworkingService { ThrowOnInitialize = true };

        Net.CreateDefaultNetworkingService = () => defaultService;
        Net.CreateOfflineNetworkingService = () => fallbackService;

        try
        {
            var initialized = Net.TryInitializeNetworkingService(null, out var service);

            Assert.False(initialized);
            Assert.Null(service);
            Assert.True(defaultService.InitializeCalled);
            Assert.True(fallbackService.InitializeCalled);
        }
        finally
        {
            Net.ResetNetworkingStartupHooks();
        }
    }

    static Harmony GetHarmony()
    {
        return (Harmony)typeof(Net).GetField("Harmony", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    static INetworkingService? GetService()
    {
        var property = typeof(Net).GetProperty("Service", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        return (INetworkingService?)property.GetValue(null);
    }

    static void SetService(INetworkingService? service)
    {
        var property = typeof(Net).GetProperty("Service", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        property.SetValue(null, service);
    }

    static void InvokeOnDestroy(Net net)
    {
        typeof(Net).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(net, null);
    }
}
