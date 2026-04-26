using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkingLibrary.Modules
{
    internal static class NetLog
    {
        static readonly object throttleLock = new();
        static readonly Dictionary<string, double> throttleByKey = new();

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
                if (throttleByKey.TryGetValue(key, out var lastAt) && currentTime - lastAt < cooldownSeconds) return false;
                throttleByKey[key] = currentTime;
                return true;
            }
        }

        public static void DebugThrottled(string source, string key, double cooldownSeconds, string message, Func<double>? timeProvider = null, bool includeOriginalMessageInFallback = false)
        {
            var now = timeProvider?.Invoke() ?? TimeProvider();
            if (!TryEnterCooldown(key, cooldownSeconds, now)) return;
            Debug(source, message, includeOriginalMessageInFallback);
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
            lock (throttleLock) throttleByKey.Clear();
            TimeProvider = () => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
        }
    }
}
