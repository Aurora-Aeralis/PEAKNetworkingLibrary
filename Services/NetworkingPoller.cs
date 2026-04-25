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
            var dispatcherSucceeded = true;
            try
            {
                UnityMainThreadDispatcher.ProcessPendingMainThreadWork();
            }
            catch (Exception ex)
            {
                dispatcherSucceeded = false;
                mainThreadDispatcherHadFault = true;

                var currentTime = Time.unscaledTime;
                if (currentTime - mainThreadDispatcherLastErrorLogTime >= ErrorLogCooldownSeconds)
                {
                    mainThreadDispatcherLastErrorLogTime = currentTime;
                    Net.Logger?.LogError($"NetworkingPollerDebug Main-thread dispatcher error: {ex}");
                    mainThreadDispatcherSuppressedFault = false;
                }
                else
                {
                    mainThreadDispatcherSuppressedFault = true;
                }
            }

            if (dispatcherSucceeded && mainThreadDispatcherHadFault)
            {
                Net.Logger?.LogInfo(mainThreadDispatcherSuppressedFault
                    ? "NetworkingPollerDebug Main-thread dispatcher recovered after repeated failures."
                    : "NetworkingPollerDebug Main-thread dispatcher recovered.");
                mainThreadDispatcherHadFault = false;
                mainThreadDispatcherSuppressedFault = false;
            }

            var pollReceiveSucceeded = true;
            try
            {
                Net.Service?.PollReceive();
            }
            catch (Exception ex)
            {
                pollReceiveSucceeded = false;
                pollReceiveHadFault = true;

                var currentTime = Time.unscaledTime;
                if (currentTime - pollReceiveLastErrorLogTime >= ErrorLogCooldownSeconds)
                {
                    pollReceiveLastErrorLogTime = currentTime;
                    Net.Logger?.LogError($"NetworkingPollerDebug PollReceive error: {ex}");
                    pollReceiveSuppressedFault = false;
                }
                else
                {
                    pollReceiveSuppressedFault = true;
                }
            }

            if (pollReceiveSucceeded && pollReceiveHadFault)
            {
                Net.Logger?.LogInfo(pollReceiveSuppressedFault
                    ? "NetworkingPollerDebug PollReceive recovered after repeated failures."
                    : "NetworkingPollerDebug PollReceive recovered.");
                pollReceiveHadFault = false;
                pollReceiveSuppressedFault = false;
            }
        }

        void Awake()
        {
            DontDestroyOnLoad(this.gameObject);
        }
    }
}
