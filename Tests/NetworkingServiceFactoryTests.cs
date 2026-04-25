using NetworkingLibrary.Services;
using System;
using System.Reflection;
using Xunit;

namespace NetworkingLibrary.Tests;

#if !UNITY_EDITOR
public class NetworkingServiceFactoryTests
{
    sealed class PrivatePropertySteamManagerProbe
    {
        static bool Initialized { get; set; }
        public static void Set(bool value) => Initialized = value;
    }

    sealed class PrivateFieldSteamManagerProbe
    {
        static bool Initialized;
        public static void Set(bool value) => Initialized = value;
    }

    sealed class FakeService : INetworkingService
    {
        public bool IsInitialized => false;
        public bool InLobby => false;
        public ulong HostSteamId64 => 0;
        public string HostIdString => string.Empty;
        public bool IsHost => false;
        public Func<Modules.Message, ulong, bool>? IncomingValidator { get; set; }

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
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0) => new NoopDisposable();
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0) => new NoopDisposable();
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0) { }
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0) { }
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters) { }
        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) { }
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters) { }
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) { }
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
    public void CreateDefaultService_ReturnsSteamService_WhenClientRunningAndApiInitialized()
    {
        var steam = new FakeService();
        var offline = new FakeService();

        NetworkingServiceFactory.IsSteamClientRunning = () => true;
        NetworkingServiceFactory.IsSteamApiInitialized = () => true;
        NetworkingServiceFactory.CreateSteamService = () => steam;
        NetworkingServiceFactory.CreateOfflineService = () => offline;

        try
        {
            var service = NetworkingServiceFactory.CreateDefaultService();
            Assert.Same(steam, service);
        }
        finally
        {
            NetworkingServiceFactory.ResetTestHooks();
        }
    }

    [Fact]
    public void CreateDefaultService_ReturnsOfflineService_WhenApiIsNotInitialized()
    {
        var steam = new FakeService();
        var offline = new FakeService();

        NetworkingServiceFactory.IsSteamClientRunning = () => true;
        NetworkingServiceFactory.IsSteamApiInitialized = () => false;
        NetworkingServiceFactory.CreateSteamService = () => steam;
        NetworkingServiceFactory.CreateOfflineService = () => offline;

        try
        {
            var service = NetworkingServiceFactory.CreateDefaultService();
            Assert.Same(offline, service);
        }
        finally
        {
            NetworkingServiceFactory.ResetTestHooks();
        }
    }

    [Fact]
    public void CreateDefaultService_ReturnsOfflineService_WhenClientNotRunning()
    {
        var apiProbeCallCount = 0;
        var offline = new FakeService();

        NetworkingServiceFactory.IsSteamClientRunning = () => false;
        NetworkingServiceFactory.IsSteamApiInitialized = () =>
        {
            apiProbeCallCount++;
            return true;
        };
        NetworkingServiceFactory.CreateOfflineService = () => offline;

        try
        {
            var service = NetworkingServiceFactory.CreateDefaultService();
            Assert.Same(offline, service);
            Assert.Equal(0, apiProbeCallCount);
        }
        finally
        {
            NetworkingServiceFactory.ResetTestHooks();
        }
    }

    [Fact]
    public void CreateDefaultService_ReturnsOfflineService_WhenReadinessProbeThrows()
    {
        var offline = new FakeService();

        NetworkingServiceFactory.IsSteamClientRunning = () => throw new InvalidOperationException("probe failed");
        NetworkingServiceFactory.CreateOfflineService = () => offline;

        try
        {
            var service = NetworkingServiceFactory.CreateDefaultService();
            Assert.Same(offline, service);
        }
        finally
        {
            NetworkingServiceFactory.ResetTestHooks();
        }
    }

    [Fact]
    public void CreateDefaultService_DoesNotThrow_WhenNetLoggerIsUnavailable()
    {
        using var _ = WithUnavailableNetLogger();
        var offline = new FakeService();

        NetworkingServiceFactory.IsSteamClientRunning = () => false;
        NetworkingServiceFactory.CreateOfflineService = () => offline;

        try
        {
            var ex = Record.Exception(() => NetworkingServiceFactory.CreateDefaultService());
            Assert.Null(ex);
        }
        finally
        {
            NetworkingServiceFactory.ResetTestHooks();
        }
    }

    [Fact]
    public void TryReadInitializedFromType_ReadsNonPublicInitializedProperty()
    {
        PrivatePropertySteamManagerProbe.Set(true);
        var read = InvokeTryReadInitializedFromType(typeof(PrivatePropertySteamManagerProbe), out var isInitialized);
        Assert.True(read);
        Assert.True(isInitialized);
    }

    [Fact]
    public void TryReadInitializedFromType_ReadsNonPublicInitializedField()
    {
        PrivateFieldSteamManagerProbe.Set(true);
        var read = InvokeTryReadInitializedFromType(typeof(PrivateFieldSteamManagerProbe), out var isInitialized);
        Assert.True(read);
        Assert.True(isInitialized);
    }

    static bool InvokeTryReadInitializedFromType(Type steamManagerType, out bool isInitialized)
    {
        var method = typeof(NetworkingServiceFactory).GetMethod("TryReadInitializedFromType", BindingFlags.Static | BindingFlags.NonPublic)!;
        var args = new object?[] { steamManagerType, false };
        var read = (bool)method.Invoke(null, args)!;
        isInitialized = (bool)(args[1] ?? false);
        return read;
    }
}
#endif
