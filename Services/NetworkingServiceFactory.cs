using System;
using System.Reflection;
using NetworkingLibrary.Modules;
#if !UNITY_EDITOR
using Steamworks;
#endif
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public enum DefaultServiceSelectionReason
    {
        SteamReady,
        SteamClientNotRunning,
        SteamApiNotReady,
        ProbeFailed,
        UnityEditorOffline
    }

    public static class NetworkingServiceFactory
    {
        const string LogSource = "NetworkingServiceFactory";
        const float ProbeDebugLogCooldownSeconds = 2f;
        static readonly string[] steamManagerTypeCandidates =
        {
            "pworld.Scripts.SteamManager, Assembly-CSharp",
            "SteamManager, Assembly-CSharp",
            "SteamManager"
        };

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
            return CreateDefaultServiceWithReason(out _);
        }

        internal static INetworkingService CreateDefaultServiceWithReason(out DefaultServiceSelectionReason reason)
        {
#if UNITY_EDITOR
            reason = DefaultServiceSelectionReason.UnityEditorOffline;
            NetLog.Info(LogSource, "UNITY_EDITOR detected. Creating OfflineNetworkingService.");
            return new OfflineNetworkingService();
#else
            try
            {
                var isSteamClientRunning = IsSteamClientRunning();
                NetLog.Info(LogSource, $"Steam client running: {isSteamClientRunning}.");
                if (!isSteamClientRunning)
                {
                    reason = DefaultServiceSelectionReason.SteamClientNotRunning;
                    NetLog.Info(LogSource, "Falling back to OfflineNetworkingService. Reason: Steam client is not running.");
                    return CreateOfflineService();
                }

                var isSteamApiInitialized = IsSteamApiInitialized();
                NetLog.Info(LogSource, $"Steam API initialized: {isSteamApiInitialized}.");
                if (isSteamApiInitialized)
                {
                    reason = DefaultServiceSelectionReason.SteamReady;
                    NetLog.Info(LogSource, "Steam ready. Creating SteamNetworkingService.");
                    return CreateSteamService();
                }

                reason = DefaultServiceSelectionReason.SteamApiNotReady;
                NetLog.Info(LogSource, "Falling back to OfflineNetworkingService. Reason: Steam API is not initialized.");
            }
            catch (Exception exception)
            {
                reason = DefaultServiceSelectionReason.ProbeFailed;
                NetLog.Error(LogSource, $"Steam readiness probe failed. Falling back to OfflineNetworkingService. Exception: {exception}");
                return CreateOfflineService();
            }

            reason = DefaultServiceSelectionReason.SteamApiNotReady;
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
                foreach (var candidateTypeName in steamManagerTypeCandidates)
                {
                    var steamManagerType = ResolveType(candidateTypeName);
                    if (steamManagerType == null) continue;
                    if (TryReadInitializedSafely(steamManagerType, out isInitialized)) return true;
                }

                var loadedSteamManagerType = ResolveLoadedSteamManagerType();
                if (loadedSteamManagerType != null && TryReadInitializedSafely(loadedSteamManagerType, out isInitialized))
                    return true;

                return false;
            }
            catch (Exception ex)
            {
                NetLog.DebugThrottled(LogSource, "NetworkingServiceFactory.TryReadSteamManagerInitialized", ProbeDebugLogCooldownSeconds, $"TryReadSteamManagerInitialized reflection probe failed: {ex.GetType().Name}: {ex.Message}", () => UnscaledTimeProvider(), includeOriginalMessageInFallback: true);
                return false;
            }
        }

        static bool TryReadInitializedSafely(Type steamManagerType, out bool isInitialized)
        {
            isInitialized = false;

            try
            {
                return TryReadInitializedFromType(steamManagerType, out isInitialized);
            }
            catch (Exception ex)
            {
                NetLog.DebugThrottled(LogSource, "NetworkingServiceFactory.TryReadInitializedSafely", ProbeDebugLogCooldownSeconds, $"TryReadInitializedFromType failed for '{steamManagerType.FullName ?? steamManagerType.Name}': {ex.GetType().Name}: {ex.Message}", () => UnscaledTimeProvider(), includeOriginalMessageInFallback: true);
                return false;
            }
        }

        static Type? ResolveLoadedSteamManagerType()
        {
            const BindingFlags AnyStaticVisibility = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type? fallbackCandidate = null;
            var hasAmbiguousFallbackCandidates = false;
            for (var index = 0; index < assemblies.Length; index++)
            {
                var assembly = assemblies[index];
                if (assembly == null || assembly.IsDynamic) continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = GetLoadableTypes(ex);
                }
                catch
                {
                    continue;
                }

                for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    var candidate = types[typeIndex];
                    if (candidate == null || !string.Equals(candidate.Name, "SteamManager", StringComparison.Ordinal)) continue;
                    if (candidate.GetProperty("Initialized", AnyStaticVisibility)?.PropertyType != typeof(bool)
                        && candidate.GetField("Initialized", AnyStaticVisibility)?.FieldType != typeof(bool))
                        continue;

                    if (IsPreferredSteamManagerType(candidate))
                        return candidate;

                    if (fallbackCandidate == null)
                    {
                        fallbackCandidate = candidate;
                        continue;
                    }

                    if (!ReferenceEquals(fallbackCandidate, candidate))
                        hasAmbiguousFallbackCandidates = true;

                }
            }

            return hasAmbiguousFallbackCandidates ? null : fallbackCandidate;
        }

        static Type[] GetLoadableTypes(ReflectionTypeLoadException exception)
        {
            var source = exception.Types;
            var count = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] != null) count++;
            }

            if (count == 0) return Array.Empty<Type>();
            var loadable = new Type[count];
            var index = 0;
            for (var i = 0; i < source.Length; i++)
            {
                var type = source[i];
                if (type == null) continue;
                loadable[index++] = type;
            }
            return loadable;
        }

        static bool IsPreferredSteamManagerType(Type steamManagerType)
        {
            var fullName = steamManagerType.FullName;
            if (string.Equals(fullName, "pworld.Scripts.SteamManager", StringComparison.Ordinal))
                return true;

            if (string.Equals(fullName, "SteamManager", StringComparison.Ordinal))
                return true;

            var assemblyName = steamManagerType.Assembly.GetName().Name;
            return string.Equals(assemblyName, "Assembly-CSharp", StringComparison.Ordinal);
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
