using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Steamworks;
using System;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamNetworkingServiceSignatureCompressionTests
{
    const uint TestModId = 4242;
    static readonly CSteamID Sender = new(987654321UL);

    [Fact]
    public void ProcessIncomingFrame_SignedUncompressedPayload_IsAccepted()
    {
        using var rsa = new RSACryptoServiceProvider(2048);
        var service = new SteamNetworkingService();
        var sink = new RpcSink();
        using var registration = service.RegisterNetworkObject(sink, TestModId);
        service.RegisterModSigner(TestModId, bytes => rsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256")));
        service.RegisterModPublicKey(TestModId, rsa.ExportParameters(false));

        var frame = BuildFrame(service, BuildMessage("short"), ReliableType.Unreliable);
        ProcessIncomingFrame(service, frame);

        Assert.Equal(1, sink.Calls);
        Assert.Equal("short", sink.LastPayload);
    }

    [Fact]
    public void ProcessIncomingFrame_SignedCompressedPayload_IsAccepted()
    {
        using var rsa = new RSACryptoServiceProvider(2048);
        var service = new SteamNetworkingService();
        var sink = new RpcSink();
        using var registration = service.RegisterNetworkObject(sink, TestModId);
        service.RegisterModSigner(TestModId, bytes => rsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256")));
        service.RegisterModPublicKey(TestModId, rsa.ExportParameters(false));

        var payload = new string('x', 6000);
        var frame = BuildFrame(service, BuildMessage(payload), ReliableType.Unreliable);
        ProcessIncomingFrame(service, frame);

        Assert.Equal(1, sink.Calls);
        Assert.Equal(payload, sink.LastPayload);
    }

    [Fact]
    public void ProcessIncomingFrame_SignedPayloadWithWrongKey_IsRejected()
    {
        using var signerRsa = new RSACryptoServiceProvider(2048);
        using var verifierRsa = new RSACryptoServiceProvider(2048);
        var service = new SteamNetworkingService();
        var sink = new RpcSink();
        using var registration = service.RegisterNetworkObject(sink, TestModId);
        service.RegisterModSigner(TestModId, bytes => signerRsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256")));
        service.RegisterModPublicKey(TestModId, verifierRsa.ExportParameters(false));

        var frame = BuildFrame(service, BuildMessage("bad-signature"), ReliableType.Unreliable);
        ProcessIncomingFrame(service, frame);

        Assert.Equal(0, sink.Calls);
    }

    [Fact]
    public void ProcessIncomingFrame_CompressedUnsignedPayload_BehaviorUnchanged()
    {
        var service = new SteamNetworkingService();
        var sink = new RpcSink();
        using var registration = service.RegisterNetworkObject(sink, TestModId);

        var payload = new string('z', 6000);
        var frame = BuildFrame(service, BuildMessage(payload), ReliableType.Unreliable);
        ProcessIncomingFrame(service, frame);

        Assert.Equal(1, sink.Calls);
        Assert.Equal(payload, sink.LastPayload);
    }

    [Fact]
    public void RegisterModPublicKey_CachesSnapshot_AndReusesAcrossReceivePath()
    {
        using var signerRsa = new RSACryptoServiceProvider(2048);
        using var verifierRsa = new RSACryptoServiceProvider(2048);
        var service = new SteamNetworkingService();
        var sink = new RpcSink();
        using var registration = service.RegisterNetworkObject(sink, TestModId);
        service.RegisterModSigner(TestModId, bytes => signerRsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256")));
        service.RegisterModPublicKey(TestModId, signerRsa.ExportParameters(false));

        var firstSnapshot = GetModPublicKeySnapshot(service);
        var frame = BuildFrame(service, BuildMessage("snapshot"), ReliableType.Unreliable);
        ProcessIncomingFrame(service, frame);
        ProcessIncomingFrame(service, frame);

        var secondSnapshot = GetModPublicKeySnapshot(service);
        Assert.Same(firstSnapshot, secondSnapshot);
        Assert.Equal(2, sink.Calls);

        service.RegisterModPublicKey(TestModId + 1, verifierRsa.ExportParameters(false));
        var thirdSnapshot = GetModPublicKeySnapshot(service);

        Assert.NotSame(secondSnapshot, thirdSnapshot);
        Assert.Equal(2, thirdSnapshot.Length);
    }

    static Message BuildMessage(string payload)
    {
        var message = new Message(TestModId, nameof(RpcSink.Handle), 0);
        message.WriteObject(typeof(string), payload);
        return message;
    }

    static byte[] BuildFrame(SteamNetworkingService service, Message message, ReliableType reliable)
    {
        return (byte[])InvokeNonPublic(service, "BuildFramedBytesWithMeta", message, TestModId, reliable)!;
    }

    static void ProcessIncomingFrame(SteamNetworkingService service, byte[] frame)
    {
        InvokeNonPublic(service, "ProcessIncomingFrame", frame, Sender);
    }

    static object? InvokeNonPublic(SteamNetworkingService service, string methodName, params object[] args)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var types = Array.ConvertAll(args, a => a.GetType());
        var method = typeof(SteamNetworkingService).GetMethod(methodName, flags, null, types, null)
            ?? typeof(SteamNetworkingService).GetMethod(methodName, flags)!;
        return method.Invoke(service, args);
    }

    static Array GetModPublicKeySnapshot(SteamNetworkingService service)
    {
        return (Array)typeof(SteamNetworkingService).GetField("modPublicKeysSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    }

    sealed class RpcSink
    {
        public int Calls;
        public string? LastPayload;

        [CustomRPC]
        public void Handle(string payload)
        {
            Calls++;
            LastPayload = payload;
        }
    }
}
