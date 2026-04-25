using System;
#if !UNITY_EDITOR
using Steamworks;
#endif
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public static class NetworkingServiceFactory
    {
#if !UNITY_EDITOR
        internal static Func<bool> IsSteamClientRunning = () => SteamAPI.IsSteamRunning();
        internal static Func<bool> IsSteamApiInitialized = ProbeSteamApiInitialized;
        internal static Func<INetworkingService> CreateSteamService = () => new SteamNetworkingService();
        internal static Func<INetworkingService> CreateOfflineService = () => new OfflineNetworkingService();
#endif

        public static INetworkingService CreateDefaultService()
        {
#if UNITY_EDITOR
            Net.Logger.LogInfo("UNITY_EDITOR detected. Creating OfflineNetworkingService.");
            return new OfflineNetworkingService();
#else
            try
            {
                var isSteamClientRunning = IsSteamClientRunning();
                Net.Logger.LogInfo($"Steam client running: {isSteamClientRunning}.");
                if (!isSteamClientRunning)
                {
                    Net.Logger.LogInfo("Falling back to OfflineNetworkingService. Reason: Steam client is not running.");
                    return CreateOfflineService();
                }

                var isSteamApiInitialized = IsSteamApiInitialized();
                Net.Logger.LogInfo($"Steam API initialized: {isSteamApiInitialized}.");
                if (isSteamApiInitialized)
                {
                    Net.Logger.LogInfo("Steam ready. Creating SteamNetworkingService.");
                    return CreateSteamService();
                }

                Net.Logger.LogInfo("Falling back to OfflineNetworkingService. Reason: Steam API is not initialized.");
            }
            catch (Exception exception)
            {
                Net.Logger.LogError($"Steam readiness probe failed. Falling back to OfflineNetworkingService. Exception: {exception}");
            }

            return CreateOfflineService();
#endif
        }

#if !UNITY_EDITOR
        static bool ProbeSteamApiInitialized()
        {
            if (TryReadSteamManagerInitialized(out var isInitialized))
                return isInitialized;

            try
            {
                return SteamUser.GetSteamID() != CSteamID.Nil;
            }
            catch
            {
                return false;
            }
        }

        static bool TryReadSteamManagerInitialized(out bool isInitialized)
        {
            isInitialized = false;

            try
            {
                var steamManagerType = Type.GetType("pworld.Scripts.SteamManager, Assembly-CSharp")
                    ?? Type.GetType("SteamManager, Assembly-CSharp")
                    ?? Type.GetType("SteamManager");
                if (steamManagerType == null)
                    return false;

                var initializedProperty = steamManagerType.GetProperty("Initialized", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (initializedProperty?.PropertyType != typeof(bool))
                    return false;

                isInitialized = (bool)(initializedProperty.GetValue(null) ?? false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ResetTestHooks()
        {
            IsSteamClientRunning = () => SteamAPI.IsSteamRunning();
            IsSteamApiInitialized = ProbeSteamApiInitialized;
            CreateSteamService = () => new SteamNetworkingService();
            CreateOfflineService = () => new OfflineNetworkingService();
        }
#endif
    }
}
