using NetworkingLibrary.Services;
using Steamworks;
using System;
using System.Collections;
using System.Reflection;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamNetworkingServiceFragmentSweepTests
{
    static readonly MethodInfo ProcessIncomingFrameMethod = typeof(SteamNetworkingService).GetMethod("ProcessIncomingFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void FragmentSweep_RunsPeriodically_NotPerFragmentPacket()
    {
        var service = new SteamNetworkingService();
        var now = DateTime.UtcNow;

        SetField(service, "fragmentUtcNow", (Func<DateTime>)(() => now));
        SetField(service, "fragmentSweepInterval", TimeSpan.FromSeconds(5));
        SetField(service, "nextFragmentSweepUtc", DateTime.MinValue);

        var sender = new CSteamID(42UL);
        for (var i = 0; i < 2000; i++)
        {
            InvokeProcessIncomingFrame(service, BuildFragmentFrame((ulong)(10_000 + i), total: 2, index: 0, payload: new byte[] { 0xAA }), sender);
        }

        Assert.Equal(1, (int)GetField(service, "fragmentSweepRunCount"));
        Assert.Equal(2000, ((IDictionary)GetField(service, "fragmentBuffers")).Count);

        now = now.AddSeconds(6);
        InvokeProcessIncomingFrame(service, BuildFragmentFrame(99_999UL, total: 2, index: 0, payload: new byte[] { 0xBB }), sender);

        Assert.Equal(2, (int)GetField(service, "fragmentSweepRunCount"));
    }

    [Fact]
    public void FragmentReassembly_OutOfOrder_UnderLoad_CleansUpAllBuffers()
    {
        var service = new SteamNetworkingService();
        var now = DateTime.UtcNow;

        SetField(service, "fragmentUtcNow", (Func<DateTime>)(() => now));
        SetField(service, "fragmentSweepInterval", TimeSpan.FromSeconds(1));
        SetField(service, "nextFragmentSweepUtc", DateTime.MinValue);

        var sender = new CSteamID(77UL);
        for (var i = 0; i < 1500; i++)
        {
            var msgId = (ulong)(500_000 + i);
            InvokeProcessIncomingFrame(service, BuildFragmentFrame(msgId, total: 2, index: 1, payload: new byte[] { (byte)(i & 0xFF), 0x5A }), sender);
            InvokeProcessIncomingFrame(service, BuildFragmentFrame(msgId, total: 2, index: 0, payload: new byte[] { 0x01, 0x02, 0x03 }), sender);
            now = now.AddMilliseconds(1);
        }

        Assert.Equal(0, ((IDictionary)GetField(service, "fragmentBuffers")).Count);
    }

    static void InvokeProcessIncomingFrame(SteamNetworkingService service, byte[] frame, CSteamID sender)
        => ProcessIncomingFrameMethod.Invoke(service, new object[] { frame, sender });

    static byte[] BuildFragmentFrame(ulong messageId, int total, int index, byte[] payload)
    {
        var frame = new byte[1 + 8 + 8 + 4 + 4 + payload.Length];
        frame[0] = 0;
        WriteU64(frame, 1, messageId);
        WriteU64(frame, 9, 0UL);
        WriteI32(frame, 17, total);
        WriteI32(frame, 21, index);
        Buffer.BlockCopy(payload, 0, frame, 25, payload.Length);
        return frame;
    }

    static void WriteU64(byte[] bytes, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (8 * i));
    }

    static void WriteI32(byte[] bytes, int offset, int value)
    {
        unchecked
        {
            bytes[offset + 0] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }
    }

    static void SetField(object target, string fieldName, object value)
        => target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    static object GetField(object target, string fieldName)
        => target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
