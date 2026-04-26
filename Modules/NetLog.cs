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

        static readonly object throttleLock = new();
        static readonly Dictionary<string, ThrottleState> throttleByKey = new();
        static readonly Dictionary<string, int> suppressedByKey = new();
        static int cleanupCallCount;
        static double lastCleanupAt = double.NegativeInfinity;

        internal static Func<double> TimeProvider = () => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

        public static void Info(string source, string message) => Guarded(source, "info", message, () => Net.Logger?.LogInfo(message), false, true);
        public static void Debug(string source, string message, bool includeOriginalMessageInFallback = false) => Guarded(source, "debug", message, () => Net.Logger?.LogDebug(message), false, includeOriginalMessageInFallback);
        public static void Warning(string source, string message) => Guarded(source, "warning", message, () => Net.Logger?.LogWarning(message), false, true);
        public static void Error(string source, string message) => Guarded(source, "error", message, () => Net.Logger?.LogError(message), true, true);

        public static bool TryEnterCooldown(string key, double cooldownSeconds, double? now = null)
        {
            lock (throttleLock)
            {
                var currentTime = now ?? TimeProvider();
                MaybeCleanupStaleEntries(currentTime);

                if (throttleByKey.TryGetValue(key, out var state))
                {
                    var inCooldown = currentTime - state.LastAt < cooldownSeconds;
                    throttleByKey[key] = inCooldown
                        ? new ThrottleState(state.LastAt, currentTime)
                        : new ThrottleState(currentTime, currentTime);
                    return !inCooldown;
                }

                throttleByKey[key] = new ThrottleState(currentTime, currentTime);
                return true;
            }
        }

        public static void DebugThrottled(string source, string key, double cooldownSeconds, string message, Func<double>? timeProvider = null, bool includeOriginalMessageInFallback = false)
        {
            var now = timeProvider?.Invoke() ?? TimeProvider();
            if (!TryEnterCooldown(key, cooldownSeconds, now)) return;
            Debug(source, message, includeOriginalMessageInFallback);
        }

        public static void ErrorThrottled(string source, string key, double cooldownSeconds, string message, Func<double>? timeProvider = null)
        {
            var now = timeProvider?.Invoke() ?? TimeProvider();
            var suppressed = 0;
            lock (throttleLock)
            {
                MaybeCleanupStaleEntries(now);

                if (throttleByKey.TryGetValue(key, out var state) && now - state.LastAt < cooldownSeconds)
                {
                    throttleByKey[key] = new ThrottleState(state.LastAt, now);
                    suppressedByKey[key] = suppressedByKey.TryGetValue(key, out var currentSuppressed) ? currentSuppressed + 1 : 1;
                    return;
                }

                throttleByKey[key] = new ThrottleState(now, now);
                if (suppressedByKey.TryGetValue(key, out suppressed))
                {
                    suppressedByKey.Remove(key);
                }
            }

            var suffix = suppressed > 0 ? $" Suppressed {suppressed} similar exceptions." : string.Empty;
            Error(source, $"{message}{suffix}");
        }

        static void Guarded(string source, string level, string message, Action write, bool fallbackAsError, bool includeOriginalMessage)
        {
            try { write(); }
            catch (Exception ex)
            {
                var originalMessageSuffix = includeOriginalMessage ? $". Original message: {message}" : string.Empty;
                var fallback = $"[{source}] Failed to write {level} log. Exception: {ex.GetType().Name}: {ex.Message}{originalMessageSuffix}";
                if (fallbackAsError) Debug.LogError(fallback);
                else Debug.LogWarning(fallback);
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
            TimeProvider = () => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
        }

        static void MaybeCleanupStaleEntries(double now)
        {
            cleanupCallCount++;
            if (now - lastCleanupAt < CleanupMinIntervalSeconds && cleanupCallCount % CleanupCheckInterval != 0) return;
            if (now - lastCleanupAt < CleanupMinIntervalSeconds) return;

            lastCleanupAt = now;
            List<string>? staleKeys = null;
            foreach (var pair in throttleByKey)
            {
                if (now - pair.Value.LastSeenAt <= ThrottleRetentionSeconds) continue;
                staleKeys ??= new List<string>();
                staleKeys.Add(pair.Key);
            }

            if (staleKeys == null) return;
            foreach (var staleKey in staleKeys)
            {
                throttleByKey.Remove(staleKey);
                suppressedByKey.Remove(staleKey);
            }
        }
    }
}
