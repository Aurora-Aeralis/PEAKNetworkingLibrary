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
        int mainThreadDispatcherSuppressedExceptionCount;
        float pollReceiveLastErrorLogTime = float.NegativeInfinity;
        bool pollReceiveHadFault;
        bool pollReceiveSuppressedFault;
        int pollReceiveSuppressedExceptionCount;

        void Update()
        {
            PollGuarded(
                UnityMainThreadDispatcher.ProcessPendingMainThreadWork,
                "Main-thread dispatcher",
                ref mainThreadDispatcherLastErrorLogTime,
                ref mainThreadDispatcherHadFault,
                ref mainThreadDispatcherSuppressedFault,
                ref mainThreadDispatcherSuppressedExceptionCount);

            PollGuarded(
                () => Net.Service?.PollReceive(),
                "PollReceive",
                ref pollReceiveLastErrorLogTime,
                ref pollReceiveHadFault,
                ref pollReceiveSuppressedFault,
                ref pollReceiveSuppressedExceptionCount);
        }

        static void PollGuarded(
            Action action,
            string name,
            ref float lastErrorLogTime,
            ref bool hadFault,
            ref bool suppressedFault,
            ref int suppressedExceptionCount)
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
                    suppressedExceptionCount = 0;
                }
                else
                {
                    suppressedFault = true;
                    suppressedExceptionCount++;
                }
            }

            if (!succeeded || !hadFault) return;
            Net.Logger?.LogInfo(suppressedFault
                ? $"{name} recovered after repeated failures. Suppressed {suppressedExceptionCount} errors since last emitted error."
                : $"{name} recovered. Suppressed {suppressedExceptionCount} errors since last emitted error.");
            hadFault = false;
            suppressedFault = false;
            suppressedExceptionCount = 0;
        }

        void Awake()
        {
            DontDestroyOnLoad(this.gameObject);
        }
    }
}
