using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Steamworks;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamNetworkingServiceQueueDepthTests
{
    [Fact]
    public void EnqueueOrSend_NormalQueueAtCapacity_DropsOldest()
    {
        var service = new SteamNetworkingService();
        var maxDepth = GetMaxDepth("MAX_NORMAL_QUEUE_DEPTH");

        for (var i = 0; i < maxDepth + 1; i++)
        {
            InvokeEnqueueOrSend(service, new byte[] { (byte)i }, (ulong)(1000 + i), ReliableType.Unreliable, "Normal");
        }

        var payloads = GetQueuePayloads(service, "normalQueue");
        Assert.Equal(maxDepth, payloads.Length);
        Assert.Equal((byte)1, payloads.First());
        Assert.Equal((byte)maxDepth, payloads.Last());
    }

    [Fact]
    public void EnqueueOrSend_LowQueueAtCapacity_DropsOldest()
    {
        var service = new SteamNetworkingService();
        var maxDepth = GetMaxDepth("MAX_LOW_QUEUE_DEPTH");

        for (var i = 0; i < maxDepth + 1; i++)
        {
            InvokeEnqueueOrSend(service, new byte[] { (byte)i }, (ulong)(3000 + i), ReliableType.Unreliable, "Low");
        }

        var payloads = GetQueuePayloads(service, "lowQueue");
        Assert.Equal(maxDepth, payloads.Length);
        Assert.Equal((byte)1, payloads.First());
        Assert.Equal((byte)maxDepth, payloads.Last());
    }

    [Fact]
    public void EnqueueOrSend_HighPriority_DoesNotQueue()
    {
        var service = new SteamNetworkingService();

        InvokeEnqueueOrSend(service, new byte[] { 42 }, 98765UL, ReliableType.Unreliable, "High");

        Assert.Equal(0, GetQueueCount(service, "normalQueue"));
        Assert.Equal(0, GetQueueCount(service, "lowQueue"));
    }

    [Fact]
    public void EnqueueOrSend_OverflowWarning_IsThrottledByKey()
    {
        NetLog.ResetForTests();
        try
        {
            var service = new SteamNetworkingService();
            var maxDepth = GetMaxDepth("MAX_NORMAL_QUEUE_DEPTH");

            SetTimeProvider(100);
            for (var i = 0; i < maxDepth + 2; i++)
            {
                InvokeEnqueueOrSend(service, new byte[] { (byte)i }, (ulong)(5000 + i), ReliableType.Unreliable, "Normal");
            }

            Assert.False(NetLog.TryEnterCooldown("EnqueueOrSend.Overflow.normalQueue", 2d, 100));

            SetTimeProvider(103);
            Assert.True(NetLog.TryEnterCooldown("EnqueueOrSend.Overflow.normalQueue", 2d, 103));
        }
        finally
        {
            NetLog.ResetForTests();
        }
    }

    static void SetTimeProvider(double now)
    {
        NetLog.TimeProvider = () => now;
    }

    static int GetMaxDepth(string fieldName)
    {
        return (int)typeof(SteamNetworkingService).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    }

    static void InvokeEnqueueOrSend(SteamNetworkingService service, byte[] framed, ulong target, ReliableType reliable, string priorityName)
    {
        var priorityType = typeof(SteamNetworkingService).GetNestedType("Priority", BindingFlags.NonPublic)!;
        var priority = Enum.Parse(priorityType, priorityName);
        typeof(SteamNetworkingService)
            .GetMethod("EnqueueOrSend", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object[] { framed, new CSteamID(target), reliable, priority });
    }

    static int GetQueueCount(SteamNetworkingService service, string queueFieldName)
    {
        var queue = (ICollection)typeof(SteamNetworkingService).GetField(queueFieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        return queue.Count;
    }

    static byte[] GetQueuePayloads(SteamNetworkingService service, string queueFieldName)
    {
        var queue = (IEnumerable)typeof(SteamNetworkingService).GetField(queueFieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var queuedSendType = typeof(SteamNetworkingService).GetNestedType("QueuedSend", BindingFlags.NonPublic)!;
        var framedField = queuedSendType.GetField("Framed", BindingFlags.Instance | BindingFlags.Public)!;
        return queue.Cast<object>().Select(item => ((byte[])framedField.GetValue(item)!)[0]).ToArray();
    }
}
