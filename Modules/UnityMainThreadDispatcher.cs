using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace NetworkingLibrary.Modules
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        static UnityMainThreadDispatcher? instance;
        static readonly object instanceLock = new();
        static readonly Queue<Action> queue = new Queue<Action>();
        internal const int DefaultMaxQueuedActions = 1024;
        static int maxQueuedActions = DefaultMaxQueuedActions;
        static readonly TimeSpan defaultDropWarningThrottle = TimeSpan.FromSeconds(5);
        static int mainThreadId = -1;
        static int createRequestQueued;
        static int droppedActionCount;
        static long nextDropWarningAtUtcTicks;
        static readonly ManualResetEventSlim instanceReady = new(false);
        static readonly TimeSpan defaultBackgroundThreadWaitTimeout = TimeSpan.FromSeconds(2);

        internal static Func<UnityMainThreadDispatcher> CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;
        internal static Action<string> WarningLogger = message => Debug.LogWarning(message);
        internal static TimeSpan BackgroundThreadInstanceWaitTimeout = defaultBackgroundThreadWaitTimeout;
        internal static TimeSpan DropWarningThrottle = defaultDropWarningThrottle;

        public static int MaxQueuedActions
        {
            get => Volatile.Read(ref maxQueuedActions);
            set => Interlocked.Exchange(ref maxQueuedActions, Math.Max(1, value));
        }

        public static UnityMainThreadDispatcher Instance()
        {
            if (instance != null) return instance;

            if (IsMainThread() || CanCaptureMainThreadFromCurrentContext())
            {
                CaptureMainThreadIfUnknown();
                return EnsureInstanceOnMainThread();
            }

            QueueCreateRequest();
            if (WaitForInstanceFromBackgroundThread() && instance != null) return instance;
            if (instance != null) return instance;

            throw new InvalidOperationException("UnityMainThreadDispatcher.Instance() was called from a non-main thread before the main thread could create the dispatcher.");
        }

        static bool WaitForInstanceFromBackgroundThread()
        {
            var timeout = BackgroundThreadInstanceWaitTimeout;
            if (timeout <= TimeSpan.Zero) timeout = defaultBackgroundThreadWaitTimeout;
            var deadline = DateTime.UtcNow + timeout;
            var wait = TimeSpan.FromMilliseconds(10);
            var maxWait = TimeSpan.FromMilliseconds(200);

            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var currentWait = wait <= remaining ? wait : remaining;
                if (instanceReady.Wait(currentWait)) return true;
                wait = wait < maxWait ? TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds * 2, maxWait.TotalMilliseconds)) : maxWait;
            }

            return false;
        }

        static bool CanCaptureMainThreadFromCurrentContext()
        {
            if (Volatile.Read(ref mainThreadId) != -1) return false;

            var context = SynchronizationContext.Current;
            return string.Equals(context?.GetType().FullName, "UnityEngine.UnitySynchronizationContext", StringComparison.Ordinal);
        }

        static void QueueCreateRequest() => Interlocked.Exchange(ref createRequestQueued, 1);

        static bool IsMainThread()
        {
            var id = Volatile.Read(ref mainThreadId);
            return id != -1 && id == Thread.CurrentThread.ManagedThreadId;
        }

        static void CaptureMainThreadIfUnknown()
        {
            if (Volatile.Read(ref mainThreadId) == -1)
                Interlocked.CompareExchange(ref mainThreadId, Thread.CurrentThread.ManagedThreadId, -1);
        }

        static UnityMainThreadDispatcher EnsureInstanceOnMainThread()
        {
            if (!IsMainThread()) throw new InvalidOperationException("Dispatcher creation must run on the Unity main thread.");

            lock (instanceLock)
            {
                if (instance != null) return instance;

                instance = CreateInstanceOnMainThreadFactory();
                instanceReady.Set();
                return instance!;
            }
        }

        static UnityMainThreadDispatcher CreateOrFindDispatcherOnMainThread()
        {
            var go = GameObject.Find("UnityMainThreadDispatcher");
            if (go == null)
            {
                go = new GameObject("UnityMainThreadDispatcher");
                DontDestroyOnLoad(go);
                return go.AddComponent<UnityMainThreadDispatcher>();
            }

            var dispatcher = go.GetComponent<UnityMainThreadDispatcher>() ?? go.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(go);
            return dispatcher;
        }

        internal static void ProcessPendingMainThreadWork()
        {
            if (!IsMainThread())
            {
                if (!CanCaptureMainThreadFromCurrentContext()) return;
                CaptureMainThreadIfUnknown();
                if (!IsMainThread()) return;
            }

            if (Interlocked.Exchange(ref createRequestQueued, 0) == 1 && instance == null)
                EnsureInstanceOnMainThread();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InitializeMainThreadContext() => CaptureMainThreadIfUnknown();

        void Awake()
        {
            CaptureMainThreadIfUnknown();

            lock (instanceLock)
            {
                if (instance == null)
                {
                    instance = this;
                    instanceReady.Set();
                    DontDestroyOnLoad(gameObject);
                    return;
                }

                if (instance != this) Destroy(gameObject);
            }
        }

        public void Enqueue(Action a, float delaySeconds = 0f)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (delaySeconds <= 0f)
            {
                TryEnqueueAction(a, "immediate");
            }
            else
            {
                if (IsMainThread()) StartCoroutine(EnqueueDelayed(a, delaySeconds));
                else TryEnqueueAction(() => StartCoroutine(EnqueueDelayed(a, delaySeconds)), "delayed-bootstrap");
            }
        }

        IEnumerator EnqueueDelayed(Action a, float d)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            yield return new WaitForSeconds(d);
            TryEnqueueAction(a, "delayed");
        }

        static bool TryEnqueueAction(Action a, string source)
        {
            lock (queue)
            {
                if (queue.Count < MaxQueuedActions)
                {
                    queue.Enqueue(a);
                    return true;
                }
            }

            ReportDroppedAction(source);
            return false;
        }

        static void ReportDroppedAction(string source)
        {
            Interlocked.Increment(ref droppedActionCount);
            var now = DateTime.UtcNow;
            if (now.Ticks < Volatile.Read(ref nextDropWarningAtUtcTicks)) return;

            lock (instanceLock)
            {
                if (now.Ticks < nextDropWarningAtUtcTicks) return;
                nextDropWarningAtUtcTicks = (now + GetSafeDropWarningThrottle()).Ticks;
                var dropped = Interlocked.Exchange(ref droppedActionCount, 0);
                WarningLogger($"UnityMainThreadDispatcher dropped {dropped} queued action(s) because the queue is full (limit={MaxQueuedActions}, source={source}).");
            }
        }

        static TimeSpan GetSafeDropWarningThrottle()
        {
            var throttle = DropWarningThrottle;
            return throttle <= TimeSpan.Zero ? defaultDropWarningThrottle : throttle;
        }

        void Update()
        {
            while (true)
            {
                Action a = null!;
                lock (queue)
                {
                    if (queue.Count > 0) a = queue.Dequeue();
                    else break;
                }
                try { a?.Invoke(); } catch (Exception ex) { Debug.LogError($"Dispatcher action error: {ex}"); }
            }
        }

        internal static class TestHooks
        {
            internal static void ResetForTests()
            {
                lock (instanceLock)
                {
                    instance = null;
                    createRequestQueued = 0;
                    mainThreadId = -1;
                    lock (queue)
                    {
                        while (queue.Count > 0) queue.Dequeue();
                    }
                    droppedActionCount = 0;
                    nextDropWarningAtUtcTicks = 0;
                    instanceReady.Reset();
                    CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;
                    WarningLogger = message => Debug.LogWarning(message);
                    BackgroundThreadInstanceWaitTimeout = defaultBackgroundThreadWaitTimeout;
                    DropWarningThrottle = defaultDropWarningThrottle;
                    MaxQueuedActions = DefaultMaxQueuedActions;
                }
            }

            internal static void SetMainThreadIdForTests(int id) => mainThreadId = id;
            internal static int CreateRequestQueuedForTests => Volatile.Read(ref createRequestQueued);
            internal static int CurrentMainThreadIdForTests => Volatile.Read(ref mainThreadId);
            internal static int QueueDepthForTests
            {
                get
                {
                    lock (queue) return queue.Count;
                }
            }

            internal static int DroppedActionCountForTests => Volatile.Read(ref droppedActionCount);
            internal static TimeSpan DropWarningThrottleForTests
            {
                get => DropWarningThrottle;
                set => DropWarningThrottle = value;
            }

            internal static int MaxQueuedActionsForTests
            {
                get => MaxQueuedActions;
                set => MaxQueuedActions = value;
            }

            internal static void SetWarningLoggerForTests(Action<string> logger) => WarningLogger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
    }
}
