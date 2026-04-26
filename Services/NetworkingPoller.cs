using System;
using UnityEngine;
using NetworkingLibrary.Modules;

namespace NetworkingLibrary.Services
{
    public class NetworkingPoller : MonoBehaviour
    {
        const float ErrorLogCooldownSeconds = 2f;

        float mainThreadDispatcherLastErrorLogTime = float.NegativeInfinity;
        bool mainThreadDispatcherHadFault;
        bool mainThreadDispatcherSuppressedFault;
        float pollReceiveLastErrorLogTime = float.NegativeInfinity;
        bool pollReceiveHadFault;
        bool pollReceiveSuppressedFault;

        void Update()
        {
            PollGuarded(
                UnityMainThreadDispatcher.ProcessPendingMainThreadWork,
                "Main-thread dispatcher",
                ref mainThreadDispatcherLastErrorLogTime,
                ref mainThreadDispatcherHadFault,
                ref mainThreadDispatcherSuppressedFault);

            PollGuarded(
                () => Net.Service?.PollReceive(),
                "PollReceive",
                ref pollReceiveLastErrorLogTime,
                ref pollReceiveHadFault,
                ref pollReceiveSuppressedFault);
        }

        static void PollGuarded(Action action, string name, ref float lastErrorLogTime, ref bool hadFault, ref bool suppressedFault)
        {
            var succeeded = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                succeeded = false;
                hadFault = true;

                var currentTime = Time.unscaledTime;
                if (currentTime - lastErrorLogTime >= ErrorLogCooldownSeconds)
                {
                    lastErrorLogTime = currentTime;
                    Net.Logger?.LogError($"{name} error: {ex}");
                    suppressedFault = false;
                }
                else
                {
                    suppressedFault = true;
                }
            }

            if (!succeeded || !hadFault) return;
            Net.Logger?.LogInfo(suppressedFault
                ? $"{name} recovered after repeated failures."
                : $"{name} recovered.");
            hadFault = false;
            suppressedFault = false;
        }

        void Awake()
        {
            DontDestroyOnLoad(this.gameObject);
        }
    }
}
