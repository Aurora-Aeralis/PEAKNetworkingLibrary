using System;
using System.Reflection;
using Steamworks;
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public static class NetworkingServiceFactory
    {
        public static INetworkingService CreateDefaultService()
        {
            try
            {
                if (SteamAPI.IsSteamRunning())
                {
                    Net.Logger.LogInfo("Steam type present. Creating SteamNetworkingService.");
                    return new SteamNetworkingService();
                }
            }
            catch (Exception exception)
            {
                Net.Logger.LogError($"Steam availability probe failed. Falling back to OfflineNetworkingService. Exception: {exception}");
            }

            Net.Logger.LogInfo("Steam not available. Creating OfflineNetworkingService.");
            return new OfflineNetworkingService();
        }
    }
}
