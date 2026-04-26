using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
        internal const int defaultMaxActionsPerFrame = 128;
        internal const double defaultMaxFrameWorkMilliseconds = 4.0d;
        internal const int defaultBacklogWarningFrameThreshold = 120;
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
        static string? lastOverflowWarningMessageForTests;
        static long lastOverflowWarningTicks;
        static int suppressedOverflowLoggerFailures;
        static long lastOverflowLoggerFailureTicks;
        static int consecutiveBacklogFrames;
        static int suppressedBacklogWarnings;
        static int emittedBacklogWarningCountForTests;
        static long lastBacklogWarningTicks;
        static int suppressedActionErrorLogs;
        static int emittedActionErrorLogCountForTests;
        static string? lastActionErrorMessageForTests;
        static long lastActionErrorLogTicks;
        static readonly ManualResetEventSlim instanceReady = new(false);
        static readonly TimeSpan defaultBackgroundThreadWaitTimeout = TimeSpan.FromSeconds(2);

        internal static Func<UnityMainThreadDispatcher> CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;
        internal static TimeSpan BackgroundThreadInstanceWaitTimeout = defaultBackgroundThreadWaitTimeout;
        internal static Func<int?>? MaxQueueDepthProvider;
        internal static Func<QueueOverflowBehavior>? QueueOverflowBehaviorProvider;
        internal static Func<int?>? MaxActionsPerFrameProvider;
        internal static Func<double?>? MaxFrameWorkMillisecondsProvider;
        internal static Func<int?>? BacklogWarningFrameThresholdProvider;
        internal static Action<DelayedEnqueueObservation>? DelayedEnqueueRejectedObserver;

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
            var stopwatch = Stopwatch.StartNew();
            var wait = TimeSpan.FromMilliseconds(10);
            var maxWait = TimeSpan.FromMilliseconds(200);

            while (stopwatch.Elapsed < timeout)
            {
                var remaining = timeout - stopwatch.Elapsed;
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

        public bool TryEnqueue(Action a, float delaySeconds = 0f)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (delaySeconds <= 0f) return TryEnqueueBounded(a, delayed: false, out _);
            return TryEnqueueBounded(CreateDelayedEnqueueAction(a, delaySeconds), delayed: true, out _);
        }

        public void Enqueue(Action a, float delaySeconds = 0f) => Enqueue(a, delaySeconds, throwOnRejection: false);

        public void Enqueue(Action a, float delaySeconds, bool throwOnRejection)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            EnqueueRejectionInfo? rejection;
            var accepted = delaySeconds > 0f
                ? TryEnqueueBounded(CreateDelayedEnqueueAction(a, delaySeconds), delayed: true, out rejection)
                : TryEnqueueBounded(a, delayed: false, out rejection);
            if (accepted || rejection == null) return;

            var message = FormatEnqueueRejectionMessage(rejection.Value);
            if (throwOnRejection) throw new InvalidOperationException(message);
        }

        Action CreateDelayedEnqueueAction(Action action, float delaySeconds)
        {
            var delayedWork = new DelayedEnqueueWork(this, action, delaySeconds);
            return delayedWork.Invoke;
        }

        IEnumerator EnqueueDelayed(Action a, float d)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            yield return new WaitForSeconds(d);
            var accepted = TryEnqueueBounded(a, delayed: true, out var rejection);
            if (accepted || rejection == null) yield break;
            NotifyDelayedEnqueueRejected(rejection.Value);
        }

        sealed class DelayedEnqueueWork
        {
            readonly UnityMainThreadDispatcher dispatcher;
            readonly Action action;
            readonly float delaySeconds;

            internal DelayedEnqueueWork(UnityMainThreadDispatcher dispatcher, Action action, float delaySeconds)
            {
                this.dispatcher = dispatcher;
                this.action = action;
                this.delaySeconds = delaySeconds;
            }

            internal void Invoke() => dispatcher.StartCoroutine(dispatcher.EnqueueDelayed(action, delaySeconds));

            public override bool Equals(object? obj)
            {
                if (obj is not DelayedEnqueueWork other) return false;
                return ReferenceEquals(dispatcher, other.dispatcher)
                    && action.Method == other.action.Method
                    && Equals(action.Target, other.action.Target)
                    && delaySeconds.Equals(other.delaySeconds);
            }

            public override int GetHashCode() => HashCode.Combine(dispatcher, action.Method, action.Target, delaySeconds);
        }

        static bool TryEnqueueBounded(Action action, bool delayed, out EnqueueRejectionInfo? rejection)
        {
            var maxDepth = ResolveMaxQueueDepth();
            var behavior = QueueOverflowBehaviorProvider?.Invoke() ?? QueueOverflowBehavior.DropOldest;
            rejection = null;
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
                            rejection = EnqueueRejectionInfo.Create(delayed, behavior, queue.Count, maxDepth, "coalesced duplicate action");
                            EmitOverflowWarning(FormatEnqueueRejectionMessage(rejection.Value));
                            return false;
                        }

                        rejectedEnqueueCount++;
                        rejection = EnqueueRejectionInfo.Create(delayed, behavior, queue.Count, maxDepth, "coalesce fallback rejected non-duplicate action");
                        EmitOverflowWarning(FormatEnqueueRejectionMessage(rejection.Value));
                        return false;
                    case QueueOverflowBehavior.RejectNewWork:
                    default:
                        rejectedEnqueueCount++;
                        rejection = EnqueueRejectionInfo.Create(delayed, behavior, queue.Count, maxDepth, "rejected new action");
                        EmitOverflowWarning(FormatEnqueueRejectionMessage(rejection.Value));
                        return false;
                }
            }
        }

        static string FormatEnqueueRejectionMessage(EnqueueRejectionInfo rejection)
            => $"UnityMainThreadDispatcher enqueue rejected {rejection.Mode} work ({rejection.Reason}); overflowBehavior={rejection.Behavior}, queueDepth={rejection.QueueDepth}, maxDepth={rejection.MaxDepth}.";

        readonly struct EnqueueRejectionInfo
        {
            internal readonly string Mode;
            internal readonly QueueOverflowBehavior Behavior;
            internal readonly int QueueDepth;
            internal readonly int MaxDepth;
            internal readonly string Reason;

            EnqueueRejectionInfo(string mode, QueueOverflowBehavior behavior, int queueDepth, int maxDepth, string reason)
            {
                Mode = mode;
                Behavior = behavior;
                QueueDepth = queueDepth;
                MaxDepth = maxDepth;
                Reason = reason;
            }

            internal static EnqueueRejectionInfo Create(bool delayed, QueueOverflowBehavior behavior, int queueDepth, int maxDepth, string reason)
                => new(delayed ? "delayed" : "immediate", behavior, queueDepth, maxDepth, reason);
        }

        internal readonly struct DelayedEnqueueObservation
        {
            internal readonly QueueOverflowBehavior Behavior;
            internal readonly int QueueDepth;
            internal readonly int MaxDepth;
            internal readonly string Reason;
            internal readonly string Message;

            internal DelayedEnqueueObservation(QueueOverflowBehavior behavior, int queueDepth, int maxDepth, string reason, string message)
            {
                Behavior = behavior;
                QueueDepth = queueDepth;
                MaxDepth = maxDepth;
                Reason = reason;
                Message = message;
            }
        }

        static void NotifyDelayedEnqueueRejected(EnqueueRejectionInfo rejection)
        {
            var observer = DelayedEnqueueRejectedObserver;
            if (observer == null) return;
            var message = FormatEnqueueRejectionMessage(rejection);
            try
            {
                observer(new DelayedEnqueueObservation(rejection.Behavior, rejection.QueueDepth, rejection.MaxDepth, rejection.Reason, message));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityMainThreadDispatcher] Delayed enqueue rejection observer failed. Exception: {ex.GetType().Name}: {ex.Message}. Original message: {message}");
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
                Volatile.Write(ref lastOverflowWarningMessageForTests, message);
                Net.Logger?.LogWarning(message);
            }
            catch (Exception ex)
            {
                EmitOverflowLoggerFailureWarning(ex, message, nowTicks);
            }
        }

        static void EmitOverflowLoggerFailureWarning(Exception ex, string originalMessage, long nowTicks)
        {
            while (true)
            {
                var previousTicks = Interlocked.Read(ref lastOverflowLoggerFailureTicks);
                if (previousTicks != 0 && new TimeSpan(nowTicks - previousTicks) < overflowWarningCooldown)
                {
                    Interlocked.Increment(ref suppressedOverflowLoggerFailures);
                    return;
                }

                if (Interlocked.CompareExchange(ref lastOverflowLoggerFailureTicks, nowTicks, previousTicks) == previousTicks) break;
            }
            var suppressed = Interlocked.Exchange(ref suppressedOverflowLoggerFailures, 0);
            var suppressedSuffix = suppressed > 0 ? $" Suppressed {suppressed} similar logger failures." : string.Empty;
            Debug.LogWarning($"[UnityMainThreadDispatcher] Failed to write overflow warning. Exception: {ex.GetType().Name}: {ex.Message}. Original message: {originalMessage}.{suppressedSuffix}");
        }

        void Update()
        {
            var maxActions = ResolveMaxActionsPerFrame();
            var maxFrameMs = ResolveMaxFrameWorkMilliseconds();
            var stopwatch = maxFrameMs > 0d ? Stopwatch.StartNew() : null;
            var processed = 0;
            while (true)
            {
                Action a = null!;
                lock (queue)
                {
                    if (queue.Count > 0) a = queue.Dequeue();
                    else break;
                }
                try { a?.Invoke(); } catch (Exception ex) { EmitActionErrorLog(ex); }
                processed++;
                if (maxActions > 0 && processed >= maxActions) break;
                if (stopwatch != null && stopwatch.Elapsed.TotalMilliseconds >= maxFrameMs) break;
            }

            TrackBacklogWarning();
        }

        static int ResolveMaxActionsPerFrame()
        {
            var configured = MaxActionsPerFrameProvider?.Invoke();
            if (configured.HasValue && configured.Value > 0) return configured.Value;
            return defaultMaxActionsPerFrame;
        }

        static double ResolveMaxFrameWorkMilliseconds()
        {
            var configured = MaxFrameWorkMillisecondsProvider?.Invoke();
            if (configured.HasValue && configured.Value > 0d) return configured.Value;
            return defaultMaxFrameWorkMilliseconds;
        }

        static int ResolveBacklogWarningFrameThreshold()
        {
            var configured = BacklogWarningFrameThresholdProvider?.Invoke();
            if (configured.HasValue && configured.Value > 0) return configured.Value;
            return defaultBacklogWarningFrameThreshold;
        }

        static void TrackBacklogWarning()
        {
            var remaining = 0;
            lock (queue) remaining = queue.Count;
            if (remaining <= 0)
            {
                Interlocked.Exchange(ref consecutiveBacklogFrames, 0);
                return;
            }

            var backlogFrames = Interlocked.Increment(ref consecutiveBacklogFrames);
            var threshold = ResolveBacklogWarningFrameThreshold();
            if (threshold <= 0 || backlogFrames < threshold) return;
            EmitBacklogWarning($"UnityMainThreadDispatcher backlog persisted for {backlogFrames} frames with {remaining} actions still queued.");
        }

        static void EmitBacklogWarning(string message)
        {
            while (true)
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                var previousTicks = Interlocked.Read(ref lastBacklogWarningTicks);
                if (previousTicks != 0 && new TimeSpan(nowTicks - previousTicks) < overflowWarningCooldown)
                {
                    Interlocked.Increment(ref suppressedBacklogWarnings);
                    return;
                }

                if (Interlocked.CompareExchange(ref lastBacklogWarningTicks, nowTicks, previousTicks) == previousTicks) break;
            }

            try
            {
                var suppressed = Interlocked.Exchange(ref suppressedBacklogWarnings, 0);
                if (suppressed > 0) message = $"{message} Suppressed {suppressed} similar warnings.";
                Interlocked.Increment(ref emittedBacklogWarningCountForTests);
                Net.Logger?.LogWarning(message);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityMainThreadDispatcher] Failed to write backlog warning. Exception: {ex.GetType().Name}: {ex.Message}. Original message: {message}");
            }
        }

        static void EmitActionErrorLog(Exception ex)
        {
            while (true)
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                var previousTicks = Interlocked.Read(ref lastActionErrorLogTicks);
                if (previousTicks != 0 && new TimeSpan(nowTicks - previousTicks) < overflowWarningCooldown)
                {
                    Interlocked.Increment(ref suppressedActionErrorLogs);
                    return;
                }

                if (Interlocked.CompareExchange(ref lastActionErrorLogTicks, nowTicks, previousTicks) == previousTicks)
                {
                    var suppressed = Interlocked.Exchange(ref suppressedActionErrorLogs, 0);
                    var message = $"Dispatcher action error: {ex}";
                    if (suppressed > 0) message = $"{message} Suppressed {suppressed} similar action exceptions.";
                    Interlocked.Increment(ref emittedActionErrorLogCountForTests);
                    Volatile.Write(ref lastActionErrorMessageForTests, message);
                    Debug.LogError(message);
                    return;
                }
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
                    lastOverflowWarningMessageForTests = null;
                    lastOverflowWarningTicks = 0;
                    suppressedOverflowLoggerFailures = 0;
                    lastOverflowLoggerFailureTicks = 0;
                    consecutiveBacklogFrames = 0;
                    suppressedBacklogWarnings = 0;
                    emittedBacklogWarningCountForTests = 0;
                    lastBacklogWarningTicks = 0;
                    suppressedActionErrorLogs = 0;
                    emittedActionErrorLogCountForTests = 0;
                    lastActionErrorMessageForTests = null;
                    lastActionErrorLogTicks = 0;
                    CreateInstanceOnMainThreadFactory = CreateOrFindDispatcherOnMainThread;
                    BackgroundThreadInstanceWaitTimeout = defaultBackgroundThreadWaitTimeout;
                    MaxQueueDepthProvider = null;
                    QueueOverflowBehaviorProvider = null;
                    MaxActionsPerFrameProvider = null;
                    MaxFrameWorkMillisecondsProvider = null;
                    BacklogWarningFrameThresholdProvider = null;
                    DelayedEnqueueRejectedObserver = null;
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
            internal static string? LastOverflowWarningMessageForTests => Volatile.Read(ref lastOverflowWarningMessageForTests);
            internal static long LastOverflowWarningTicksForTests
            {
                get => Interlocked.Read(ref lastOverflowWarningTicks);
                set => Interlocked.Exchange(ref lastOverflowWarningTicks, value);
            }
            internal static int MaxQueueDepthForTests => ResolveMaxQueueDepth();
            internal static int MaxActionsPerFrameForTests => ResolveMaxActionsPerFrame();
            internal static double MaxFrameWorkMillisecondsForTests => ResolveMaxFrameWorkMilliseconds();
            internal static int BacklogWarningFrameThresholdForTests => ResolveBacklogWarningFrameThreshold();
            internal static int ConsecutiveBacklogFramesForTests => Volatile.Read(ref consecutiveBacklogFrames);
            internal static int SuppressedBacklogWarningsForTests => Volatile.Read(ref suppressedBacklogWarnings);
            internal static int EmittedBacklogWarningCountForTests => Volatile.Read(ref emittedBacklogWarningCountForTests);
            internal static long LastBacklogWarningTicksForTests
            {
                get => Interlocked.Read(ref lastBacklogWarningTicks);
                set => Interlocked.Exchange(ref lastBacklogWarningTicks, value);
            }
            internal static int SuppressedActionErrorLogsForTests => Volatile.Read(ref suppressedActionErrorLogs);
            internal static int EmittedActionErrorLogCountForTests => Volatile.Read(ref emittedActionErrorLogCountForTests);
            internal static string? LastActionErrorMessageForTests => Volatile.Read(ref lastActionErrorMessageForTests);
            internal static long LastActionErrorLogTicksForTests
            {
                get => Interlocked.Read(ref lastActionErrorLogTicks);
                set => Interlocked.Exchange(ref lastActionErrorLogTicks, value);
            }
        }
    }
}
