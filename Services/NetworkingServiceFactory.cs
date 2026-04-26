using System;
#if !UNITY_EDITOR
using Steamworks;
#endif
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public static class NetworkingServiceFactory
    {
        static void LogInfo(string message)
        {
            try { Net.Logger?.LogInfo(message); } catch { }
        }

        static void LogError(string message)
        {
            try { Net.Logger?.LogError(message); } catch { }
        }

#if !UNITY_EDITOR
        internal static Func<bool> IsSteamClientRunning = () => SteamAPI.IsSteamRunning();
        internal static Func<bool> IsSteamApiInitialized = ProbeSteamApiInitialized;
        internal static Func<INetworkingService> CreateSteamService = () => new SteamNetworkingService();
        internal static Func<INetworkingService> CreateOfflineService = () => new OfflineNetworkingService();
#endif

        public static INetworkingService CreateDefaultService()
        {
#if UNITY_EDITOR
            LogInfo("UNITY_EDITOR detected. Creating OfflineNetworkingService.");
            return new OfflineNetworkingService();
#else
            try
            {
                var isSteamClientRunning = IsSteamClientRunning();
                LogInfo($"Steam client running: {isSteamClientRunning}.");
                if (!isSteamClientRunning)
                {
                    LogInfo("Falling back to OfflineNetworkingService. Reason: Steam client is not running.");
                    return CreateOfflineService();
                }

                var isSteamApiInitialized = IsSteamApiInitialized();
                LogInfo($"Steam API initialized: {isSteamApiInitialized}.");
                if (isSteamApiInitialized)
                {
                    LogInfo("Steam ready. Creating SteamNetworkingService.");
                    return CreateSteamService();
                }

                LogInfo("Falling back to OfflineNetworkingService. Reason: Steam API is not initialized.");
            }
            catch (Exception exception)
            {
                LogError($"Steam readiness probe failed. Falling back to OfflineNetworkingService. Exception: {exception}");
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
                var candidateTypeNames = new[]
                {
                    "pworld.Scripts.SteamManager, Assembly-CSharp",
                    "SteamManager, Assembly-CSharp",
                    "SteamManager"
                };
                foreach (var candidateTypeName in candidateTypeNames)
                {
                    var steamManagerType = Type.GetType(candidateTypeName);
                    if (steamManagerType == null) continue;
                    if (TryReadInitializedFromType(steamManagerType, out isInitialized)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        static bool TryReadInitializedFromType(Type steamManagerType, out bool isInitialized)
        {
            isInitialized = false;
            const System.Reflection.BindingFlags StaticAnyVisibility = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var initializedProperty = steamManagerType.GetProperty("Initialized", StaticAnyVisibility);
            if (initializedProperty?.PropertyType == typeof(bool))
            {
                isInitialized = (bool)(initializedProperty.GetValue(null) ?? false);
                return true;
            }

            var initializedField = steamManagerType.GetField("Initialized", StaticAnyVisibility);
            if (initializedField?.FieldType == typeof(bool))
            {
                isInitialized = (bool)(initializedField.GetValue(null) ?? false);
                return true;
            }

            return false;
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
