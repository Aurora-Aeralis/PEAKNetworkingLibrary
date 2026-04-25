using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using UnityEngine;
using System.Reflection;
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
        internal static Harmony Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        public ConfigFile config = null!;
        public static INetworkingService? Service { get; private set; } 

        internal static Func<INetworkingService> CreateDefaultNetworkingService = NetworkingServiceFactory.CreateDefaultService;
        internal static Func<INetworkingService> CreateOfflineNetworkingService = () => new OfflineNetworkingService();

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
            }

            if (Harmony == null)
                return;

            try
            {
                Harmony.UnpatchSelf();
                Logger?.LogDebug("Removed Harmony patches during plugin teardown.");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Failed to unpatch Harmony during plugin teardown: {ex.Message}");
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

            CleanupDuplicatePollers(pollers.Skip(1), Destroy);

            var go = canonicalPoller.gameObject;
            go.name = pollerName;
            var foundExistingObject = pollers.Count > 0;
            
            // Persist the poller object across scene loads so networking lifecycle remains stable.
            if (foundExistingObject && go.scene.IsValid() && go.scene.name != "DontDestroyOnLoad")
                Logger.LogDebug($"Promoting existing poller object '{go.name}' from scene '{go.scene.name}' to DontDestroyOnLoad lifecycle.");
            DontDestroyOnLoad(go);

            Logger.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} has fully loaded!");
        }

        private static void CleanupDuplicatePollers(IEnumerable<NetworkingPoller> duplicatePollers, Action<UnityEngine.Object> destroyAction)
        {
            foreach (var extraPoller in duplicatePollers)
            {
                var components = extraPoller.gameObject
                    .GetComponents<Component>()
                    .Where(component => component != null)
                    .ToArray();
                var hasOnlyTransformAndPoller = components.Length == 2
                    && components.Any(component => component is Transform)
                    && components.Any(component => component is NetworkingPoller);
                destroyAction(hasOnlyTransformAndPoller ? extraPoller.gameObject : extraPoller);
            }
        }

        internal static bool TryInitializeNetworkingService(ManualLogSource? logger, out INetworkingService? service)
        {
            service = null;

            try
            {
                service = CreateDefaultNetworkingService();
                service.Initialize();
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
        }
    }
}
