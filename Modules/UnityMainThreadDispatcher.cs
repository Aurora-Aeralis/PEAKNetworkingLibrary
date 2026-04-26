using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace NetworkingLibrary.Modules
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        internal enum QueueOverflowBehavior
        {
            RejectNewWork = 0,
            DropOldest = 1,
            Coalesce = 2
        }

        const int defaultMaxQueueDepth = 2048;
        static readonly TimeSpan overflowWarningCooldown = TimeSpan.FromSeconds(5);
        static UnityMainThreadDispatcher? instance;
        static readonly object instanceLock = new();
        static readonly Queue<Action> queue = new Queue<Action>();
        static int mainThreadId = -1;
        static int createRequestQueued;
        static int queueHighWaterMark;
        static int droppedEnqueueCount;
        static int rejectedEnqueueCount;
        static int coalescedEnqueueCount;
        static int suppressedOverflowWarnings;
        static int emittedOverflowWarningCountForTests;
        static long lastOverflowWarningTicks;
        static readonly ManualResetEventSlim instanceReady = new(false);
        static readonly TimeSpan defaultBackgroundThreadWaitTimeout = TimeSpan.FromSeconds(2);

        internal static Func<UnityMainThreadDispatcher> CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;
        internal static TimeSpan BackgroundThreadInstanceWaitTimeout = defaultBackgroundThreadWaitTimeout;
        internal static Func<int?>? MaxQueueDepthProvider;
        internal static Func<QueueOverflowBehavior>? QueueOverflowBehaviorProvider;

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
                TryEnqueueBounded(a, delayed: false);
            }
            else
            {
                if (IsMainThread()) StartCoroutine(EnqueueDelayed(a, delaySeconds));
                else TryEnqueueBounded(() => StartCoroutine(EnqueueDelayed(a, delaySeconds)), delayed: true);
            }
        }

        IEnumerator EnqueueDelayed(Action a, float d)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            yield return new WaitForSeconds(d);
            TryEnqueueBounded(a, delayed: true);
        }

        static bool TryEnqueueBounded(Action action, bool delayed)
        {
            var maxDepth = ResolveMaxQueueDepth();
            var behavior = QueueOverflowBehaviorProvider?.Invoke() ?? QueueOverflowBehavior.DropOldest;
            lock (queue)
            {
                if (queue.Count < maxDepth)
                {
                    queue.Enqueue(action);
                    if (queue.Count > queueHighWaterMark) queueHighWaterMark = queue.Count;
                    return true;
                }

                switch (behavior)
                {
                    case QueueOverflowBehavior.DropOldest:
                        queue.Dequeue();
                        droppedEnqueueCount++;
                        queue.Enqueue(action);
                        if (queue.Count > queueHighWaterMark) queueHighWaterMark = queue.Count;
                        EmitOverflowWarning($"UnityMainThreadDispatcher queue depth limit ({maxDepth}) reached; dropping oldest pending action to enqueue new {(delayed ? "delayed" : "immediate")} work.");
                        return true;
                    case QueueOverflowBehavior.Coalesce:
                        if (HasEquivalentPendingAction(action))
                        {
                            coalescedEnqueueCount++;
                            EmitOverflowWarning($"UnityMainThreadDispatcher queue depth limit ({maxDepth}) reached; coalescing duplicate {(delayed ? "delayed" : "immediate")} action.");
                            return false;
                        }

                        rejectedEnqueueCount++;
                        EmitOverflowWarning($"UnityMainThreadDispatcher queue depth limit ({maxDepth}) reached; coalesce fallback rejected non-duplicate {(delayed ? "delayed" : "immediate")} action.");
                        return false;
                    case QueueOverflowBehavior.RejectNewWork:
                    default:
                        rejectedEnqueueCount++;
                        EmitOverflowWarning($"UnityMainThreadDispatcher queue depth limit ({maxDepth}) reached; rejecting new {(delayed ? "delayed" : "immediate")} action.");
                        return false;
                }
            }
        }

        static bool HasEquivalentPendingAction(Action action)
        {
            foreach (var pending in queue)
            {
                if (pending == null) continue;
                if (pending.Method == action.Method && Equals(pending.Target, action.Target)) return true;
            }

            return false;
        }

        static int ResolveMaxQueueDepth()
        {
            var configured = MaxQueueDepthProvider?.Invoke();
            if (configured.HasValue && configured.Value > 0) return configured.Value;
            return defaultMaxQueueDepth;
        }

        static void EmitOverflowWarning(string message)
        {
            while (true)
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                var previousTicks = Interlocked.Read(ref lastOverflowWarningTicks);
                if (previousTicks != 0 && new TimeSpan(nowTicks - previousTicks) < overflowWarningCooldown)
                {
                    Interlocked.Increment(ref suppressedOverflowWarnings);
                    return;
                }

                if (Interlocked.CompareExchange(ref lastOverflowWarningTicks, nowTicks, previousTicks) == previousTicks) break;
            }

            try
            {
                var suppressed = Interlocked.Exchange(ref suppressedOverflowWarnings, 0);
                if (suppressed > 0) message = $"{message} Suppressed {suppressed} similar warnings.";
                Interlocked.Increment(ref emittedOverflowWarningCountForTests);
                Net.Logger?.LogWarning(message);
            }
            catch { }
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
                    instanceReady.Reset();
                    queueHighWaterMark = 0;
                    droppedEnqueueCount = 0;
                    rejectedEnqueueCount = 0;
                    coalescedEnqueueCount = 0;
                    suppressedOverflowWarnings = 0;
                    emittedOverflowWarningCountForTests = 0;
                    lastOverflowWarningTicks = 0;
                    CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;
                    BackgroundThreadInstanceWaitTimeout = defaultBackgroundThreadWaitTimeout;
                    MaxQueueDepthProvider = null;
                    QueueOverflowBehaviorProvider = null;
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
            internal static int QueueHighWaterMarkForTests => Volatile.Read(ref queueHighWaterMark);
            internal static int DroppedEnqueueCountForTests => Volatile.Read(ref droppedEnqueueCount);
            internal static int RejectedEnqueueCountForTests => Volatile.Read(ref rejectedEnqueueCount);
            internal static int CoalescedEnqueueCountForTests => Volatile.Read(ref coalescedEnqueueCount);
            internal static int SuppressedOverflowWarningsForTests => Volatile.Read(ref suppressedOverflowWarnings);
            internal static int EmittedOverflowWarningCountForTests => Volatile.Read(ref emittedOverflowWarningCountForTests);
            internal static long LastOverflowWarningTicksForTests
            {
                get => Interlocked.Read(ref lastOverflowWarningTicks);
                set => Interlocked.Exchange(ref lastOverflowWarningTicks, value);
            }
            internal static int MaxQueueDepthForTests => ResolveMaxQueueDepth();
        }
    }
}
