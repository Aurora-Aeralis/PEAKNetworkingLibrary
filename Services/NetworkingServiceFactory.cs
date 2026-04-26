using System;
using NetworkingLibrary.Modules;
#if !UNITY_EDITOR
using Steamworks;
#endif
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public static class NetworkingServiceFactory
    {
        const string LogSource = "NetworkingServiceFactory";
        const float ProbeDebugLogCooldownSeconds = 2f;

#if !UNITY_EDITOR
        internal static Func<bool> IsSteamClientRunning = () => SteamAPI.IsSteamRunning();
        internal static Func<bool> IsSteamApiInitialized = ProbeSteamApiInitialized;
        internal static Func<INetworkingService> CreateSteamService = () => new SteamNetworkingService();
        internal static Func<INetworkingService> CreateOfflineService = () => new OfflineNetworkingService();
        internal static Func<string, Type?> ResolveType = Type.GetType;
        internal static Func<float> UnscaledTimeProvider = () => Time.unscaledTime;
#endif

        public static INetworkingService CreateDefaultService()
        {
#if UNITY_EDITOR
            NetLog.Info(LogSource, "UNITY_EDITOR detected. Creating OfflineNetworkingService.");
            return new OfflineNetworkingService();
#else
            try
            {
                var isSteamClientRunning = IsSteamClientRunning();
                NetLog.Info(LogSource, $"Steam client running: {isSteamClientRunning}.");
                if (!isSteamClientRunning)
                {
                    NetLog.Info(LogSource, "Falling back to OfflineNetworkingService. Reason: Steam client is not running.");
                    return CreateOfflineService();
                }

                var isSteamApiInitialized = IsSteamApiInitialized();
                NetLog.Info(LogSource, $"Steam API initialized: {isSteamApiInitialized}.");
                if (isSteamApiInitialized)
                {
                    NetLog.Info(LogSource, "Steam ready. Creating SteamNetworkingService.");
                    return CreateSteamService();
                }

                NetLog.Info(LogSource, "Falling back to OfflineNetworkingService. Reason: Steam API is not initialized.");
            }
            catch (Exception exception)
            {
                NetLog.Error(LogSource, $"Steam readiness probe failed. Falling back to OfflineNetworkingService. Exception: {exception}");
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
            catch (Exception ex)
            {
                NetLog.DebugThrottled(LogSource, "NetworkingServiceFactory.ProbeSteamApiInitialized", ProbeDebugLogCooldownSeconds, $"ProbeSteamApiInitialized fallback SteamUser.GetSteamID failed: {ex.GetType().Name}: {ex.Message}", () => UnscaledTimeProvider(), includeOriginalMessageInFallback: true);
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
                    var steamManagerType = ResolveType(candidateTypeName);
                    if (steamManagerType == null) continue;
                    if (TryReadInitializedFromType(steamManagerType, out isInitialized)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                NetLog.DebugThrottled(LogSource, "NetworkingServiceFactory.TryReadSteamManagerInitialized", ProbeDebugLogCooldownSeconds, $"TryReadSteamManagerInitialized reflection probe failed: {ex.GetType().Name}: {ex.Message}", () => UnscaledTimeProvider(), includeOriginalMessageInFallback: true);
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
            ResolveType = Type.GetType;
            UnscaledTimeProvider = () => Time.unscaledTime;
        }
#endif
    }
}
