using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
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
        internal static Harmony? Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        public ConfigFile config = null!;
        public static INetworkingService? Service { get; private set; } 

        internal static Func<INetworkingService> CreateDefaultNetworkingService = NetworkingServiceFactory.CreateDefaultService;
        internal static Func<INetworkingService> CreateOfflineNetworkingService = () => new OfflineNetworkingService();
        internal static Func<bool> IsApplicationPlaying = () => Application.isPlaying;
        internal static Action<UnityEngine.Object> DestroyObject = UnityEngine.Object.Destroy;
        internal static Action<UnityEngine.Object> DestroyObjectImmediate = UnityEngine.Object.DestroyImmediate;

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
            TryInitializeNetworkingService(Logger, out var initializedService);
            Service = initializedService;

            var pollerName = $"{MyPluginInfo.PLUGIN_NAME}.Poller";
            var pollers = FindObjectsOfType<NetworkingPoller>(true)
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
            service = null;

            try
            {
                service = CreateDefaultNetworkingService();
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
                logger?.LogError($"FATAL: Failed to initialize fallback OfflineNetworkingService. Networking service disabled. Exception: {exception}");
                service = null;
                return false;
            }
        }

        internal static void ResetNetworkingStartupHooks()
        {
            CreateDefaultNetworkingService = NetworkingServiceFactory.CreateDefaultService;
            CreateOfflineNetworkingService = () => new OfflineNetworkingService();
            IsApplicationPlaying = () => Application.isPlaying;
            DestroyObject = UnityEngine.Object.Destroy;
            DestroyObjectImmediate = UnityEngine.Object.DestroyImmediate;
        }
    }
}
