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
    static readonly CSteamID LocalSteamId = new(76561198000000000UL);
    static readonly MethodInfo OfflineBuildMessage = typeof(OfflineNetworkingService).GetMethod("BuildMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly MethodInfo SteamBuildMessage = typeof(SteamNetworkingService).GetMethod("BuildMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly MethodInfo SteamDispatchIncoming = typeof(SteamNetworkingService).GetMethod("DispatchIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly MethodInfo SteamCreateRpcInfoInstance = typeof(SteamNetworkingService).GetMethod("CreateRpcInfoInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;

    sealed class NullReceiver
    {
        [CustomRPC]
        void StringAndBytes(string text, byte[] bytes) { }
    }

    sealed class IntOverloadReceiver
    {
        [CustomRPC]
        void Shared(int value) { }
    }

    sealed class StringOverloadReceiver
    {
        [CustomRPC]
        void Shared(string value) { }
    }

    sealed class ReferenceOverloadReceiver
    {
        [CustomRPC]
        void Shared(string value) { }

        [CustomRPC]
        void Shared(byte[] value) { }
    }

    sealed class DispatchOverloadReceiver
    {
        public string? LastOverload;
        public string? LastStringValue;
        public int? LastIntValue;

        [CustomRPC]
        void Shared(int value)
        {
            LastOverload = "int";
            LastIntValue = value;
        }

        [CustomRPC]
        void Shared(string value)
        {
            LastOverload = "string";
            LastStringValue = value;
        }
    }

    sealed class ByteBoolDispatchReceiver
    {
        public string? LastOverload;
        public byte? LastByteValue;
        public bool? LastBoolValue;

        [CustomRPC]
        void Shared(byte value)
        {
            LastOverload = "byte";
            LastByteValue = value;
        }

        [CustomRPC]
        void Shared(bool value)
        {
            LastOverload = "bool";
            LastBoolValue = value;
        }
    }

    sealed class MaskedReceiver
    {
        public int Calls;

        [CustomRPC]
        void Shared(string value) => Calls++;
    }


    sealed class SteamLocalDuplicateListenerReceiver
    {
        public int Calls;
        public bool LastValue;

        [CustomRPC]
        void Shared(bool value)
        {
            Calls++;
            LastValue = value;
        }
    }

    sealed class SteamLocalWireCompatibleOverloadReceiver
    {
        public int BoolCalls;
        public int ByteCalls;
        public bool LastBoolValue;
        public byte LastByteValue;

        [CustomRPC]
        void Shared(byte value)
        {
            ByteCalls++;
            LastByteValue = value;
        }

        [CustomRPC]
        void Shared(bool value)
        {
            BoolCalls++;
            LastBoolValue = value;
        }
    }

    sealed class ParamlessRpcInfoWithUlongSenderSteamID
    {
        public ulong SenderSteamID;
    }

    sealed class ParamlessRpcInfoWithCSteamIDSender
    {
        public CSteamID Sender;
    }

    sealed class ParamlessRpcInfoWithSteamIdentityProperties
    {
        public ulong SteamId64 { get; set; }
        public string SteamIdString { get; set; } = string.Empty;
    }

    sealed class TransportRpcInfoReceiver
    {
        [CustomRPC]
        void Shared(string value, RPCInfo info) { }
    }

    sealed class ForeignRpcInfoReceiver
    {
        [CustomRPC]
        void Shared(string value, Foo.RPCInfo info) { }
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
    public void OfflineBuildMessage_AllowsBoxedValue_ForNullableValueParameter()
    {
        var service = new OfflineNetworkingService();
        using var token = service.RegisterNetworkObject(new NullableValueReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "OnNullableInt", 0, new object?[] { 42 }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Equal(42, (int?)read.ReadObject(typeof(int?)));
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
    public void OfflineBuildMessage_ChoosesAssignableOverload_InsteadOfFirstHandler()
    {
        var service = new OfflineNetworkingService();
        using var first = service.RegisterNetworkObject(new IntOverloadReceiver(), TestModId);
        using var second = service.RegisterNetworkObject(new StringOverloadReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { "hi" }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Equal("hi", read.ReadObject(typeof(string)));
    }

    [Fact]
    public void OfflineBuildMessage_ChoosesNullAcceptingReferenceOverload()
    {
        var service = new OfflineNetworkingService();
        using var first = service.RegisterNetworkObject(new IntOverloadReceiver(), TestModId);
        using var second = service.RegisterNetworkObject(new StringOverloadReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { null }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Null(read.ReadObject(typeof(string)));
    }

    [Fact]
    public void OfflineBuildMessage_UsesTypedNullOverload_WhenHandlersExist()
    {
        var service = new OfflineNetworkingService();
        using var token = service.RegisterNetworkObject(new ReferenceOverloadReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { null }, new[] { typeof(byte[]) } });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Contains("System.Byte[]", read.OverloadKey);
        Assert.Null(read.ReadObject(typeof(byte[])));
    }

    [Fact]
    public void OfflineBuildMessage_PrefersNonNullableValueOverload_BeforeNullableFallback()
    {
        var service = new OfflineNetworkingService();
        using var nullableToken = service.RegisterNetworkObject(new NullableIntOverloadReceiver(), TestModId);
        using var intToken = service.RegisterNetworkObject(new IntOverloadReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { 42 }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.DoesNotContain("System.Nullable", read.OverloadKey);
        Assert.Equal(42, read.ReadObject(typeof(int)));
    }

    [Fact]
    public void OfflineBuildMessage_RecognizesModulesRpcInfo_AsTransportMetadata()
    {
        var service = new OfflineNetworkingService();
        using var token = service.RegisterNetworkObject(new TransportRpcInfoReceiver(), TestModId);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { "hi" }, null });

        Assert.NotNull(msg);
    }

    [Fact]
    public void OfflineBuildMessage_DoesNotTreatForeignRpcInfo_AsTransportMetadata()
    {
        var service = new OfflineNetworkingService();
        using var token = service.RegisterNetworkObject(new ForeignRpcInfoReceiver(), TestModId);

        var missingForeignArg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { "hi" }, null });
        var withForeignArg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { "hi", null }, null });

        Assert.Null(missingForeignArg);
        Assert.NotNull(withForeignArg);
    }

    [Fact]
    public void OfflineDispatchIncoming_InvokesMatchingOverloadWithoutCorruptingReader()
    {
        var service = new OfflineNetworkingService();
        service.Initialize();
        service.CreateLobby();
        var receiver = new DispatchOverloadReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        service.RPC(TestModId, "Shared", ReliableType.Reliable, "hi");

        Assert.Equal("string", receiver.LastOverload);
        Assert.Equal("hi", receiver.LastStringValue);
        Assert.Null(receiver.LastIntValue);
    }

    [Fact]
    public void OfflineDispatchIncoming_UsesOverloadIdentity_ForWireCompatibleOverloads()
    {
        var service = new OfflineNetworkingService();
        service.Initialize();
        service.CreateLobby();
        var receiver = new ByteBoolDispatchReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        service.RPC(TestModId, "Shared", ReliableType.Reliable, true);

        Assert.Equal("bool", receiver.LastOverload);
        Assert.True(receiver.LastBoolValue);
        Assert.Null(receiver.LastByteValue);
    }

    [Fact]
    public void OfflineBuildMessage_ReturnsNull_WhenOnlyDifferentMaskHandlersExist()
    {
        var service = new OfflineNetworkingService();
        var receiver = new MaskedReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId, mask: 1);

        var msg = (Message?)OfflineBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 2, new object?[] { "hi" }, null });

        Assert.Null(msg);
        Assert.Equal(0, receiver.Calls);
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
    public void SteamBuildMessage_AllowsBoxedValue_ForNullableValueParameter()
    {
        var service = new SteamNetworkingService();
        using var token = service.RegisterNetworkObject(new NullableValueReceiver(), TestModId);

        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "OnNullableInt", 0, new object?[] { 42 }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Equal(42, (int?)read.ReadObject(typeof(int?)));
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
    public void SteamBuildMessage_ReturnsNull_WhenOnlyDifferentMaskHandlersExist()
    {
        var service = new SteamNetworkingService();
        var receiver = new MaskedReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId, mask: 1);

        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 2, new object?[] { "hi" }, null });

        Assert.Null(msg);
        Assert.Equal(0, receiver.Calls);
    }

    [Fact]
    public void SteamBuildMessage_UsesTypedNullOverload_WhenHandlersExist()
    {
        var service = new SteamNetworkingService();
        using var token = service.RegisterNetworkObject(new ReferenceOverloadReceiver(), TestModId);

        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { null }, new[] { typeof(byte[]) } });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.Contains("System.Byte[]", read.OverloadKey);
        Assert.Null(read.ReadObject(typeof(byte[])));
    }

    [Fact]
    public void SteamBuildMessage_PrefersNonNullableValueOverload_BeforeNullableFallback()
    {
        var service = new SteamNetworkingService();
        using var nullableToken = service.RegisterNetworkObject(new NullableIntOverloadReceiver(), TestModId);
        using var intToken = service.RegisterNetworkObject(new IntOverloadReceiver(), TestModId);

        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { 42 }, null });

        Assert.NotNull(msg);
        var read = new Message(msg!.ToArray());
        Assert.DoesNotContain("System.Nullable", read.OverloadKey);
        Assert.Equal(42, read.ReadObject(typeof(int)));
    }

    [Fact]
    public void SteamBuildMessage_RecognizesModulesRpcInfo_AsTransportMetadata()
    {
        var service = new SteamNetworkingService();
        using var token = service.RegisterNetworkObject(new TransportRpcInfoReceiver(), TestModId);

        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { "hi" }, null });

        Assert.NotNull(msg);
    }

    [Fact]
    public void SteamBuildMessage_DoesNotTreatForeignRpcInfo_AsTransportMetadata()
    {
        var service = new SteamNetworkingService();
        using var token = service.RegisterNetworkObject(new ForeignRpcInfoReceiver(), TestModId);

        var missingForeignArg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { "hi" }, null });
        var withForeignArg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { "hi", null }, null });

        Assert.Null(missingForeignArg);
        Assert.NotNull(withForeignArg);
    }

    [Fact]
    public void SteamDispatchIncoming_UsesOverloadIdentity_ForWireCompatibleOverloads()
    {
        var service = new SteamNetworkingService();
        var receiver = new ByteBoolDispatchReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        var msg = (Message?)SteamBuildMessage.Invoke(service, new object?[] { TestModId, "Shared", 0, new object?[] { true }, null });
        Assert.NotNull(msg);

        SteamDispatchIncoming.Invoke(service, new object?[] { msg!, new CSteamID(42UL) });

        Assert.Equal("bool", receiver.LastOverload);
        Assert.True(receiver.LastBoolValue);
        Assert.Null(receiver.LastByteValue);
    }

    [Fact]
    public void SteamCreateRpcInfoInstance_ParamlessFallback_AssignsSenderSteamIDAsUlong_WhenFieldIsUlong()
    {
        var service = new SteamNetworkingService();
        var sender = new CSteamID(42UL);

        var obj = SteamCreateRpcInfoInstance.Invoke(service, new object?[] { typeof(ParamlessRpcInfoWithUlongSenderSteamID), sender, false });

        var info = Assert.IsType<ParamlessRpcInfoWithUlongSenderSteamID>(obj);
        Assert.Equal(sender.m_SteamID, info.SenderSteamID);
    }

    [Fact]
    public void SteamCreateRpcInfoInstance_ParamlessFallback_AssignsSenderAsCSteamID_WhenFieldIsCSteamID()
    {
        var service = new SteamNetworkingService();
        var sender = new CSteamID(77UL);

        var obj = SteamCreateRpcInfoInstance.Invoke(service, new object?[] { typeof(ParamlessRpcInfoWithCSteamIDSender), sender, false });

        var info = Assert.IsType<ParamlessRpcInfoWithCSteamIDSender>(obj);
        Assert.Equal(sender, info.Sender);
    }

    [Fact]
    public void SteamCreateRpcInfoInstance_ParamlessFallback_AssignsSteamIdentityProperties()
    {
        var service = new SteamNetworkingService();
        var sender = new CSteamID(321UL);

        var obj = SteamCreateRpcInfoInstance.Invoke(service, new object?[] { typeof(ParamlessRpcInfoWithSteamIdentityProperties), sender, false });

        var info = Assert.IsType<ParamlessRpcInfoWithSteamIdentityProperties>(obj);
        Assert.Equal(sender.m_SteamID, info.SteamId64);
        Assert.Equal(sender.ToString(), info.SteamIdString);
    }

    [Fact]
    public void OfflineCreateRpcInfoInstance_ParamlessFallback_AssignsSteamIdentityProperties()
    {
        var service = new OfflineNetworkingService();
        var createRpcInfo = typeof(OfflineNetworkingService).GetMethod("CreateRpcInfoInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var senderSteamId = 654UL;

        var obj = createRpcInfo.Invoke(service, new object?[] { typeof(ParamlessRpcInfoWithSteamIdentityProperties), senderSteamId, false });

        var info = Assert.IsType<ParamlessRpcInfoWithSteamIdentityProperties>(obj);
        Assert.Equal(senderSteamId, info.SteamId64);
        Assert.Equal(senderSteamId.ToString(), info.SteamIdString);
    }

    [Fact]
    public void SteamRPCTarget_SelfTarget_InvokesLocalHandler_WithoutQueueing()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        SetLocalSteamId(service, LocalSteamId);
        var receiver = new CounterReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        service.RPCTarget(TestModId, nameof(CounterReceiver.Increment), LocalSteamId, ReliableType.Reliable, 3);

        Assert.Equal(3, receiver.Total);
        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
    }

    [Fact]
    public void SteamRPCTarget_SelfTarget_TypedOverload_InvokesLocalHandler_WithoutQueueing()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        SetLocalSteamId(service, LocalSteamId);
        var receiver = new CounterReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        service.RPCTarget(TestModId, nameof(CounterReceiver.Increment), LocalSteamId, ReliableType.Reliable, new[] { typeof(int) }, 5);

        Assert.Equal(5, receiver.Total);
        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
    }

    [Fact]
    public void SteamRPCTarget_SelfTarget_OverloadKey_ChoosesSingleMatchingOverload_WithoutReaderCorruption()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        SetLocalSteamId(service, LocalSteamId);
        var receiver = new SteamLocalWireCompatibleOverloadReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        service.RPCTarget(TestModId, "Shared", LocalSteamId, ReliableType.Reliable, new[] { typeof(bool) }, true);

        Assert.Equal(1, receiver.BoolCalls);
        Assert.Equal(0, receiver.ByteCalls);
        Assert.True(receiver.LastBoolValue);
        Assert.Equal((byte)0, receiver.LastByteValue);
        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
    }


    [Fact]
    public void SteamRPCTarget_SelfTarget_OverloadKey_InvokesAllMatchingLocalRegistrations()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        SetLocalSteamId(service, LocalSteamId);
        var receiver = new SteamLocalDuplicateListenerReceiver();
        using var first = service.RegisterNetworkObject(receiver, TestModId);
        using var second = service.RegisterNetworkObject(receiver, TestModId);

        service.RPCTarget(TestModId, "Shared", LocalSteamId, ReliableType.Reliable, new[] { typeof(bool) }, true);

        Assert.Equal(1, receiver.Calls);
        Assert.True(receiver.LastValue);
        AssertQueueCount(service, "normalQueue", 0);
        AssertQueueCount(service, "lowQueue", 0);
    }

    [Fact]
    public void SteamRPCTarget_RemoteTarget_StillUsesQueueTransport()
    {
        var service = new SteamNetworkingService();
        SetInLobby(service, true);
        SetLocalSteamId(service, LocalSteamId);
        var receiver = new CounterReceiver();
        using var token = service.RegisterNetworkObject(receiver, TestModId);

        var local = LocalSteamId;
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

    sealed class NullableValueReceiver
    {
        [CustomRPC]
        void OnNullableInt(int? value) { }
    }

    sealed class NullableIntOverloadReceiver
    {
        [CustomRPC]
        void Shared(int? value) { }
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

    static void SetLocalSteamId(SteamNetworkingService service, CSteamID value)
    {
        typeof(SteamNetworkingService).GetField("getLocalSteamId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, (Func<CSteamID>)(() => value));
    }

    static void AssertQueueCount(SteamNetworkingService service, string queueFieldName, int expected)
    {
        var field = typeof(SteamNetworkingService).GetField(queueFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            Assert.Equal(0, expected);
            return;
        }
        var queue = (ICollection)field.GetValue(service)!;
        Assert.Equal(expected, queue.Count);
    }
}
