using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Steamworks;
using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamNetworkingServiceLifecycleTests
{
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

    static void SetInLobby(SteamNetworkingService service, bool value)
    {
        typeof(SteamNetworkingService).GetField("<InLobby>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
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
}
