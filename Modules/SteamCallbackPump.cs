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
            var existingPump = GameObject.Find(PumpObjectName);
            if (existingPump == null) return;
            UnityEngine.Object.Destroy(existingPump);
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
            try
            {
                return NetworkingServiceFactory.IsSteamClientRunning() && NetworkingServiceFactory.IsSteamApiInitialized();
            }
            catch
            {
                return false;
            }
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
