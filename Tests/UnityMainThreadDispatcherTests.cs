using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NetworkingLibrary.Modules;
using UnityEngine;
using Xunit;

namespace NetworkingLibrary.Tests;

[Collection("UnityMainThreadDispatcher")]
public class UnityMainThreadDispatcherTests : IDisposable
{
    sealed class DummyComponent : MonoBehaviour { }

    public UnityMainThreadDispatcherTests()
    {
        UnityMainThreadDispatcher.TestHooks.ResetForTests();
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(Thread.CurrentThread.ManagedThreadId);
    }

    public void Dispose() => UnityMainThreadDispatcher.TestHooks.ResetForTests();

    [Fact]
    public void Instance_FromWorkerThread_QueuesCreationRequestWithoutCreatingOnWorker()
    {
        UnityMainThreadDispatcher.BackgroundThreadInstanceWaitTimeout = TimeSpan.FromMilliseconds(250);
        var createCalls = 0;
        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () =>
        {
            Interlocked.Increment(ref createCalls);
            return (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        };

        var error = Record.Exception(() => Task.Run(() => UnityMainThreadDispatcher.Instance()).GetAwaiter().GetResult());

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, createCalls);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);
    }

    [Fact]
    public void Instance_FromWorkerThread_WhenMainThreadUnknown_DoesNotCaptureWorkerThread()
    {
        UnityMainThreadDispatcher.TestHooks.ResetForTests();
        UnityMainThreadDispatcher.BackgroundThreadInstanceWaitTimeout = TimeSpan.FromMilliseconds(250);

        var error = Record.Exception(() => Task.Run(() => UnityMainThreadDispatcher.Instance()).GetAwaiter().GetResult());

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(-1, UnityMainThreadDispatcher.TestHooks.CurrentMainThreadIdForTests);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);
    }

    [Fact]
    public async Task Instance_ConcurrentCalls_CreateOnlyOnceOnMainThread()
    {
        const int WorkerCount = 12;
        var createCalls = 0;
        var factoryThreadIds = new ConcurrentBag<int>();
        UnityMainThreadDispatcher.BackgroundThreadInstanceWaitTimeout = TimeSpan.FromSeconds(5);
        using var ready = new CountdownEvent(WorkerCount);
        using var start = new ManualResetEventSlim(false);

        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () =>
        {
            factoryThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            Interlocked.Increment(ref createCalls);
            return (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        };
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(int.MinValue);

        var tasks = Enumerable.Range(0, WorkerCount)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                ready.Signal();
                start.Wait();
                return UnityMainThreadDispatcher.Instance();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        start.Set();
        Assert.True(SpinWait.SpinUntil(() => UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests != 0, TimeSpan.FromSeconds(5)));

        var processingThreadId = Thread.CurrentThread.ManagedThreadId;
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(processingThreadId);
        UnityMainThreadDispatcher.ProcessPendingMainThreadWork();
        var instances = await Task.WhenAll(tasks);

        Assert.Equal(1, createCalls);
        Assert.All(factoryThreadIds, id => Assert.Equal(processingThreadId, id));
        Assert.All(instances, item => Assert.Same(instances[0], item));
    }

    [Fact]
    public async Task Instance_RepeatedWorkerCalls_DoNotGrowQueuedActionDepth()
    {
        UnityMainThreadDispatcher.BackgroundThreadInstanceWaitTimeout = TimeSpan.FromMilliseconds(100);
        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () => (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(int.MinValue);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Record.Exception(() => UnityMainThreadDispatcher.Instance())))
            .ToArray();

        await Task.WhenAll(attempts);

        Assert.All(attempts, task => Assert.IsType<InvalidOperationException>(task.Result));
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);

        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(Thread.CurrentThread.ManagedThreadId);
        UnityMainThreadDispatcher.ProcessPendingMainThreadWork();

        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
    }

    [Fact]
    public async Task Instance_FromWorkerThread_WithDelayedMainThreadProcessing_DoesNotThrow()
    {
        UnityMainThreadDispatcher.BackgroundThreadInstanceWaitTimeout = TimeSpan.FromSeconds(2);
        var createCalls = 0;

        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () =>
        {
            Interlocked.Increment(ref createCalls);
            return (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        };
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(int.MinValue);

        var workerTask = Task.Run(() => UnityMainThreadDispatcher.Instance());

        await Task.Delay(450);
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(Thread.CurrentThread.ManagedThreadId);
        UnityMainThreadDispatcher.ProcessPendingMainThreadWork();

        var instance = await workerTask;
        Assert.NotNull(instance);
        Assert.Equal(1, createCalls);
    }

    [Fact]
    public async Task ProcessPendingMainThreadWork_RetainsCreateRequest_WhenFactoryThrows()
    {
        UnityMainThreadDispatcher.BackgroundThreadInstanceWaitTimeout = TimeSpan.FromMilliseconds(50);
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(int.MinValue);

        var worker = Task.Run(() => Record.Exception(() => UnityMainThreadDispatcher.Instance()));
        var workerError = await worker;
        Assert.IsType<InvalidOperationException>(workerError);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);

        var createCalls = 0;
        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () =>
        {
            createCalls++;
            if (createCalls == 1) throw new InvalidOperationException("factory failed");
            return (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        };
        UnityMainThreadDispatcher.TestHooks.SetMainThreadIdForTests(Thread.CurrentThread.ManagedThreadId);

        var mainThreadError = Assert.Throws<InvalidOperationException>(UnityMainThreadDispatcher.ProcessPendingMainThreadWork);
        Assert.Equal("factory failed", mainThreadError.Message);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);

        UnityMainThreadDispatcher.ProcessPendingMainThreadWork();

        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);
        Assert.Equal(2, createCalls);
    }

    [Fact]
    public void OnDestroy_WhenActiveInstance_ClearsCachedDispatcher()
    {
        var destroyed = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        var replacement = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () => destroyed;

        Assert.Same(destroyed, UnityMainThreadDispatcher.Instance());
        typeof(UnityMainThreadDispatcher)
            .GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(destroyed, null);

        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () => replacement;
        Assert.Same(replacement, UnityMainThreadDispatcher.Instance());
    }

    [UnityRuntimeFact]
    public void Awake_WhenDuplicateDispatcherSharesObjectWithOtherComponents_DestroysOnlyDuplicateComponent()
    {
        GameObject? canonicalObject = null;
        GameObject? duplicateObject = null;
        try
        {
            canonicalObject = new GameObject("canonical-dispatcher");
            var canonical = canonicalObject.AddComponent<UnityMainThreadDispatcher>();
            duplicateObject = new GameObject("duplicate-dispatcher-with-component");
            var dummy = duplicateObject.AddComponent<DummyComponent>();
            var duplicate = duplicateObject.AddComponent<UnityMainThreadDispatcher>();

            Assert.True(canonicalObject);
            Assert.True(duplicateObject);
            Assert.True(dummy);
            Assert.False(duplicate);
            Assert.Same(canonical, UnityMainThreadDispatcher.Instance());
        }
        finally
        {
            if (duplicateObject) UnityEngine.Object.DestroyImmediate(duplicateObject);
            if (canonicalObject) UnityEngine.Object.DestroyImmediate(canonicalObject);
        }
    }

    [Fact]
    public void Enqueue_WhenQueueLimitReached_RejectsNewWork()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 3;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;

        var accepted = new List<bool>();
        for (var i = 0; i < 6; i++) accepted.Add(dispatcher.TryEnqueue(() => { }));

        Assert.Equal(new[] { true, true, true, false, false, false }, accepted);
        Assert.Equal(3, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(3, UnityMainThreadDispatcher.TestHooks.QueueHighWaterMarkForTests);
        Assert.Equal(3, UnityMainThreadDispatcher.TestHooks.RejectedEnqueueCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.DroppedEnqueueCountForTests);
    }

    [Fact]
    public void Enqueue_WhenQueueLimitReached_EmitsRejectionDiagnostics()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;

        dispatcher.Enqueue(() => { });
        dispatcher.Enqueue(() => { }, 1f);

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Contains("enqueue rejected delayed work", UnityMainThreadDispatcher.TestHooks.LastOverflowWarningMessageForTests);
        Assert.Contains("overflowBehavior=RejectNewWork", UnityMainThreadDispatcher.TestHooks.LastOverflowWarningMessageForTests);
        Assert.Contains("queueDepth=1, maxDepth=1", UnityMainThreadDispatcher.TestHooks.LastOverflowWarningMessageForTests);
    }

    [Fact]
    public void Enqueue_StrictMode_WhenRejected_ThrowsInvalidOperationException()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;

        dispatcher.Enqueue(() => { });
        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.Enqueue(() => { }, 0f, throwOnRejection: true));

        Assert.Contains("enqueue rejected immediate work", error.Message);
        Assert.Contains("overflowBehavior=RejectNewWork", error.Message);
    }

    [Fact]
    public void Enqueue_WhenAccepted_DoesNotEmitRejectionDiagnostics()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 2;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;

        dispatcher.Enqueue(() => { });

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.EmittedOverflowWarningCountForTests);
        Assert.Null(UnityMainThreadDispatcher.TestHooks.LastOverflowWarningMessageForTests);
    }

    [Fact]
    public void Enqueue_WhenQueueLimitReached_DropsOldest()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 2;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.DropOldest;

        var executed = new List<int>();
        Assert.True(dispatcher.TryEnqueue(() => executed.Add(1)));
        Assert.True(dispatcher.TryEnqueue(() => executed.Add(2)));
        Assert.True(dispatcher.TryEnqueue(() => executed.Add(3)));

        typeof(UnityMainThreadDispatcher)
            .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, null);

        Assert.Equal(new[] { 2, 3 }, executed);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.DroppedEnqueueCountForTests);
        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.QueueHighWaterMarkForTests);
    }

    [Fact]
    public void EnqueueDelayed_WhenQueueLimitReached_UsesSameBoundControls()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;
        UnityMainThreadDispatcher.DelayedEnqueueObservation? observation = null;
        UnityMainThreadDispatcher.DelayedEnqueueRejectedObserver = o => observation = o;

        Assert.True(dispatcher.TryEnqueue(() => { }));
        Assert.False(dispatcher.TryEnqueue(() => { }, 1f));
        var delayed = (System.Collections.IEnumerator)typeof(UnityMainThreadDispatcher)
            .GetMethod("EnqueueDelayed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, new object[] { (Action)(() => { }), 0f })!;

        Assert.True(delayed.MoveNext());
        Assert.False(delayed.MoveNext());

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.RejectedEnqueueCountForTests);
        Assert.True(observation.HasValue);
        Assert.Equal(UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork, observation.Value.Behavior);
        Assert.Equal(1, observation.Value.QueueDepth);
        Assert.Equal(1, observation.Value.MaxDepth);
        Assert.Contains("enqueue rejected delayed work", observation.Value.Message);
    }

    [Fact]
    public void DelayedEnqueueWork_Invoke_WhenDispatcherUnavailable_NotifiesRejectionObserver()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.DelayedEnqueueObservation? observation = null;
        UnityMainThreadDispatcher.DelayedEnqueueRejectedObserver = o => observation = o;

        var delayedAction = (Action)typeof(UnityMainThreadDispatcher)
            .GetMethod("CreateDelayedEnqueueAction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, new object[] { (Action)(() => { }), 0.2f })!;

        delayedAction();

        Assert.True(observation.HasValue);
        Assert.Contains("dispatcher unavailable during delayed enqueue dispatch", observation.Value.Reason);
        Assert.Contains("enqueue rejected delayed work", observation.Value.Message);
    }

    [Fact]
    public void TryEnqueueDelayed_WhenQueueLimitReached_CoalescesDuplicates()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.Coalesce;

        Action action = () => { };
        Assert.True(dispatcher.TryEnqueue(action, 1f));
        Assert.False(dispatcher.TryEnqueue(action, 1f));

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CoalescedEnqueueCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.RejectedEnqueueCountForTests);
    }

    [Fact]
    public void EnqueueDelayed_WhenQueueLimitReached_CoalescesDuplicateAndEmitsObservation()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.Coalesce;
        UnityMainThreadDispatcher.DelayedEnqueueObservation? observation = null;
        UnityMainThreadDispatcher.DelayedEnqueueRejectedObserver = o => observation = o;

        Action action = () => { };
        Assert.True(dispatcher.TryEnqueue(action));
        var delayed = (System.Collections.IEnumerator)typeof(UnityMainThreadDispatcher)
            .GetMethod("EnqueueDelayed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, new object[] { action, 0f })!;

        Assert.True(delayed.MoveNext());
        Assert.False(delayed.MoveNext());

        Assert.True(observation.HasValue);
        Assert.Equal(UnityMainThreadDispatcher.QueueOverflowBehavior.Coalesce, observation.Value.Behavior);
        Assert.Equal("coalesced duplicate action", observation.Value.Reason);
        Assert.Contains("enqueue rejected delayed work", observation.Value.Message);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CoalescedEnqueueCountForTests);
    }

    [Fact]
    public async Task Enqueue_ConcurrentProducers_RemainsBoundedAndThreadSafe()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 64;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.DropOldest;

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 200; i++) dispatcher.Enqueue(() => { });
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.True(UnityMainThreadDispatcher.TestHooks.QueueDepthForTests <= 64);
        Assert.True(UnityMainThreadDispatcher.TestHooks.QueueHighWaterMarkForTests <= 64);
        Assert.True(UnityMainThreadDispatcher.TestHooks.DroppedEnqueueCountForTests > 0);
    }

    [Fact]
    public async Task Enqueue_ConcurrentOverflowWarnings_AreCooldownGatedToSingleEmissionPerWindow()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;
        dispatcher.Enqueue(() => { });

        var tasks = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 30; i++) dispatcher.Enqueue(() => { });
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.EmittedOverflowWarningCountForTests);
        Assert.True(UnityMainThreadDispatcher.TestHooks.SuppressedOverflowWarningsForTests > 0);

        UnityMainThreadDispatcher.TestHooks.LastOverflowWarningTicksForTests = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(6)).Ticks;
        dispatcher.Enqueue(() => { });

        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.EmittedOverflowWarningCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.SuppressedOverflowWarningsForTests);
    }

    [Fact]
    public void Enqueue_OverflowWarningCooldown_RecoversWhenClockMovesBackward()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;
        dispatcher.Enqueue(() => { });
        dispatcher.Enqueue(() => { });

        UnityMainThreadDispatcher.TestHooks.LastOverflowWarningTicksForTests = DateTime.UtcNow.AddSeconds(30).Ticks;
        dispatcher.Enqueue(() => { });

        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.EmittedOverflowWarningCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.SuppressedOverflowWarningsForTests);
    }

    [Fact]
    public void Update_WhenPerFrameActionBudgetReached_PartiallyDrainsQueue()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 32;
        UnityMainThreadDispatcher.MaxActionsPerFrameProvider = () => 2;
        UnityMainThreadDispatcher.MaxFrameWorkMillisecondsProvider = () => 1000d;
        UnityMainThreadDispatcher.BacklogWarningFrameThresholdProvider = () => 1000;

        var executed = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var value = i;
            dispatcher.Enqueue(() => executed.Add(value));
        }

        var update = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        update.Invoke(dispatcher, null);
        Assert.Equal(new[] { 0, 1 }, executed);
        Assert.Equal(3, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);

        update.Invoke(dispatcher, null);
        Assert.Equal(new[] { 0, 1, 2, 3 }, executed);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);

        update.Invoke(dispatcher, null);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, executed);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
    }

    [Fact]
    public void Update_WhenBacklogPersistsAcrossFrames_EmitsThrottledWarning()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 32;
        UnityMainThreadDispatcher.MaxActionsPerFrameProvider = () => 1;
        UnityMainThreadDispatcher.MaxFrameWorkMillisecondsProvider = () => 1000d;
        UnityMainThreadDispatcher.BacklogWarningFrameThresholdProvider = () => 2;

        for (var i = 0; i < 6; i++) dispatcher.Enqueue(() => { });
        var update = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;

        update.Invoke(dispatcher, null);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.EmittedBacklogWarningCountForTests);

        update.Invoke(dispatcher, null);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.EmittedBacklogWarningCountForTests);

        update.Invoke(dispatcher, null);
        Assert.True(UnityMainThreadDispatcher.TestHooks.SuppressedBacklogWarningsForTests > 0);

        UnityMainThreadDispatcher.TestHooks.LastBacklogWarningTicksForTests = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(6)).Ticks;
        update.Invoke(dispatcher, null);
        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.EmittedBacklogWarningCountForTests);
    }

    [Fact]
    public void Update_BacklogWarningCooldown_RecoversWhenClockMovesBackward()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 32;
        UnityMainThreadDispatcher.MaxActionsPerFrameProvider = () => 1;
        UnityMainThreadDispatcher.MaxFrameWorkMillisecondsProvider = () => 1000d;
        UnityMainThreadDispatcher.BacklogWarningFrameThresholdProvider = () => 1;

        for (var i = 0; i < 4; i++) dispatcher.Enqueue(() => { });
        var update = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        update.Invoke(dispatcher, null);

        UnityMainThreadDispatcher.TestHooks.LastBacklogWarningTicksForTests = DateTime.UtcNow.AddSeconds(30).Ticks;
        update.Invoke(dispatcher, null);

        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.EmittedBacklogWarningCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.SuppressedBacklogWarningsForTests);
    }

    [Fact]
    public void Update_WhenActionsThrowAcrossFrames_ThrottlesAndSummarizesSuppressedExceptions()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 32;
        UnityMainThreadDispatcher.MaxActionsPerFrameProvider = () => 1;
        UnityMainThreadDispatcher.MaxFrameWorkMillisecondsProvider = () => 1000d;
        UnityMainThreadDispatcher.BacklogWarningFrameThresholdProvider = () => 1000;

        for (var i = 0; i < 4; i++) dispatcher.Enqueue(() => throw new InvalidOperationException("boom"));
        var update = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;

        update.Invoke(dispatcher, null);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.EmittedActionErrorLogCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.SuppressedActionErrorLogsForTests);
        Assert.Contains("Dispatcher action error:", UnityMainThreadDispatcher.TestHooks.LastActionErrorMessageForTests);
        Assert.DoesNotContain("Suppressed 1 similar action exceptions.", UnityMainThreadDispatcher.TestHooks.LastActionErrorMessageForTests);

        update.Invoke(dispatcher, null);
        update.Invoke(dispatcher, null);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.EmittedActionErrorLogCountForTests);
        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.SuppressedActionErrorLogsForTests);

        UnityMainThreadDispatcher.TestHooks.LastActionErrorLogTicksForTests = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(6)).Ticks;
        update.Invoke(dispatcher, null);

        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.EmittedActionErrorLogCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.SuppressedActionErrorLogsForTests);
        Assert.Contains("Suppressed 2 similar action exceptions.", UnityMainThreadDispatcher.TestHooks.LastActionErrorMessageForTests);
    }

    [Fact]
    public void Update_ActionErrorCooldown_RecoversWhenClockMovesBackward()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 32;
        UnityMainThreadDispatcher.MaxActionsPerFrameProvider = () => 1;
        UnityMainThreadDispatcher.MaxFrameWorkMillisecondsProvider = () => 1000d;
        UnityMainThreadDispatcher.BacklogWarningFrameThresholdProvider = () => 1000;

        for (var i = 0; i < 2; i++) dispatcher.Enqueue(() => throw new InvalidOperationException("boom"));
        var update = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        update.Invoke(dispatcher, null);

        UnityMainThreadDispatcher.TestHooks.LastActionErrorLogTicksForTests = DateTime.UtcNow.AddSeconds(30).Ticks;
        update.Invoke(dispatcher, null);

        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.EmittedActionErrorLogCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.SuppressedActionErrorLogsForTests);
    }

    [Fact]
    public void Enqueue_WhenQueueBehaviorProviderThrows_FallsBackToDropOldest()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => throw new InvalidOperationException("queue behavior unavailable");

        var executed = new List<int>();
        dispatcher.Enqueue(() => executed.Add(1));
        var error = Record.Exception(() => dispatcher.Enqueue(() => executed.Add(2)));

        typeof(UnityMainThreadDispatcher)
            .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, null);

        Assert.Null(error);
        Assert.Equal(new[] { 2 }, executed);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.DroppedEnqueueCountForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.RejectedEnqueueCountForTests);
    }

    [Fact]
    public void TestHooks_WhenBudgetProvidersThrow_ExposeDefaultValues()
    {
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => throw new InvalidOperationException("depth unavailable");
        UnityMainThreadDispatcher.MaxActionsPerFrameProvider = () => throw new InvalidOperationException("frame action budget unavailable");
        UnityMainThreadDispatcher.MaxFrameWorkMillisecondsProvider = () => throw new InvalidOperationException("frame time budget unavailable");
        UnityMainThreadDispatcher.BacklogWarningFrameThresholdProvider = () => throw new InvalidOperationException("backlog threshold unavailable");

        Assert.Equal(2048, UnityMainThreadDispatcher.TestHooks.MaxQueueDepthForTests);
        Assert.Equal(128, UnityMainThreadDispatcher.TestHooks.MaxActionsPerFrameForTests);
        Assert.Equal(4.0d, UnityMainThreadDispatcher.TestHooks.MaxFrameWorkMillisecondsForTests, 3);
        Assert.Equal(120, UnityMainThreadDispatcher.TestHooks.BacklogWarningFrameThresholdForTests);
    }

    [Fact]
    public void TestHooks_ExposeResolvedFrameBudgetValues()
    {
        UnityMainThreadDispatcher.MaxActionsPerFrameProvider = () => 7;
        UnityMainThreadDispatcher.MaxFrameWorkMillisecondsProvider = () => 2.5d;
        UnityMainThreadDispatcher.BacklogWarningFrameThresholdProvider = () => 9;

        Assert.Equal(7, UnityMainThreadDispatcher.TestHooks.MaxActionsPerFrameForTests);
        Assert.Equal(2.5d, UnityMainThreadDispatcher.TestHooks.MaxFrameWorkMillisecondsForTests, 3);
        Assert.Equal(9, UnityMainThreadDispatcher.TestHooks.BacklogWarningFrameThresholdForTests);
    }
}
