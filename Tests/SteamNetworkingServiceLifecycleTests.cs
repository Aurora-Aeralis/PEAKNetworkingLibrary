using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

    sealed class ConcurrentRpcReceiver
    {
        public int CallCount;

        [CustomRPC]
        void OnConcurrentPing(int value)
        {
            Interlocked.Increment(ref CallCount);
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
    public void JoinLobby_WithInvalidLobbyId_DoesNotThrow_WhenNetLoggerIsUnavailable()
    {
        using var _ = WithUnavailableNetLogger();
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
    public void Shutdown_WhileInLobby_RaisesLobbyLeftOnce()
    {
        var service = new SteamNetworkingService();
        var lobbyLeftCount = 0;
        service.LobbyLeft += () => lobbyLeftCount++;

        InvokeLobbyEnter(service, 9001UL);

        service.Shutdown();
        service.Shutdown();

        Assert.Equal(1, lobbyLeftCount);
        Assert.False(service.InLobby);
    }

    [Fact]
    public void Shutdown_ClearsRegisteredHandlers_AndModSecurityArtifacts()
    {
        var service = new SteamNetworkingService();
        var receiver = new RpcReceiver();
        using var rsa = RSA.Create(2048);

        service.RegisterNetworkObject(receiver, TestModId);
        service.RegisterModSigner(TestModId, bytes => bytes);
        service.RegisterModPublicKey(TestModId, rsa.ExportParameters(false));
        service.Shutdown();

        var rpcs = (IDictionary)GetField(service, "rpcs")!;
        var modSigners = (IDictionary)GetField(service, "modSigners")!;
        var modPublicKeys = (IDictionary)GetField(service, "modPublicKeys")!;
        Assert.Equal(0, rpcs.Count);
        Assert.Equal(0, modSigners.Count);
        Assert.Equal(0, modPublicKeys.Count);
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
    public void RegisterNetworkObject_RegistersOnlyAttributedInstanceMethods_AndDispatchParityHolds()
    {
        var service = new SteamNetworkingService();
        var receiver = new VisibilityRpcReceiver();
        using var _ = service.RegisterNetworkObject(receiver, TestModId, mask: 5);

        Assert.Equal(2, GetRegisteredHandlerCount(service, TestModId));

        DispatchMessage(service, nameof(VisibilityRpcReceiver.PublicPing), 2);
        DispatchMessage(service, "PrivatePing", 3);
        DispatchMessage(service, nameof(VisibilityRpcReceiver.NotRpc), 4);

        Assert.Equal(2, receiver.PublicCallCount);
        Assert.Equal(3, receiver.PrivateCallCount);
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
    public void Initialize_WhenCallbackAndCryptoSetupFails_KeepsServiceUninitialized_ClearsPartialState_AndPreservesExistingPumpState()
    {
        var service = new SteamNetworkingService();
        var rsaFactoryField = typeof(SteamNetworkingService).GetField("localRsaFactory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        rsaFactoryField.SetValue(service, (Func<RSACryptoServiceProvider>)(() => throw new InvalidOperationException("simulated init failure")));
        SteamCallbackPump.EnablePumping();

        try
        {
            service.Initialize();

            Assert.False(service.IsInitialized);
            Assert.True(SteamCallbackPump.CallbackPumpingEnabled);
            Assert.Null(GetField(service, "cbLobbyEnter"));
            Assert.Null(GetField(service, "cbLobbyCreated"));
            Assert.Null(GetField(service, "cbLobbyChatUpdate"));
            Assert.Null(GetField(service, "cbLobbyDataUpdate"));
            Assert.Null(GetField(service, "LocalRsa"));
        }
        finally
        {
            SteamCallbackPump.DisablePumping();
        }
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
    public void OnLobbyEnter_RefreshPlayerList_UsesConfiguredLobbyMemberDelegates()
    {
        var service = new SteamNetworkingService();
        var numCalls = 0;
        var indexCalls = 0;

        SetField(service, "getNumLobbyMembers", (Func<CSteamID, int>)(_ =>
        {
            numCalls++;
            return 2;
        }));
        SetField(service, "getLobbyMemberByIndex", (Func<CSteamID, int, CSteamID>)((_, index) =>
        {
            indexCalls++;
            return new CSteamID((ulong)(100 + index));
        }));

        InvokeLobbyEnter(service, 9001UL);

        Assert.Equal(1, numCalls);
        Assert.Equal(2, indexCalls);
    }

    [Fact]
    public void OnLobbyCreated_RefreshPlayerList_UsesConfiguredLobbyMemberDelegates()
    {
        var service = new SteamNetworkingService();
        var numCalls = 0;
        var indexCalls = 0;

        SetField(service, "getNumLobbyMembers", (Func<CSteamID, int>)(_ =>
        {
            numCalls++;
            return 3;
        }));
        SetField(service, "getLobbyMemberByIndex", (Func<CSteamID, int, CSteamID>)((_, index) =>
        {
            indexCalls++;
            return new CSteamID((ulong)(200 + index));
        }));

        InvokeNonPublic(service, "OnLobbyCreated", new LobbyCreated_t
        {
            m_eResult = EResult.k_EResultOK,
            m_ulSteamIDLobby = 9002UL
        });

        Assert.Equal(1, numCalls);
        Assert.Equal(3, indexCalls);
    }

    [Fact]
    public void OnLobbyChatUpdate_RefreshPlayerList_UsesConfiguredLobbyMemberDelegates()
    {
        var service = new SteamNetworkingService();
        var numCalls = 0;
        var indexCalls = 0;

        SetLobby(service, 9003UL);
        SetField(service, "getNumLobbyMembers", (Func<CSteamID, int>)(_ =>
        {
            numCalls++;
            return 1;
        }));
        SetField(service, "getLobbyMemberByIndex", (Func<CSteamID, int, CSteamID>)((_, _) =>
        {
            indexCalls++;
            return new CSteamID(300UL);
        }));

        InvokeNonPublic(service, "OnLobbyChatUpdate", new LobbyChatUpdate_t
        {
            m_ulSteamIDUserChanged = 300UL,
            m_rgfChatMemberStateChange = (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered
        });

        Assert.Equal(1, numCalls);
        Assert.Equal(1, indexCalls);
    }

    [Fact]
    public void SetLobbyData_AndSetPlayerData_DoNotThrow_WhenSteamCallsFail()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        SetLobby(service, 9001UL);
        SetField(service, "setLobbyData", (Action<CSteamID, string, string>)((_, _, _) => throw new InvalidOperationException("set lobby failed")));
        SetField(service, "setLobbyMemberData", (Action<CSteamID, string, string>)((_, _, _) => throw new InvalidOperationException("set player failed")));

        var lobbyEx = Record.Exception(() => service.SetLobbyData("map", "forest"));
        var playerEx = Record.Exception(() => service.SetPlayerData("team", "blue"));

        Assert.Null(lobbyEx);
        Assert.Null(playerEx);
    }

    [Fact]
    public void GetLobbyData_AndGetPlayerData_ReturnDefault_WhenSteamCallsFail()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        SetLobby(service, 9001UL);
        SetField(service, "getLobbyData", (Func<CSteamID, string, string>)((_, _) => throw new InvalidOperationException("get lobby failed")));
        SetField(service, "getLobbyMemberData", (Func<CSteamID, CSteamID, string, string>)((_, _, _) => throw new InvalidOperationException("get player failed")));

        var lobbyEx = Record.Exception(() =>
        {
            var value = service.GetLobbyData<int>("round");
            Assert.Equal(default, value);
        });
        var playerEx = Record.Exception(() =>
        {
            var value = service.GetPlayerData<int>(1234UL, "score");
            Assert.Equal(default, value);
        });

        Assert.Null(lobbyEx);
        Assert.Null(playerEx);
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

    [Fact]
    public void MultipleInstances_HaveIsolatedInboundBuffers_WithConfiguredCapacity()
    {
        var first = new SteamNetworkingService();
        var second = new SteamNetworkingService();

        var firstBuffer = GetInboundBuffer(first);
        var secondBuffer = GetInboundBuffer(second);

        Assert.Equal(500, firstBuffer.Length);
        Assert.Equal(500, secondBuffer.Length);
        Assert.NotSame(firstBuffer, secondBuffer);

        firstBuffer[0] = new IntPtr(111);
        secondBuffer[0] = new IntPtr(222);

        Assert.Equal(new IntPtr(111), firstBuffer[0]);
        Assert.Equal(new IntPtr(222), secondBuffer[0]);
    }

    [Fact]
    public async Task MultipleInstances_CanMutateInboundBuffersConcurrently_WithoutCrossInstanceWrites()
    {
        var first = new SteamNetworkingService();
        var second = new SteamNetworkingService();
        var firstBuffer = GetInboundBuffer(first);
        var secondBuffer = GetInboundBuffer(second);
        var writes = Math.Min(firstBuffer.Length, secondBuffer.Length);

        await Task.WhenAll(
            Task.Run(() =>
            {
                for (var i = 0; i < writes; i++) firstBuffer[i] = new IntPtr(i + 1);
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < writes; i++) secondBuffer[i] = new IntPtr(i + 10_000);
            })
        );

        Assert.Equal(new IntPtr(1), firstBuffer[0]);
        Assert.Equal(new IntPtr(10_000), secondBuffer[0]);
        Assert.Equal(new IntPtr(writes), firstBuffer[writes - 1]);
        Assert.Equal(new IntPtr(writes - 1 + 10_000), secondBuffer[writes - 1]);
    }

    [Fact]
    public void SendWithPossibleAck_SelfTarget_FrameUsesIncomingPipeline_AndClearsUnacked()
    {
        var service = new SteamNetworkingService();
        var receiver = new RpcReceiver();
        using var _ = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        var local = SteamUser.GetSteamID();
        var msg = new Message(TestModId, nameof(RpcReceiver.OnPing), 0);
        msg.WriteObject(typeof(int), 73);
        var framed = BuildFramed(service, msg, TestModId, ReliableType.Reliable);

        InvokeNonPublic(service, "SendWithPossibleAck", framed, local, ReliableType.Reliable);

        Assert.Equal(1, receiver.CallCount);
        Assert.Equal(73, receiver.LastValue);
        AssertUnackedCount(service, 0);
    }

    [Fact]
    public void SendBytes_SelfTarget_FrameStillHonorsIncomingValidator()
    {
        var service = new SteamNetworkingService();
        var receiver = new RpcReceiver();
        using var _ = service.RegisterNetworkObject(receiver, TestModId, mask: 0);

        var local = SteamUser.GetSteamID();
        var msg = new Message(TestModId, nameof(RpcReceiver.OnPing), 0);
        msg.WriteObject(typeof(int), 5);
        var framed = BuildFramed(service, msg, TestModId, ReliableType.Unreliable);
        var validatorCalls = 0;
        service.IncomingValidator = (_, sender) =>
        {
            validatorCalls++;
            Assert.Equal(local.m_SteamID, sender);
            return false;
        };

        InvokeNonPublic(service, "SendBytes", framed, local, ReliableType.Unreliable);

        Assert.Equal(1, validatorCalls);
        Assert.Equal(0, receiver.CallCount);
    }


    [Fact]
    public void ProcessIncomingFrame_FragmentCleanupGate_DefersStaleSweepUntilInterval()
    {
        var service = new SteamNetworkingService();
        var sender = new CSteamID(404UL);
        var staleKey = ValueTuple.Create(sender.m_SteamID, 77UL);

        SeedFragmentBuffer(service, staleKey, total: 2, DateTime.UtcNow - TimeSpan.FromMinutes(2), new byte[] { 9, 9 }, index: 0);
        SetField(service, "nextFragmentCleanupAt", DateTime.UtcNow + TimeSpan.FromMinutes(1));

        InvokeNonPublic(service, "ProcessIncomingFrame", BuildFragmentFrame(1001UL, total: 2, index: 0, new byte[] { 1 }), sender);
        Assert.True(FragmentBuffers(service).Contains(staleKey));

        SetField(service, "nextFragmentCleanupAt", DateTime.UtcNow - TimeSpan.FromSeconds(1));
        InvokeNonPublic(service, "ProcessIncomingFrame", BuildFragmentFrame(1002UL, total: 2, index: 0, new byte[] { 2 }), sender);

        Assert.False(FragmentBuffers(service).Contains(staleKey));
    }

    [Fact]
    public void ProcessIncomingFrame_HighFragmentLoad_CleansExpiredBuffersWhenGateOpens()
    {
        var service = new SteamNetworkingService();
        var sender = new CSteamID(505UL);

        SetField(service, "nextFragmentCleanupAt", DateTime.UtcNow + TimeSpan.FromMinutes(5));
        const int workCount = 600;
        for (var i = 0; i < workCount; i++)
        {
            InvokeNonPublic(service, "ProcessIncomingFrame", BuildFragmentFrame((ulong)(10000 + i), total: 3, index: 0, new byte[] { 1, 2, 3, 4 }), sender);
        }

        Assert.Equal(workCount, FragmentBuffers(service).Count);

        AgeAllFragmentBuffers(service, DateTime.UtcNow - TimeSpan.FromMinutes(2));
        SetField(service, "nextFragmentCleanupAt", DateTime.UtcNow - TimeSpan.FromSeconds(1));
        InvokeNonPublic(service, "ProcessIncomingFrame", BuildFragmentFrame(999999UL, total: 3, index: 0, new byte[] { 7, 7, 7 }), sender);

        Assert.Equal(1, FragmentBuffers(service).Count);
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


    static IDictionary FragmentBuffers(SteamNetworkingService service)
    {
        return (IDictionary)GetField(service, "fragmentBuffers")!;
    }

    static void SeedFragmentBuffer(SteamNetworkingService service, (ulong sender, ulong msgId) key, int total, DateTime firstSeen, byte[] fragment, int index)
    {
        var bufferType = typeof(SteamNetworkingService).GetNestedType("FragmentBuffer", BindingFlags.NonPublic)!;
        var buffer = Activator.CreateInstance(bufferType)!;
        bufferType.GetField("Total", BindingFlags.Instance | BindingFlags.Public)!.SetValue(buffer, total);
        bufferType.GetField("FirstSeen", BindingFlags.Instance | BindingFlags.Public)!.SetValue(buffer, firstSeen);

        var fragments = (IDictionary)bufferType.GetField("Fragments", BindingFlags.Instance | BindingFlags.Public)!.GetValue(buffer)!;
        fragments[index] = fragment;

        FragmentBuffers(service)[key] = buffer;
    }

    static void AgeAllFragmentBuffers(SteamNetworkingService service, DateTime firstSeen)
    {
        var bufferType = typeof(SteamNetworkingService).GetNestedType("FragmentBuffer", BindingFlags.NonPublic)!;
        var firstSeenField = bufferType.GetField("FirstSeen", BindingFlags.Instance | BindingFlags.Public)!;
        foreach (DictionaryEntry entry in FragmentBuffers(service)) firstSeenField.SetValue(entry.Value, firstSeen);
    }

    static byte[] BuildFragmentFrame(ulong msgId, int total, int index, byte[] payload)
    {
        var frame = new byte[25 + payload.Length];
        frame[0] = 0x01;
        BitConverter.GetBytes(msgId).CopyTo(frame, 1);
        BitConverter.GetBytes(0UL).CopyTo(frame, 9);
        BitConverter.GetBytes(total).CopyTo(frame, 17);
        BitConverter.GetBytes(index).CopyTo(frame, 21);
        Buffer.BlockCopy(payload, 0, frame, 25, payload.Length);
        return frame;
    }

    static object? GetField(SteamNetworkingService service, string fieldName)
    {
        return typeof(SteamNetworkingService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
    }

    static IntPtr[] GetInboundBuffer(SteamNetworkingService service)
    {
        return (IntPtr[])GetField(service, "inMessages")!;
    }

    static int GetRegisteredHandlerCount(SteamNetworkingService service, uint modId)
    {
        var rpcs = (Dictionary<uint, Dictionary<string, List<MessageHandler>>>)GetField(service, "rpcs")!;
        if (!rpcs.TryGetValue(modId, out var methods)) return 0;
        return methods.Sum(entry => entry.Value.Count);
    }

    static byte[] BuildFramed(SteamNetworkingService service, Message message, uint modId, ReliableType reliable)
    {
        return (byte[])InvokeNonPublic(service, "BuildFramedBytesWithMeta", message, modId, reliable)!;
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
