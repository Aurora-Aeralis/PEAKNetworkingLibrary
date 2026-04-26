using NetworkingLibrary.Modules;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace NetworkingLibrary.Tests;

public class NetLogTests : IDisposable
{
    readonly Func<double> originalTimeProvider = NetLog.TimeProvider;

    public void Dispose()
    {
        NetLog.TimeProvider = originalTimeProvider;
        NetLog.ResetForTests();
    }

    [Fact]
    public void ErrorThrottled_EvictsStaleThrottleAndSuppressedEntries()
    {
        var now = 100d;
        NetLog.TimeProvider = () => now;

        NetLog.ErrorThrottled("test", "stale-key", 60d, "first");
        now += 1d;
        NetLog.ErrorThrottled("test", "stale-key", 60d, "second");
        Assert.Equal(1, GetSuppressedCount("stale-key"));

        now += 1300d;
        NetLog.TryEnterCooldown("cleanup-trigger", 1d, now);

        Assert.False(ContainsThrottleKey("stale-key"));
        Assert.False(ContainsSuppressedKey("stale-key"));
    }

    [Fact]
    public void ErrorThrottled_TracksSuppressionWithinCooldownWindow()
    {
        var now = 50d;
        NetLog.TimeProvider = () => now;

        NetLog.ErrorThrottled("test", "cooldown-key", 120d, "first");
        now += 10d;
        NetLog.ErrorThrottled("test", "cooldown-key", 120d, "second");

        Assert.Equal(1, GetSuppressedCount("cooldown-key"));
        Assert.True(ContainsThrottleKey("cooldown-key"));
    }

    [Fact]
    public void TryEnterCooldown_DoesNotEvictActivelyUsedKeys()
    {
        var now = 0d;
        NetLog.TimeProvider = () => now;

        Assert.True(NetLog.TryEnterCooldown("active-key", 60d, now));

        for (var i = 0; i < 5; i++)
        {
            now += 600d;
            Assert.True(NetLog.TryEnterCooldown("active-key", 60d, now));
            NetLog.TryEnterCooldown($"cleanup-{i}", 1d, now);
        }

        now += 601d;
        NetLog.TryEnterCooldown("cleanup-final", 1d, now);

        Assert.True(ContainsThrottleKey("active-key"));
    }

    static bool ContainsThrottleKey(string key)
    {
        var throttle = GetPrivateDictionary("throttleByKey");
        return throttle.Contains(key);
    }

    static bool ContainsSuppressedKey(string key)
    {
        var suppressed = GetPrivateDictionary("suppressedByKey");
        return suppressed.Contains(key);
    }

    static int GetSuppressedCount(string key)
    {
        var suppressed = GetPrivateDictionary("suppressedByKey");
        return suppressed.Contains(key) ? (int)(suppressed[key] ?? 0) : 0;
    }

    static IDictionary GetPrivateDictionary(string fieldName)
    {
        return (IDictionary)(typeof(NetLog).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)
            ?? throw new InvalidOperationException($"Missing field: {fieldName}"));
    }
}
