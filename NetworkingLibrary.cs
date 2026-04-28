using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

using NetworkingLibrary.Services;
using NetworkingLibrary.Modules;
using NetworkingLibrary.Features;
namespace NetworkingLibrary
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]

    public class Net : BaseUnityPlugin
    {
        public static Net Instance { get; private set; } = null!;
        internal new static ManualLogSource Logger { get; private set; } = null!;
        internal static Harmony? Harmony;

        public ConfigFile config = null!;
        public static INetworkingService? Service { get; private set; } 

        internal static Func<(INetworkingService service, DefaultServiceSelectionReason reason)> CreateDefaultNetworkingServiceWithReason = () =>
        {
            var service = NetworkingServiceFactory.CreateDefaultServiceWithReason(out var reason);
            return (service, reason);
        };
        internal static Func<INetworkingService> CreateOfflineNetworkingService = () => new OfflineNetworkingService();
        internal static Func<bool> IsApplicationPlaying = () => Application.isPlaying;
        internal new static Action<UnityEngine.Object> DestroyObject = UnityEngine.Object.Destroy;
        internal static Action<UnityEngine.Object> DestroyObjectImmediate = UnityEngine.Object.DestroyImmediate;
        internal static Func<float> RealtimeSinceStartupProvider = () => Time.realtimeSinceStartup;
        internal static Func<float, object?> WaitForSecondsRealtimeFactory = seconds => new WaitForSecondsRealtime(seconds);

        const string StartupRetryLogSource = "Net.StartupRetry";
        const string StartupRetryTransitionLogKey = "Net.StartupRetry.Transition";
        const float StartupRetryTransitionLogCooldownSeconds = 1.5f;
        const float StartupRetryWindowSeconds = 10f;
        const float StartupRetryInitialDelaySeconds = 0.5f;
        const float StartupRetryMaxDelaySeconds = 3f;
        const float StartupRetryBackoffMultiplier = 1.8f;

        private void OnDestroy()
        {
            try
            {
                Service?.Shutdown();
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Failed to shutdown networking service during plugin teardown: {ex.Message}");
            }
            finally
            {
                Service = null;
                ResetNetworkingStartupHooks();
                Message.ResetSerializersForTests();
            }

            try
            {
                var harmony = Harmony;
                if (harmony != null)
                {
                    harmony.UnpatchSelf();
                    Logger?.LogDebug("Removed Harmony patches during plugin teardown.");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Failed to unpatch Harmony during plugin teardown: {ex.Message}");
            }
            finally
            {
                Harmony = null;
                Instance = null!;
            }
        }

        private void Awake() {
            Logger = base.Logger;
            Instance = this;

            FileManager.InitializeConfig();

            try
            {
                Harmony ??= new Harmony(MyPluginInfo.PLUGIN_GUID);
                Harmony.PatchAll();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to apply Harmony patches: {ex}");
            }

            Service = null;
            TryInitializeNetworkingService(Logger, out var initializedService, out var serviceSelectionReason);
            Service = initializedService;

            if (serviceSelectionReason == DefaultServiceSelectionReason.SteamApiNotReady && Service is OfflineNetworkingService)
                StartCoroutine(RetrySteamInitializationForStartupWindow(StartupRetryWindowSeconds));

            var pollerName = $"{MyPluginInfo.PLUGIN_NAME}.Poller";
            var pollers = UnityEngine.Object.FindObjectsByType<NetworkingPoller>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderByDescending(poller => poller.isActiveAndEnabled)
                .ThenByDescending(poller => poller.gameObject.activeInHierarchy)
                .ThenBy(poller => poller.GetInstanceID())
                .ToList();
            var canonicalPoller = pollers.FirstOrDefault();
            if (canonicalPoller == null)
            {
                var createdPollerObject = new GameObject(pollerName);
                canonicalPoller = createdPollerObject.AddComponent<NetworkingPoller>();
                canonicalPoller.hideFlags = HideFlags.HideAndDontSave;
            }

            CleanupDuplicatePollers(canonicalPoller, pollers.Skip(1), DestroyPollerDuringStartup);

            var go = canonicalPoller.gameObject;
            go.name = pollerName;
            var foundExistingObject = pollers.Count > 0;
            if (foundExistingObject && go.scene.IsValid() && go.scene.name != "DontDestroyOnLoad")
                Logger.LogDebug($"Promoting existing poller object '{go.name}' from scene '{go.scene.name}' to DontDestroyOnLoad lifecycle.");
            DontDestroyOnLoad(go);

            Logger.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} has fully loaded!");
        }

        private static void CleanupDuplicatePollers(
            NetworkingPoller canonicalPoller,
            IEnumerable<NetworkingPoller> duplicatePollers,
            Action<UnityEngine.Object> destroyAction)
        {
            foreach (var extraPoller in duplicatePollers)
            {
                if (extraPoller == null)
                    continue;

                var duplicatePollerObject = extraPoller.gameObject;
                if (duplicatePollerObject == null)
                    continue;

                var hasOnlyTransformAndPoller = HasOnlyTransformAndPollerComponents(duplicatePollerObject);
                var destroyingObjectWouldDeleteCanonical = hasOnlyTransformAndPoller
                    && duplicatePollerObject.transform != null
                    && canonicalPoller.transform.IsChildOf(duplicatePollerObject.transform);
                destroyAction(hasOnlyTransformAndPoller && !destroyingObjectWouldDeleteCanonical
                    ? duplicatePollerObject
                    : extraPoller);
            }
        }

        private static bool HasOnlyTransformAndPollerComponents(GameObject pollerObject)
        {
            var components = pollerObject.GetComponents<Component>();
            var hasTransform = false;
            var hasPoller = false;
            var count = 0;
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;
                count++;
                if (component is Transform)
                {
                    hasTransform = true;
                    continue;
                }
                if (component is NetworkingPoller)
                {
                    hasPoller = true;
                    continue;
                }
                return false;
            }

            return count == 2 && hasTransform && hasPoller;
        }

        private static void DestroyPollerDuringStartup(UnityEngine.Object target)
        {
            if (target is NetworkingPoller duplicatePoller)
                duplicatePoller.enabled = false;
            else if (target is GameObject duplicatePollerObject)
                duplicatePollerObject.SetActive(false);

            if (IsApplicationPlaying()) DestroyObject(target);
            else DestroyObjectImmediate(target);
        }

        internal static bool TryInitializeNetworkingService(ManualLogSource? logger, out INetworkingService? service)
        {
            return TryInitializeNetworkingService(logger, out service, out _);
        }

        internal static bool TryInitializeNetworkingService(ManualLogSource? logger, out INetworkingService? service, out DefaultServiceSelectionReason defaultSelectionReason)
        {
            service = null;
            defaultSelectionReason = DefaultServiceSelectionReason.ProbeFailed;

            try
            {
                var defaultCreation = CreateDefaultNetworkingServiceWithReason();
                defaultSelectionReason = defaultCreation.reason;
                service = defaultCreation.service;
                service.Initialize();
                if (!service.IsInitialized)
                {
                    logger?.LogError($"Default networking service '{service.GetType().Name}' initialization returned without becoming active. Attempting OfflineNetworkingService fallback.");
                    try
                    {
                        service.Shutdown();
                    }
                    catch (Exception shutdownException)
                    {
                        logger?.LogWarning($"Failed to shutdown uninitialized default networking service '{service.GetType().Name}' during fallback: {shutdownException}");
                    }
                    service = null;
                    throw new InvalidOperationException("Default networking service initialization completed without becoming active.");
                }
                return true;
            }
            catch (Exception exception)
            {
                SafeShutdown(service, "failed default networking service initialization");
                service = null;
                logger?.LogError($"Failed to initialize default networking service. Attempting OfflineNetworkingService fallback. Exception: {exception}");
            }

            try
            {
                service = CreateOfflineNetworkingService();
                service.Initialize();
                if (!service.IsInitialized)
                {
                    logger?.LogError($"Fallback networking service '{service.GetType().Name}' initialization returned without becoming active. Networking service disabled.");
                    try
                    {
                        service.Shutdown();
                    }
                    catch (Exception shutdownException)
                    {
                        logger?.LogWarning($"Failed to shutdown uninitialized fallback networking service '{service.GetType().Name}': {shutdownException}");
                    }
                    service = null;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                SafeShutdown(service, "failed fallback OfflineNetworkingService initialization");
                logger?.LogError($"FATAL: Failed to initialize fallback OfflineNetworkingService. Networking service disabled. Exception: {exception}");
                service = null;
                return false;
            }
        }

        IEnumerator RetrySteamInitializationForStartupWindow(float retryWindowSeconds)
        {
            var delaySeconds = StartupRetryInitialDelaySeconds;
            var deadline = RealtimeSinceStartupProvider() + Mathf.Max(0.1f, retryWindowSeconds);

            while (RealtimeSinceStartupProvider() < deadline)
            {
                yield return WaitForSecondsRealtimeFactory(delaySeconds);

                var previousService = Service;
                if (previousService == null) yield break;

                INetworkingService? candidateService = null;
                DefaultServiceSelectionReason selectionReason;
                try
                {
                    var defaultCreation = CreateDefaultNetworkingServiceWithReason();
                    candidateService = defaultCreation.service;
                    selectionReason = defaultCreation.reason;
                }
                catch (Exception ex)
                {
                    LogStartupRetryTransitionThrottled($"Aborting startup retries: default service creation threw {ex.GetType().Name}: {ex.Message}");
                    yield break;
                }

                if (selectionReason == DefaultServiceSelectionReason.SteamReady)
                {
                    try
                    {
                        candidateService.Initialize();
                        if (!candidateService.IsInitialized)
                        {
                            LogStartupRetryTransitionThrottled("Steam readiness probe passed but Steam service did not initialize yet; continuing startup retries.");
                            SafeShutdown(candidateService, "startup retry steam candidate");
                            delaySeconds = Mathf.Min(StartupRetryMaxDelaySeconds, delaySeconds * StartupRetryBackoffMultiplier);
                            continue;
                        }

                        if (ReferenceEquals(Service, previousService))
                        {
                            if (previousService.InLobby)
                            {
                                LogStartupRetryTransitionThrottled("Skipping Steam promotion during startup retry because the active service is currently in a lobby.");
                                SafeShutdown(candidateService, "startup retry steam candidate while active lobby");
                                delaySeconds = Mathf.Min(StartupRetryMaxDelaySeconds, delaySeconds * StartupRetryBackoffMultiplier);
                                continue;
                            }

                            ReplaceService(previousService, candidateService, "Steam became ready during startup retry window.");
                        }
                        else
                        {
                            SafeShutdown(candidateService, "startup retry stale steam candidate");
                        }
                        yield break;
                    }
                    catch (Exception ex)
                    {
                        SafeShutdown(candidateService, "startup retry steam candidate after initialize exception");
                        LogStartupRetryTransitionThrottled($"Aborting startup retries: Steam service initialization threw {ex.GetType().Name}: {ex.Message}");
                        yield break;
                    }
                }

                SafeShutdown(candidateService, "startup retry discarded candidate");
                if (selectionReason == DefaultServiceSelectionReason.SteamApiNotReady)
                {
                    delaySeconds = Mathf.Min(StartupRetryMaxDelaySeconds, delaySeconds * StartupRetryBackoffMultiplier);
                    continue;
                }

                LogStartupRetryTransitionThrottled($"Aborting startup retries: default selection reason is {selectionReason}.");
                yield break;
            }

            LogStartupRetryTransitionThrottled("Startup retry window elapsed before Steam became ready.");
        }

        static void ReplaceService(INetworkingService previousService, INetworkingService nextService, string reason)
        {
            if (ReferenceEquals(previousService, nextService)) return;
            try
            {
                if (previousService is INetworkingServiceStateTransfer stateTransferSource)
                    stateTransferSource.CopyRuntimeStateTo(nextService);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Failed to migrate networking runtime state before service replacement: {ex.Message}");
                SafeShutdown(nextService, "startup retry failed replacement candidate");
                return;
            }
            SafeShutdown(previousService, "startup retry previous service");
            Service = nextService;
            LogStartupRetryTransitionThrottled($"Service transition completed: {previousService.GetType().Name} -> {nextService.GetType().Name}. Reason: {reason}");
        }

        static void SafeShutdown(INetworkingService? service, string context)
        {
            if (service == null) return;
            try
            {
                service.Shutdown();
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Failed to shutdown networking service during {context}: {ex.Message}");
            }
        }

        static void LogStartupRetryTransitionThrottled(string message)
        {
            NetLog.DebugThrottled(StartupRetryLogSource, StartupRetryTransitionLogKey, StartupRetryTransitionLogCooldownSeconds, message, () => RealtimeSinceStartupProvider(), includeOriginalMessageInFallback: true);
        }

        internal static void ResetNetworkingStartupHooks()
        {
            CreateDefaultNetworkingServiceWithReason = () =>
            {
                var service = NetworkingServiceFactory.CreateDefaultServiceWithReason(out var reason);
                return (service, reason);
            };
            CreateOfflineNetworkingService = () => new OfflineNetworkingService();
            IsApplicationPlaying = () => Application.isPlaying;
            DestroyObject = UnityEngine.Object.Destroy;
            DestroyObjectImmediate = UnityEngine.Object.DestroyImmediate;
            RealtimeSinceStartupProvider = () => Time.realtimeSinceStartup;
            WaitForSecondsRealtimeFactory = seconds => new WaitForSecondsRealtime(seconds);
        }
    }
}
