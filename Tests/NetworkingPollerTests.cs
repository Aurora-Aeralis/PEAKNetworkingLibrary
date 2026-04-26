using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace NetworkingLibrary.Tests;

public class NetworkingPollerTests : IDisposable
{
    sealed class ScopeAction : IDisposable
    {
        Action? onDispose;
        public ScopeAction(Action onDispose) => this.onDispose = onDispose;
        public void Dispose()
        {
            var action = onDispose;
            if (action == null) return;
            onDispose = null;
            action();
        }
    }

    sealed class TestLogListener : ILogListener
    {
        readonly List<string> infos = new();
        readonly List<string> errors = new();
        public IReadOnlyList<string> Infos => infos;
        public IReadOnlyList<string> Errors => errors;
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            var message = eventArgs.Data?.ToString() ?? string.Empty;
            if (eventArgs.Level == LogLevel.Info) infos.Add(message);
            if (eventArgs.Level == LogLevel.Error) errors.Add(message);
        }
        public void Dispose() { }
    }

    sealed class PollTrackingService : INetworkingService
    {
        public int PollReceiveCalls { get; private set; }

        public bool IsInitialized => true;
        public bool InLobby => false;
        public ulong HostSteamId64 => 0;
        public string HostIdString => string.Empty;
        public bool IsHost => true;
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
        public void PollReceive() => PollReceiveCalls++;
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate) { }
        public void RegisterModPublicKey(uint modId, System.Security.Cryptography.RSAParameters pub) { }
        public ulong GetLocalSteam64() => 0;
        public ulong[] GetLobbyMemberSteamIds() => Array.Empty<ulong>();

        sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    public NetworkingPollerTests()
    {
        UnityMainThreadDispatcher.TestHooks.ResetForTests();
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(Environment.CurrentManagedThreadId);
    }

    public void Dispose()
    {
        UnityMainThreadDispatcher.TestHooks.ResetForTests();
        SetService(null);
    }

    [Fact]
    public void Update_StillPollsService_WhenDispatcherThrows()
    {
        var originalFactory = UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory;
        var createRequestQueuedField = typeof(UnityMainThreadDispatcher)
            .GetField("createRequestQueued", BindingFlags.Static | BindingFlags.NonPublic)!;
        var poller = (NetworkingPoller)FormatterServices.GetUninitializedObject(typeof(NetworkingPoller));
        var service = new PollTrackingService();
        SetService(service);

        try
        {
            createRequestQueuedField.SetValue(null, 1);
            UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () => throw new InvalidOperationException("dispatcher create failed");

            var ex = Record.Exception(() => InvokeUpdate(poller));

            Assert.Null(ex);
            Assert.Equal(1, service.PollReceiveCalls);
        }
        finally
        {
            UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = originalFactory;
            createRequestQueuedField.SetValue(null, 0);
        }
    }

    [Fact]
    public void PollGuarded_RecoveryMessage_IncludesSuppressedCount_AfterThrottledBurst()
    {
        var listener = new TestLogListener();
        using var _ = WithNetLogger(listener);

        var lastErrorLogTime = float.NegativeInfinity;
        var hadFault = false;
        var suppressedFault = false;
        var suppressedExceptionCount = 0;
        var pollGuarded = typeof(NetworkingPoller).GetMethod("PollGuarded", BindingFlags.Static | BindingFlags.NonPublic)!;
        var args = new object[] {
            new Action(() => throw new InvalidOperationException("burst-1")),
            "PollReceive",
            lastErrorLogTime,
            hadFault,
            suppressedFault,
            suppressedExceptionCount
        };

        pollGuarded.Invoke(null, args);
        args[0] = new Action(() => throw new InvalidOperationException("burst-2"));
        pollGuarded.Invoke(null, args);
        args[0] = new Action(() => { });
        pollGuarded.Invoke(null, args);

        Assert.Single(listener.Errors);
        Assert.Contains("PollReceive recovered after repeated failures.", listener.Infos[0]);
        Assert.Contains("Suppressed 1 errors since last emitted error.", listener.Infos[0]);
    }

    static IDisposable WithNetLogger(TestLogListener listener)
    {
        var loggerField = typeof(Net).GetField("<Logger>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        if (loggerField == null) return new ScopeAction(() => { });
        var original = loggerField.GetValue(null);
        var logger = new ManualLogSource("NetworkingPollerTests");
        Logger.Listeners.Add(listener);
        loggerField.SetValue(null, logger);
        return new ScopeAction(() =>
        {
            loggerField.SetValue(null, original);
            Logger.Listeners.Remove(listener);
            logger.Dispose();
        });
    }

    static void SetService(INetworkingService? service)
    {
        var property = typeof(Net).GetProperty("Service", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        property.SetValue(null, service);
    }

    static void InvokeUpdate(NetworkingPoller poller)
    {
        typeof(NetworkingPoller).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(poller, null);
    }
}
