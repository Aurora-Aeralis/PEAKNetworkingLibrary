using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using UnityEngine;
using Mono.Cecil.Cil;
using System.Reflection;
using System.Linq;

using NetworkingLibrary.Services;
using NetworkingLibrary.Patches;
using NetworkingLibrary.Modules;
using NetworkingLibrary.Features;
using System.Xml.Linq;

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

        private void OnDestroy()
        {
            Service?.Shutdown();
            Service = null;

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

            Harmony.PatchAll();

            Service = NetworkingServiceFactory.CreateDefaultService();
            Service.Initialize();

            var pollerName = $"{MyPluginInfo.PLUGIN_NAME}.Poller";
            var existingPoller = FindObjectsOfType<NetworkingPoller>(true).FirstOrDefault();
            var go = existingPoller != null ? existingPoller.gameObject : GameObject.Find(pollerName);
            var foundExistingObject = go != null;
            if (go == null)
            {
                go = new GameObject(pollerName);
                go.AddComponent<NetworkingPoller>().hideFlags = HideFlags.HideAndDontSave;
            }
            else if (go.GetComponent<NetworkingPoller>() == null)
            {
                go.AddComponent<NetworkingPoller>().hideFlags = HideFlags.HideAndDontSave;
            }
            
            // Persist the poller object across scene loads so networking lifecycle remains stable.
            if (foundExistingObject && go.scene.IsValid() && go.scene.name != "DontDestroyOnLoad")
                Logger.LogDebug($"Promoting existing poller object '{go.name}' from scene '{go.scene.name}' to DontDestroyOnLoad lifecycle.");
            DontDestroyOnLoad(go);

            Logger.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} has fully loaded!");
        }
    }
}
