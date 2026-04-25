using System;
using System.Reflection;
using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
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

    sealed class ValueReceiver
    {
        [CustomRPC]
        void OnInt(int value) { }
    }
}
