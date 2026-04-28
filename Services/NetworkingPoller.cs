using System;
using UnityEngine;
using NetworkingLibrary.Modules;

namespace NetworkingLibrary.Services
{
    public class NetworkingPoller : MonoBehaviour
    {
        const string LogSource = "NetworkingPoller";
        const float ErrorLogCooldownSeconds = 2f;
        internal static Func<float> TimeProvider = () => Time.unscaledTime;
        internal static Func<double> FallbackTimeProvider = () => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

        double mainThreadDispatcherLastErrorLogTime = double.NegativeInfinity;
        bool mainThreadDispatcherHadFault;
        bool mainThreadDispatcherSuppressedFault;
        int mainThreadDispatcherSuppressedExceptionCount;
        double pollReceiveLastErrorLogTime = double.NegativeInfinity;
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
            ref double lastErrorLogTime,
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

                var currentTime = ResolveTime();
                if (currentTime < lastErrorLogTime || currentTime - lastErrorLogTime >= ErrorLogCooldownSeconds)
                {
                    lastErrorLogTime = currentTime;
                    NetLog.Error(LogSource, $"{name} error: {ex}");
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
            NetLog.Info(LogSource, suppressedFault
                ? $"{name} recovered after repeated failures. Suppressed {suppressedExceptionCount} errors since last emitted error."
                : $"{name} recovered. Suppressed {suppressedExceptionCount} errors since last emitted error.");
            hadFault = false;
            suppressedFault = false;
            suppressedExceptionCount = 0;
        }

        static double ResolveTime()
        {
            try
            {
                var time = TimeProvider();
                if (!float.IsNaN(time) && !float.IsInfinity(time)) return time;
            }
            catch { }

            try
            {
                var time = FallbackTimeProvider();
                if (!double.IsNaN(time) && !double.IsInfinity(time)) return time;
            }
            catch { }
            return DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
        }

        void Awake()
        {
            DontDestroyOnLoad(this.gameObject);
        }
    }
}
