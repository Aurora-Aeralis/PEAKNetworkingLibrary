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
        static int mainThreadId = -1;
        static int createRequestQueued;
        static readonly ManualResetEventSlim instanceReady = new(false);

        internal static Func<UnityMainThreadDispatcher> CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;

        public static UnityMainThreadDispatcher Instance()
        {
            if (instance != null) return instance;

            if (IsMainThread() || CanCaptureMainThreadFromCurrentContext())
            {
                CaptureMainThreadIfUnknown();
                return EnsureInstanceOnMainThread();
            }

            QueueCreateRequest();
            if (instanceReady.Wait(TimeSpan.FromMilliseconds(200)) && instance != null) return instance;
            if (instance != null) return instance;

            throw new InvalidOperationException("UnityMainThreadDispatcher.Instance() was called from a non-main thread before the main thread could create the dispatcher.");
        }

        static bool CanCaptureMainThreadFromCurrentContext()
        {
            if (Volatile.Read(ref mainThreadId) != -1) return false;

            var context = SynchronizationContext.Current;
            return string.Equals(context?.GetType().FullName, "UnityEngine.UnitySynchronizationContext", StringComparison.Ordinal);
        }

        static void QueueCreateRequest()
        {
            if (Interlocked.Exchange(ref createRequestQueued, 1) == 1) return;
            lock (queue) queue.Enqueue(ProcessPendingMainThreadWork);
        }

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

            if (Volatile.Read(ref createRequestQueued) == 1)
            {
                Interlocked.Exchange(ref createRequestQueued, 0);
                if (instance == null) EnsureInstanceOnMainThread();
            }
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
            ArgumentNullException.ThrowIfNull(a);
            if (delaySeconds <= 0f)
            {
                lock (queue) queue.Enqueue(a);
            }
            else StartCoroutine(EnqueueDelayed(a, delaySeconds));
        }

        IEnumerator EnqueueDelayed(Action a, float d)
        {
            ArgumentNullException.ThrowIfNull(a);
            yield return new WaitForSeconds(d);
            lock (queue) queue.Enqueue(a);
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
                    CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;
                }
            }

            internal static void SetMainThreadIdForTests(int id) => mainThreadId = id;
            internal static int CreateRequestQueuedForTests => Volatile.Read(ref createRequestQueued);
            internal static int CurrentMainThreadIdForTests => Volatile.Read(ref mainThreadId);
        }
    }
}
