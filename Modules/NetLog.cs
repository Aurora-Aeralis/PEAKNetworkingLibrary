using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkingLibrary.Modules
{
    internal static class NetLog
    {
        readonly struct ThrottleState
        {
            internal readonly double LastAt;
            internal readonly double LastSeenAt;

            internal ThrottleState(double lastAt, double lastSeenAt)
            {
                LastAt = lastAt;
                LastSeenAt = lastSeenAt;
            }
        }

        const double ThrottleRetentionSeconds = 20d * 60d;
        const int CleanupCheckInterval = 64;
        const double CleanupMinIntervalSeconds = 30d;
        const double FallbackLogCooldownSeconds = 2d;

        static readonly object throttleLock = new();
        static readonly Dictionary<string, ThrottleState> throttleByKey = new();
        static readonly Dictionary<string, int> suppressedByKey = new();
        static int cleanupCallCount;
        static double lastCleanupAt = double.NegativeInfinity;
        static readonly object fallbackThrottleLock = new();
        static readonly Dictionary<string, ThrottleState> fallbackThrottleByKey = new();
        static readonly Dictionary<string, int> fallbackSuppressedByKey = new();
        static int fallbackCleanupCallCount;
        static double fallbackLastCleanupAt = double.NegativeInfinity;

        internal static Func<double> TimeProvider = () => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

        public static void Info(string source, string message) => Guarded(source, "info", message, WriteInfo, false, true);
        public static void Debug(string source, string message, bool includeOriginalMessageInFallback = false) => Guarded(source, "debug", message, WriteDebug, false, includeOriginalMessageInFallback);
        public static void Warning(string source, string message) => Guarded(source, "warning", message, WriteWarning, false, true);
        public static void Error(string source, string message) => Guarded(source, "error", message, WriteError, true, true);

        public static bool TryEnterCooldown(string key, double cooldownSeconds, double? now = null)
        {
            lock (throttleLock)
            {
                var currentTime = now.HasValue && IsFinite(now.Value) ? now.Value : ResolveTime(null);
                MaybeCleanupStaleEntries(currentTime, throttleByKey, suppressedByKey, ref cleanupCallCount, ref lastCleanupAt);

                if (throttleByKey.TryGetValue(key, out var state))
                {
                    var inCooldown = IsInCooldown(currentTime, state.LastAt, cooldownSeconds);
                    throttleByKey[key] = inCooldown
                        ? new ThrottleState(state.LastAt, currentTime)
                        : new ThrottleState(currentTime, currentTime);
                    if (!inCooldown) suppressedByKey.Remove(key);
                    return !inCooldown;
                }

                throttleByKey[key] = new ThrottleState(currentTime, currentTime);
                return true;
            }
        }

        public static void DebugThrottled(string source, string key, double cooldownSeconds, string message, Func<double>? timeProvider = null, bool includeOriginalMessageInFallback = false)
        {
            var now = ResolveTime(timeProvider);
            if (!TryEnterCooldown(key, cooldownSeconds, now)) return;
            Debug(source, message, includeOriginalMessageInFallback);
        }

        public static void ErrorThrottled(string source, string key, double cooldownSeconds, string message, Func<double>? timeProvider = null)
        {
            var now = ResolveTime(timeProvider);
            if (!TryEnterSuppressedCooldown(key, cooldownSeconds, now, out var suppressed)) return;

            var suffix = suppressed > 0 ? $" Suppressed {suppressed} similar exceptions." : string.Empty;
            Guarded(source, "error", $"{message}{suffix}", WriteError, true, true, now);
        }

        static bool TryEnterSuppressedCooldown(string key, double cooldownSeconds, double now, out int suppressed)
        {
            lock (throttleLock)
            {
                return TryEnterSuppressedCooldownUnderLock(key, cooldownSeconds, now, throttleByKey, suppressedByKey, ref cleanupCallCount, ref lastCleanupAt, out suppressed);
            }
        }

        static bool TryEnterFallbackCooldown(string key, double cooldownSeconds, double now, out int suppressed)
        {
            lock (fallbackThrottleLock)
            {
                return TryEnterSuppressedCooldownUnderLock(key, cooldownSeconds, now, fallbackThrottleByKey, fallbackSuppressedByKey, ref fallbackCleanupCallCount, ref fallbackLastCleanupAt, out suppressed);
            }
        }

        static bool TryEnterSuppressedCooldownUnderLock(
            string key,
            double cooldownSeconds,
            double now,
            Dictionary<string, ThrottleState> throttle,
            Dictionary<string, int> suppressedCounts,
            ref int localCleanupCallCount,
            ref double localLastCleanupAt,
            out int suppressed)
        {
            suppressed = 0;
            MaybeCleanupStaleEntries(now, throttle, suppressedCounts, ref localCleanupCallCount, ref localLastCleanupAt);

            if (throttle.TryGetValue(key, out var state) && IsInCooldown(now, state.LastAt, cooldownSeconds))
            {
                throttle[key] = new ThrottleState(state.LastAt, now);
                suppressedCounts[key] = suppressedCounts.TryGetValue(key, out var currentSuppressed) ? currentSuppressed + 1 : 1;
                return false;
            }

            throttle[key] = new ThrottleState(now, now);
            if (suppressedCounts.TryGetValue(key, out suppressed)) suppressedCounts.Remove(key);
            return true;
        }

        static double ResolveTime(Func<double>? timeProvider)
        {
            if (timeProvider != null && TryGetFiniteTime(timeProvider, out var time)) return time;
            if (TryGetFiniteTime(TimeProvider, out time)) return time;

            return DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
        }

        static bool TryGetFiniteTime(Func<double> timeProvider, out double time)
        {
            try
            {
                time = timeProvider();
                return IsFinite(time);
            }
            catch
            {
                time = 0d;
                return false;
            }
        }

        static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool IsInCooldown(double currentTime, double lastAt, double cooldownSeconds) => currentTime >= lastAt && currentTime - lastAt < cooldownSeconds;

        static void WriteInfo(string message) => Net.Logger.LogInfo(message);
        static void WriteDebug(string message) => Net.Logger.LogDebug(message);
        static void WriteWarning(string message) => Net.Logger.LogWarning(message);
        static void WriteError(string message) => Net.Logger.LogError(message);

        static void Guarded(string source, string level, string message, Action<string> write, bool fallbackAsError, bool includeOriginalMessage, double? fallbackTime = null)
        {
            try
            {
                write(message);
                return;
            }
            catch (Exception ex)
            {
                var originalMessageSuffix = includeOriginalMessage ? $". Original message: {message}" : string.Empty;
                var fallback = $"[{source}] Failed to write {level} log. Exception: {ex.GetType().Name}: {ex.Message}{originalMessageSuffix}";
                var now = fallbackTime.HasValue && IsFinite(fallbackTime.Value) ? fallbackTime.Value : ResolveTime(null);
                if (!TryEnterFallbackCooldown($"fallback:{source}:{level}", FallbackLogCooldownSeconds, now, out var suppressed)) return;
                if (suppressed > 0) fallback += $" Suppressed {suppressed} similar fallback logs.";
                try
                {
                    if (fallbackAsError) UnityEngine.Debug.LogError(fallback);
                    else UnityEngine.Debug.LogWarning(fallback);
                }
                catch
                {
                    if (fallbackAsError) System.Diagnostics.Trace.TraceError(fallback);
                    else System.Diagnostics.Trace.TraceWarning(fallback);
                }
            }
        }

        internal static void ResetForTests()
        {
            lock (throttleLock)
            {
                throttleByKey.Clear();
                suppressedByKey.Clear();
                cleanupCallCount = 0;
                lastCleanupAt = double.NegativeInfinity;
            }
            lock (fallbackThrottleLock)
            {
                fallbackThrottleByKey.Clear();
                fallbackSuppressedByKey.Clear();
                fallbackCleanupCallCount = 0;
                fallbackLastCleanupAt = double.NegativeInfinity;
            }
            TimeProvider = () => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
        }

        static void MaybeCleanupStaleEntries(
            double now,
            Dictionary<string, ThrottleState> throttle,
            Dictionary<string, int> suppressedCounts,
            ref int localCleanupCallCount,
            ref double localLastCleanupAt)
        {
            localCleanupCallCount++;
            if (now >= localLastCleanupAt)
            {
                if (now - localLastCleanupAt < CleanupMinIntervalSeconds && localCleanupCallCount % CleanupCheckInterval != 0) return;
                if (now - localLastCleanupAt < CleanupMinIntervalSeconds) return;
            }

            localLastCleanupAt = now;
            List<string>? staleKeys = null;
            foreach (var pair in throttle)
            {
                var age = now >= pair.Value.LastSeenAt ? now - pair.Value.LastSeenAt : pair.Value.LastSeenAt - now;
                if (age <= ThrottleRetentionSeconds) continue;
                staleKeys ??= new List<string>();
                staleKeys.Add(pair.Key);
            }

            if (staleKeys == null) return;
            foreach (var staleKey in staleKeys)
            {
                throttle.Remove(staleKey);
                suppressedCounts.Remove(staleKey);
            }
        }
    }
}
