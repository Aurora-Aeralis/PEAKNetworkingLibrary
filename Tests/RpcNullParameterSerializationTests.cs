using System;
using System.Collections;
using System.Reflection;
using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Steamworks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class RpcNullParameterSerializationTests
{
    const uint TestModId = 9090;
    static readonly MethodInfo OfflineBuildMessage = typeof(OfflineNetworkingService).GetMethod("BuildMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly MethodInfo SteamBuildMessage = typeof(SteamNetworkingService).GetMethod("BuildMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;

    sealed class NullReceiver
    {
        [CustomRPC]
        void StringAndBytes(string text, byte[] bytes) { }
    }

    [Fact]
    public void OfflineBuildMessage_AllowsNull_ForReferenceParameters_FromHandlerSignature()
    {
        var service = new OfflineNetworkingService();
        using var token = service.RegisterNetworkObject(new NullReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "StringAndBytes", 0, new object?[] { null, null }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Null(read.ReadObject(typeof(string)));
        Assert.Null(read.ReadObject(typeof(byte[])));
    }

    [Fact]
    public void OfflineBuildMessage_RejectsNull_ForNonNullableValueType()
    {
        var service = new OfflineNetworkingService();
        using var token = service.RegisterNetworkObject(new ValueReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "OnInt", 0, new object?[] { null }, null });
        Assert.Null(msg);
    }

    [Fact]
    public void OfflineBuildMessage_FallbackTypedSerialization_AllowsNull()
    {
        var service = new OfflineNetworkingService();
        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "NoHandler", 0, new object?[] { null, null }, new[] { typeof(string), typeof(byte[]) } });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Null(read.ReadObject(typeof(string)));
        Assert.Null(read.ReadObject(typeof(byte[])));
    }

    [Fact]
    public void SteamBuildMessage_AllowsNull_ForReferenceParameters_FromHandlerSignature()
    {
        var service = new SteamNetworkingService();
        using var token = service.RegisterNetworkObject(new NullReceiver(), TestModId);

        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "StringAndBytes", 0, new object?[] { null, null }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Null(read.ReadObject(typeof(string)));
        Assert.Null(read.ReadObject(typeof(byte[])));
    }

    [Fact]
    public void SteamBuildMessage_FallbackTypedSerialization_AllowsNull()
    {
        var service = new SteamNetworkingService();
        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "NoHandler", 0, new object?[] { null, null }, new[] { typeof(string), typeof(byte[]) } });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Null(read.ReadObject(typeof(string)));
        Assert.Null(read.ReadObject(typeof(byte[])));
    }

    [Fact]
    public void SteamRPCTarget_SelfTarget_InvokesLocalHandler_WithoutQueueing()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        var receiver = new CounterReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        service.RPCTarget(TestModId, nameof(CounterReceiver.Increment), SteamUser.GetSteamID(), ReliableType.Reliable, 3);

        Assert.Equal(3, receiver.Total);
        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
        AssertQueueCount(service, "highQueue", 0);
    }

    [Fact]
    public void SteamRPCTarget_SelfTarget_TypedOverload_InvokesLocalHandler_WithoutQueueing()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        var receiver = new CounterReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        service.RPCTarget(TestModId, nameof(CounterReceiver.Increment), SteamUser.GetSteamID(), ReliableType.Reliable, new[] { typeof(int) }, 5);

        Assert.Equal(5, receiver.Total);
        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
        AssertQueueCount(service, "highQueue", 0);
    }

    [Fact]
    public void SteamRPCTarget_RemoteTarget_StillUsesQueueTransport()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        var receiver = new CounterReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        var local = SteamUser.GetSteamID();
        var remote = new CSteamID(local.m_SteamID == 0 ? 1UL : local.m_SteamID + 1);
        service.RPCTarget(TestModId, nameof(CounterReceiver.Increment), remote, ReliableType.Reliable, 11);

        Assert.Equal(0, receiver.Total);
        AssertQueueCount(service, "normalQueue", 1);
    }

    sealed class ValueReceiver
    {
        [CustomRPC]
        void OnInt(int value) { }
    }

    sealed class CounterReceiver
    {
        public int Total { get; private set; }

        [CustomRPC]
        public void Increment(int value) => Total += value;
    }

    static void SetInLobby(SteamNetworkingService service, bool value)
    {
        typeof(SteamNetworkingService).GetField("<InLobby>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
    }

    static void AssertQueueCount(SteamNetworkingService service, string queueFieldName, int expected)
    {
        var queue = (ICollection)typeof(SteamNetworkingService).GetField(queueFieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Equal(expected, queue.Count);
    }
}
