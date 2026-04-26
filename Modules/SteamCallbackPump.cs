using System;
using UnityEngine;
using Steamworks;
using NetworkingLibrary.Services;

namespace NetworkingLibrary.Modules
{
    public class SteamCallbackPump : MonoBehaviour
    {
        const float ErrorLogCooldownSeconds = 2f;

        public static bool CallbackPumpingEnabled { get; private set; }
        internal static Func<bool> IsSteamReady = ProbeSteamReady;
        internal static Func<float> TimeProvider = () => Time.unscaledTime;

        string? lastSkipReason;
        bool runCallbacksFaulted;
        static float probeSteamReadyLastErrorLogTime = float.NegativeInfinity;
        static bool probeSteamReadySuppressedFault;
        float isSteamReadyHookLastErrorLogTime = float.NegativeInfinity;
        bool isSteamReadyHookSuppressedFault;

        public static void EnablePumping() => CallbackPumpingEnabled = true;
        public static void DisablePumping() => CallbackPumpingEnabled = false;

        public static void DisableAndDestroyExisting()
        {
            DisablePumping();
            var pumps = UnityEngine.Object.FindObjectsOfType<SteamCallbackPump>(true);
            if (pumps == null || pumps.Length == 0) return;

            for (int i = 0; i < pumps.Length; i++)
            {
                var pump = pumps[i];
                if (pump == null || pump.gameObject == null) continue;

                bool canDestroyWholeObject = pump.gameObject.name == "SteamCallbackPump";
                if (canDestroyWholeObject)
                {
                    var components = pump.gameObject.GetComponents<Component>();
                    for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
                    {
                        var component = components[componentIndex];
                        if (component == null || component is Transform || component == pump) continue;
                        canDestroyWholeObject = false;
                        break;
                    }
                }

                if (canDestroyWholeObject)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(pump.gameObject);
                    else UnityEngine.Object.DestroyImmediate(pump.gameObject);
                    continue;
                }

                if (Application.isPlaying) UnityEngine.Object.Destroy(pump);
                else UnityEngine.Object.DestroyImmediate(pump);
            }
        }

        void Update()
        {
            if (!CallbackPumpingEnabled) return;

            if (!TryIsSteamReady(out var isReady) || !isReady)
            {
                LogSkipReasonOnce("Steam callbacks skipped because Steam is unavailable or uninitialized.");
                return;
            }

            lastSkipReason = null;

            try
            {
                SteamAPI.RunCallbacks();
                runCallbacksFaulted = false;
            }
            catch (Exception ex)
            {
                if (runCallbacksFaulted) return;
                runCallbacksFaulted = true;
                TryLogError($"SteamAPI.RunCallbacks error (logging once until recovery): {ex}");
            }
        }

        static bool ProbeSteamReady()
        {
#if UNITY_EDITOR
            return false;
#else
            try
            {
                return NetworkingServiceFactory.IsSteamClientRunning() && NetworkingServiceFactory.IsSteamApiInitialized();
            }
            catch (Exception ex)
            {
                LogExceptionWithCooldownStatic(
                    ref probeSteamReadyLastErrorLogTime,
                    ref probeSteamReadySuppressedFault,
                    $"ProbeSteamReady failed while checking Steam readiness. Exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
#endif
        }

        bool TryIsSteamReady(out bool isReady)
        {
            isReady = false;
            try
            {
                isReady = IsSteamReady();
                return true;
            }
            catch (Exception ex)
            {
                LogExceptionWithCooldown(
                    ref isSteamReadyHookLastErrorLogTime,
                    ref isSteamReadyHookSuppressedFault,
                    $"TryIsSteamReady failed while invoking IsSteamReady hook. Exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        void LogExceptionWithCooldown(ref float lastErrorLogTime, ref bool suppressedFault, string message)
        {
            var now = TimeProvider();
            if (now - lastErrorLogTime >= ErrorLogCooldownSeconds)
            {
                lastErrorLogTime = now;
                suppressedFault = false;
                TryLogError(message);
                return;
            }

            suppressedFault = true;
        }

        static void LogExceptionWithCooldownStatic(ref float lastErrorLogTime, ref bool suppressedFault, string message)
        {
            var now = TimeProvider();
            if (now - lastErrorLogTime >= ErrorLogCooldownSeconds)
            {
                lastErrorLogTime = now;
                suppressedFault = false;
                TryLogError(message);
                return;
            }

            suppressedFault = true;
        }

        void LogSkipReasonOnce(string message)
        {
            if (lastSkipReason == message) return;
            lastSkipReason = message;
            runCallbacksFaulted = false;
            TryLogInfo(message);
        }

        static void TryLogError(string message)
        {
            try { NetworkingLibrary.Net.Logger.LogError(message); }
            catch (Exception ex)
            {
                Debug.LogError($"[SteamCallbackPump] Failed to write error log. Exception: {ex.GetType().Name}: {ex.Message}. Original message: {message}");
            }
        }

        static void TryLogInfo(string message)
        {
            try { NetworkingLibrary.Net.Logger.LogInfo(message); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SteamCallbackPump] Failed to write info log. Exception: {ex.GetType().Name}: {ex.Message}. Original message: {message}");
            }
        }
    }
}
