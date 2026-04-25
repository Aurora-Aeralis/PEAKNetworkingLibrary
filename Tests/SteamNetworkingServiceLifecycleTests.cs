using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Steamworks;
using System;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamNetworkingServiceLifecycleTests
{
    const uint TestModId = 777;

    sealed class RpcReceiver
    {
        public int LastValue { get; private set; } = -1;
        public int CallCount { get; private set; }

        [CustomRPC]
        void OnPing(int value)
        {
            LastValue = value;
            CallCount++;
        }
    }

    sealed class MixedRpcReceiver
    {
        [CustomRPC]
        static void OnStaticPing(int value) { }

        [CustomRPC]
        void OnInstancePing(int value) { }
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

    sealed class ConcurrentRpcReceiver
    {
        public int CallCount;

        [CustomRPC]
        void OnConcurrentPing(int value)
        {
            Interlocked.Increment(ref CallCount);
        }
    }

    [Fact]
    public void LeaveAndShutdown_ClearOutboundQueues()
    {
        var service = new SteamNetworkingService();

        SetInLobby(service, true);
        EnqueueTo(service, "normalQueue");
        EnqueueTo(service, "lowQueue");
        EnqueueTo(service, "highQueue");

        InvokeNonPublic(service, "OnLobbyLeftInternal");

        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
        AssertQueueCount(service, "highQueue", 0);

        SetInLobby(service, true);
        EnqueueTo(service, "normalQueue");
        EnqueueTo(service, "lowQueue");
        EnqueueTo(service, "highQueue");

        service.Shutdown();

        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
        AssertQueueCount(service, "highQueue", 0);
    }

    [Fact]
    public void Leave_PreservesOutgoingSequence_AndShutdown_ClearsAllTransientState()
    {
        var service = new SteamNetworkingService();

        SeedLastSeenSequence(service);
        SeedRateLimiters(service);
        SeedOutgoingSequencePerMod(service);

        InvokeNonPublic(service, "OnLobbyLeftInternal");

        AssertDictionaryCount(service, "lastSeenSequence", 0);
        AssertDictionaryCount(service, "rateLimiters", 0);
        AssertDictionaryCount(service, "outgoingSequencePerMod", 1);

        SeedLastSeenSequence(service);
        SeedRateLimiters(service);
        SeedOutgoingSequencePerMod(service, 778);

        service.Shutdown();

        AssertDictionaryCount(service, "lastSeenSequence", 0);
        AssertDictionaryCount(service, "rateLimiters", 0);
        AssertDictionaryCount(service, "outgoingSequencePerMod", 0);
    }

    [Fact]
    public void PollReceive_DoesNotFlushQueues_WhenNotInLobby()
    {
        var service = new SteamNetworkingService();

        SetInLobby(service, false);
        EnqueueTo(service, "normalQueue");
        EnqueueTo(service, "lowQueue");

        service.PollReceive();

        AssertQueueCount(service, "normalQueue", 1);
        AssertQueueCount(service, "lowQueue", 1);
    }

    [Fact]
    public void CreateJoinInvite_BeforeInitialize_AreNoOps_AndDoNotThrow()
    {
        var service = new SteamNetworkingService();
        var ex = Record.Exception(() =>
        {
            service.CreateLobby();
            service.JoinLobby(9001UL);
            service.InviteToLobby(42UL);
        });

        Assert.Null(ex);
        Assert.False(service.IsInitialized);
        Assert.False(service.InLobby);
    }

    [Fact]
    public void JoinLobby_WithInvalidLobbyId_DoesNotThrow_AndDoesNotEnterLobby()
    {
        var service = new SteamNetworkingService();
        SetInitialized(service, true);

        var ex = Record.Exception(() => service.JoinLobby(0UL));

        Assert.Null(ex);
        Assert.False(service.InLobby);
    }

    [Fact]
    public void LeaveLobby_CalledTwice_RaisesLobbyLeftOnce()
    {
        var service = new SteamNetworkingService();
        var lobbyLeftCount = 0;
        service.LobbyLeft += () => lobbyLeftCount++;

        InvokeLobbyEnter(service, 9001UL);
        service.LeaveLobby();
        service.LeaveLobby();

        Assert.Equal(1, lobbyLeftCount);
        Assert.False(service.InLobby);
    }

    [Fact]
    public async Task Shutdown_RacingWithRetransmitAndFlush_DoesNotThrow()
    {
        var service = new SteamNetworkingService();

        SetInLobby(service, true);
        SeedUnacked(service, count: 32);

        var flushTask = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                InvokeNonPublic(service, "FlushQueues", 0);
            }
        });

        var retransmitTask = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                InvokeNonPublic(service, "RetransmitUnacked");
            }
        });

        service.Shutdown();
        await Task.WhenAll(flushTask, retransmitTask);

        AssertUnackedCount(service, 0);
    }

    [Fact]
    public void RegisterSameObjectTwice_IsIdempotent_AndSecondTokenDoesNotCaptureExistingHandlers()
    {
        var service = new SteamNetworkingService();
        var receiver = new RpcReceiver();

        var first = service.RegisterNetworkObject(receiver, TestModId, mask: 0);
        var second = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        DispatchPing(service, 1);
        Assert.Equal(1, receiver.CallCount);

        second.Dispose();
        DispatchPing(service, 2);
        Assert.Equal(2, receiver.CallCount);

        first.Dispose();
        DispatchPing(service, 3);
        Assert.Equal(2, receiver.CallCount);
    }

    [Fact]
    public void RegisterSameTypeTwice_IsIdempotent_AndSecondTokenDoesNotCaptureExistingHandlers()
    {
        var service = new SteamNetworkingService();
        StaticRpcReceiver.Reset();

        var first = service.RegisterNetworkType(typeof(StaticRpcReceiver), TestModId, mask: 0);
        var second = service.RegisterNetworkType(typeof(StaticRpcReceiver), TestModId, mask: 0);

        DispatchMessage(service, "OnStaticPing", 1);
        Assert.Equal(1, StaticRpcReceiver.CallCount);

        second.Dispose();
        DispatchMessage(service, "OnStaticPing", 2);
        Assert.Equal(2, StaticRpcReceiver.CallCount);
        Assert.Equal(2, StaticRpcReceiver.LastValue);

        first.Dispose();
        DispatchMessage(service, "OnStaticPing", 3);
        Assert.Equal(2, StaticRpcReceiver.CallCount);
    }

    [Fact]
    public void RegisterNetworkType_WithInstanceRpc_ThrowsAndDoesNotCreateHandlers()
    {
        var service = new SteamNetworkingService();

        var ex = Assert.Throws<InvalidOperationException>(() => service.RegisterNetworkType(typeof(MixedRpcReceiver), TestModId));
        Assert.Contains("Cannot register instance RPC method", ex.Message);

        var rpcs = (IDictionary)GetField(service, "rpcs")!;
        Assert.Equal(0, rpcs.Count);
    }

    [Fact]
    public void RegisterModSigner_NullDelegate_ThrowsArgumentNullException()
    {
        var service = new SteamNetworkingService();
        Assert.Throws<ArgumentNullException>(() => service.RegisterModSigner(TestModId, null!));
    }

    [Fact]
    public void RegisterModPublicKey_EmptyParameters_ThrowsArgumentException()
    {
        var service = new SteamNetworkingService();
        Assert.Throws<ArgumentException>(() => service.RegisterModPublicKey(TestModId, new RSAParameters()));
    }

    [Fact]
    public void Shutdown_PreservesRpcRegistrationsAcrossReinitializeAndLobbyEnter()
    {
        var service = new SteamNetworkingService();
        var receiver = new RpcReceiver();

        service.Initialize();
        service.RegisterNetworkObject(receiver, TestModId);
        service.Shutdown();

        service.Initialize();
        InvokeLobbyEnter(service, 9001UL);
        DispatchPing(service, 42);

        Assert.True(service.InLobby);
        Assert.Equal(42, receiver.LastValue);
        Assert.Equal(1, receiver.CallCount);
    }

    [Fact]
    public void Initialize_WhenCallbackAndCryptoSetupFails_KeepsServiceUninitializedAndClearsPartialState()
    {
        var service = new SteamNetworkingService();
        var rsaFactoryField = typeof(SteamNetworkingService).GetField("localRsaFactory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        rsaFactoryField.SetValue(service, (Func<RSACryptoServiceProvider>)(() => throw new InvalidOperationException("simulated init failure")));

        service.Initialize();

        Assert.False(service.IsInitialized);
        Assert.Null(GetField(service, "cbLobbyEnter"));
        Assert.Null(GetField(service, "cbLobbyCreated"));
        Assert.Null(GetField(service, "cbLobbyChatUpdate"));
        Assert.Null(GetField(service, "cbLobbyDataUpdate"));
        Assert.Null(GetField(service, "LocalRsa"));
    }

    [Fact]
    public void GetLobbyMemberSteamIds_SkipsNilMembers_AndReturnsCompactArray()
    {
        var service = new SteamNetworkingService();

        SetInLobby(service, true);
        SetLobby(service, 9001UL);
        SetField(service, "getNumLobbyMembers", (Func<CSteamID, int>)(_ => 4));
        SetField(service, "getLobbyMemberByIndex", (Func<CSteamID, int, CSteamID>)((_, index) => index switch
        {
            0 => new CSteamID(111UL),
            1 => CSteamID.Nil,
            2 => new CSteamID(333UL),
            _ => CSteamID.Nil
        }));

        var ids = service.GetLobbyMemberSteamIds();

        Assert.Equal(new ulong[] { 111UL, 333UL }, ids);
        Assert.DoesNotContain(0UL, ids);
    }

    [Fact]
    public async Task DispatchIncoming_AndBuildMessage_HandleConcurrentRpcRegistrationChanges()
    {
        var service = new SteamNetworkingService();
        var receiver = new ConcurrentRpcReceiver();
        using var baseline = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        var dispatchMethod = typeof(SteamNetworkingService).GetMethod("DispatchIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var buildMessageMethod = typeof(SteamNetworkingService).GetMethod("BuildMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Exception? dispatchError = null;
        Exception? buildError = null;
        var gate = new ManualResetEventSlim(false);
        var stop = new CancellationTokenSource();

        var toggleTask = Task.Run(() =>
        {
            gate.Wait();
            while (!stop.IsCancellationRequested)
            {
                var token = service.RegisterNetworkObject(new ConcurrentRpcReceiver(), TestModId, mask: 0);
                token.Dispose();
            }
        });

        var dispatchTask = Task.Run(() =>
        {
            gate.Wait();
            try
            {
                for (var i = 0; i < 250; i++)
                {
                    var message = new Message(TestModId, "OnConcurrentPing", 0);
                    message.WriteObject(typeof(int), i);
                    dispatchMethod.Invoke(service, new object[] { message, new CSteamID(123UL) });
                }
            }
            catch (Exception ex)
            {
                dispatchError = ex;
            }
        });

        var buildTask = Task.Run(() =>
        {
            gate.Wait();
            try
            {
                for (var i = 0; i < 250; i++)
                {
                    _ = buildMessageMethod.Invoke(service, new object?[] { TestModId, "OnConcurrentPing", 0, new object?[] { i }, null });
                }
            }
            catch (Exception ex)
            {
                buildError = ex;
            }
        });

        gate.Set();
        await Task.WhenAll(dispatchTask, buildTask);
        stop.Cancel();
        await toggleTask;

        Assert.Null(dispatchError);
        Assert.Null(buildError);
        Assert.True(receiver.CallCount > 0);
    }

    static void SetInLobby(SteamNetworkingService service, bool value)
    {
        typeof(SteamNetworkingService).GetField("<InLobby>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
    }

    static void SetInitialized(SteamNetworkingService service, bool value)
    {
        typeof(SteamNetworkingService).GetField("<IsInitialized>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
    }

    static void SetLobby(SteamNetworkingService service, ulong lobbyId)
    {
        typeof(SteamNetworkingService).GetField("<Lobby>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new CSteamID(lobbyId));
    }

    static void SetField(SteamNetworkingService service, string fieldName, object value)
    {
        typeof(SteamNetworkingService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
    }

    static void EnqueueTo(SteamNetworkingService service, string queueFieldName)
    {
        var queue = GetField(service, queueFieldName)!;
        var queuedSendType = typeof(SteamNetworkingService).GetNestedType("QueuedSend", BindingFlags.NonPublic)!;
        var queued = Activator.CreateInstance(queuedSendType)!;

        queuedSendType.GetField("Framed", BindingFlags.Instance | BindingFlags.Public)!.SetValue(queued, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        queuedSendType.GetField("Target", BindingFlags.Instance | BindingFlags.Public)!.SetValue(queued, new CSteamID(123));
        queuedSendType.GetField("Reliable", BindingFlags.Instance | BindingFlags.Public)!.SetValue(queued, ReliableType.Unreliable);
        queuedSendType.GetField("Enqueued", BindingFlags.Instance | BindingFlags.Public)!.SetValue(queued, DateTime.UtcNow);

        queue.GetType().GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.Public)!.Invoke(queue, new[] { queued });
    }

    static void SeedUnacked(SteamNetworkingService service, int count)
    {
        var unackedType = typeof(SteamNetworkingService).GetNestedType("UnackedMessage", BindingFlags.NonPublic)!;
        var dict = (IDictionary)GetField(service, "unacked")!;

        for (var i = 0; i < count; i++)
        {
            var unacked = Activator.CreateInstance(unackedType)!;
            var framed = new byte[16];
            framed[0] = 0x10;
            Array.Copy(BitConverter.GetBytes((ulong)i + 1), 0, framed, 1, 8);

            unackedType.GetField("Framed", BindingFlags.Instance | BindingFlags.Public)!.SetValue(unacked, framed);
            unackedType.GetField("Target", BindingFlags.Instance | BindingFlags.Public)!.SetValue(unacked, new CSteamID((ulong)(i + 1000)));
            unackedType.GetField("Reliable", BindingFlags.Instance | BindingFlags.Public)!.SetValue(unacked, ReliableType.Reliable);
            unackedType.GetField("LastSent", BindingFlags.Instance | BindingFlags.Public)!.SetValue(unacked, DateTime.UtcNow);
            unackedType.GetField("Attempts", BindingFlags.Instance | BindingFlags.Public)!.SetValue(unacked, 1);

            dict.Add(ValueTuple.Create((ulong)(i + 1000), (ulong)i + 1), unacked);
        }
    }

    static void SeedLastSeenSequence(SteamNetworkingService service)
    {
        var dict = (IDictionary)GetField(service, "lastSeenSequence")!;
        dict.Add(1234UL, new System.Collections.Generic.Dictionary<uint, ulong> { [TestModId] = 9UL });
    }

    static void SeedRateLimiters(SteamNetworkingService service)
    {
        var dict = (IDictionary)GetField(service, "rateLimiters")!;
        var limiterType = typeof(SteamNetworkingService).GetNestedType("SlidingWindowRateLimiter", BindingFlags.NonPublic)!;
        var limiter = Activator.CreateInstance(limiterType, 4, TimeSpan.FromSeconds(2))!;
        dict.Add(5678UL, limiter);
    }

    static void SeedOutgoingSequencePerMod(SteamNetworkingService service, uint modId = TestModId)
    {
        var dict = (IDictionary)GetField(service, "outgoingSequencePerMod")!;
        dict.Add(modId, 7UL);
    }

    static void AssertDictionaryCount(SteamNetworkingService service, string dictionaryFieldName, int expected)
    {
        var dict = (ICollection)GetField(service, dictionaryFieldName)!;
        Assert.Equal(expected, dict.Count);
    }

    static void AssertQueueCount(SteamNetworkingService service, string queueFieldName, int expected)
    {
        var queue = (ICollection)GetField(service, queueFieldName)!;
        Assert.Equal(expected, queue.Count);
    }

    static void AssertUnackedCount(SteamNetworkingService service, int expected)
    {
        var dict = (ICollection)GetField(service, "unacked")!;
        Assert.Equal(expected, dict.Count);
    }

    static object? GetField(SteamNetworkingService service, string fieldName)
    {
        return typeof(SteamNetworkingService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
    }

    static object? InvokeNonPublic(SteamNetworkingService service, string methodName, params object[] args)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var types = Array.ConvertAll(args, a => a.GetType());
        var method = typeof(SteamNetworkingService).GetMethod(methodName, flags, null, types, null)
            ?? typeof(SteamNetworkingService).GetMethod(methodName, flags)!;
        return method.Invoke(service, args);
    }

    static void InvokeLobbyEnter(SteamNetworkingService service, ulong lobbyId)
    {
        InvokeNonPublic(service, "OnLobbyEnter", new LobbyEnter_t { m_ulSteamIDLobby = lobbyId });
    }

    static void DispatchPing(SteamNetworkingService service, int value)
    {
        DispatchMessage(service, "OnPing", value);
    }

    static void DispatchMessage(SteamNetworkingService service, string methodName, int value)
    {
        var invokeLocal = typeof(SteamNetworkingService).GetMethod("InvokeLocalMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        if (invokeLocal != null)
        {
            var message = new Message(TestModId, methodName, 0);
            message.WriteObject(typeof(int), value);
            invokeLocal.Invoke(service, new object[] { message, new CSteamID(1234UL) });
            return;
        }

        service.Initialize();
        service.CreateLobby();
        service.RPC(TestModId, methodName, ReliableType.Reliable, value);
    }
}
