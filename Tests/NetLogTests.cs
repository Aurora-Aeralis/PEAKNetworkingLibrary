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
    public void TryEnterCooldown_ClearsSuppressedErrorCount_WhenWindowElapsed()
    {
        var now = 75d;
        NetLog.TimeProvider = () => now;

        NetLog.ErrorThrottled("test", "shared-key", 60d, "first");
        now += 1d;
        NetLog.ErrorThrottled("test", "shared-key", 60d, "second");
        Assert.Equal(1, GetSuppressedCount("shared-key"));

        now += 61d;
        Assert.True(NetLog.TryEnterCooldown("shared-key", 60d, now));
        Assert.False(ContainsSuppressedKey("shared-key"));
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

    [Fact]
    public void TryEnterCooldown_AllowsEntry_WhenClockMovesBackward()
    {
        Assert.True(NetLog.TryEnterCooldown("clock-key", 60d, 100d));
        Assert.True(NetLog.TryEnterCooldown("clock-key", 60d, 50d));
        Assert.False(NetLog.TryEnterCooldown("clock-key", 60d, 55d));
    }

    [Fact]
    public void TryEnterCooldown_CleansOldEpochEntries_AfterLargeClockReset()
    {
        NetLog.ErrorThrottled("test", "old-error-key", 60d, "first", () => 5000d);
        NetLog.ErrorThrottled("test", "old-error-key", 60d, "second", () => 5001d);

        Assert.True(ContainsThrottleKey("old-error-key"));
        Assert.True(ContainsSuppressedKey("old-error-key"));

        Assert.True(NetLog.TryEnterCooldown("cleanup-reset", 1d, 0d));

        Assert.False(ContainsThrottleKey("old-error-key"));
        Assert.False(ContainsSuppressedKey("old-error-key"));
    }

    [Fact]
    public void ErrorThrottled_ClockMovesBackward_LeavesCooldownAndClearsSuppression()
    {
        NetLog.ErrorThrottled("test", "clock-error-key", 60d, "first", () => 100d);
        NetLog.ErrorThrottled("test", "clock-error-key", 60d, "suppressed", () => 110d);

        Assert.Equal(1, GetSuppressedCount("clock-error-key"));

        NetLog.ErrorThrottled("test", "clock-error-key", 60d, "after clock reset", () => 50d);

        Assert.False(ContainsSuppressedKey("clock-error-key"));

        NetLog.ErrorThrottled("test", "clock-error-key", 60d, "suppressed after reset", () => 55d);

        Assert.Equal(1, GetSuppressedCount("clock-error-key"));
    }

    [Fact]
    public void ThrottledLogging_DoesNotThrow_WhenTimeProvidersThrow()
    {
        NetLog.TimeProvider = () => throw new InvalidOperationException("global clock failed");

        var exception = Record.Exception(() => NetLog.DebugThrottled("test", "throwing-clock", 60d, "message", () => throw new InvalidOperationException("local clock failed")));

        Assert.Null(exception);
        Assert.True(ContainsThrottleKey("throwing-clock"));
    }

    [Fact]
    public void GuardedFallback_ThrottlesRepeatedLoggerFailures()
    {
        var loggerField = typeof(Net).GetField("<Logger>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = loggerField.GetValue(null);
        var now = 200d;
        const string key = "fallback:logger-fallback:warning";
        NetLog.TimeProvider = () => now;

        try
        {
            loggerField.SetValue(null, null);

            NetLog.Warning("logger-fallback", "first");
            now += 0.5d;
            NetLog.Warning("logger-fallback", "second");

            Assert.Equal(1, GetFallbackSuppressedCount(key));

            now += 2.1d;
            NetLog.Warning("logger-fallback", "third");

            Assert.False(ContainsFallbackSuppressedKey(key));
        }
        finally
        {
            loggerField.SetValue(null, original);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TryEnterCooldown_UsesFallbackClock_WhenProvidedTimeIsNotFinite(double invalidNow)
    {
        var now = 100d;
        NetLog.TimeProvider = () => now;

        Assert.True(NetLog.TryEnterCooldown("invalid-time", 60d, invalidNow));

        now += 10d;
        Assert.False(NetLog.TryEnterCooldown("invalid-time", 60d, now));
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

    static bool ContainsFallbackSuppressedKey(string key)
    {
        var suppressed = GetPrivateDictionary("fallbackSuppressedByKey");
        return suppressed.Contains(key);
    }

    static int GetFallbackSuppressedCount(string key)
    {
        var suppressed = GetPrivateDictionary("fallbackSuppressedByKey");
        return suppressed.Contains(key) ? (int)(suppressed[key] ?? 0) : 0;
    }

    static IDictionary GetPrivateDictionary(string fieldName)
    {
        return (IDictionary)(typeof(NetLog).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)
            ?? throw new InvalidOperationException($"Missing field: {fieldName}"));
    }
}
