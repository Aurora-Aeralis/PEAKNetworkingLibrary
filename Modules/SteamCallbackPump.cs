using System;
using UnityEngine;
using Steamworks;
using NetworkingLibrary.Services;

namespace NetworkingLibrary.Modules
{
    public class SteamCallbackPump : MonoBehaviour
    {
        const string PumpObjectName = "SteamCallbackPump";

        public static bool CallbackPumpingEnabled { get; private set; }
        internal static Func<bool> IsSteamReady = ProbeSteamReady;

        string? lastSkipReason;
        bool runCallbacksFaulted;

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
                if (!string.Equals(pump.gameObject.name, PumpObjectName, StringComparison.Ordinal)) continue;

                if (Application.isPlaying) UnityEngine.Object.Destroy(pump.gameObject);
                else UnityEngine.Object.DestroyImmediate(pump.gameObject);
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
            catch
            {
                return false;
            }
#endif
        }

        static bool TryIsSteamReady(out bool isReady)
        {
            isReady = false;
            try
            {
                isReady = IsSteamReady();
                return true;
            }
            catch
            {
                return false;
            }
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
            try { NetworkingLibrary.Net.Logger.LogError(message); } catch { }
        }

        static void TryLogInfo(string message)
        {
            try { NetworkingLibrary.Net.Logger.LogInfo(message); } catch { }
        }
    }
}
