using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class UnityMainThreadDispatcherTests : IDisposable
{
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
        var createCalls = 0;
        var factoryThreadIds = new ConcurrentBag<int>();

        UnityMainThreadDispatcher.CreateInstanceOnMainThreadFactory = () =>
        {
            factoryThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            Interlocked.Increment(ref createCalls);
            return (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        };

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => UnityMainThreadDispatcher.Instance()))
            .ToArray();

        var stopAt = DateTime.UtcNow.AddSeconds(2);
        while (UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests == 0 && DateTime.UtcNow < stopAt)
            await Task.Delay(5);

        UnityMainThreadDispatcher.ProcessPendingMainThreadWork();
        var instances = await Task.WhenAll(tasks);

        Assert.Equal(1, createCalls);
        Assert.All(factoryThreadIds, id => Assert.Equal(Thread.CurrentThread.ManagedThreadId, id));
        Assert.All(instances, item => Assert.Same(instances[0], item));
    }


    [Fact]
    public async Task Instance_RepeatedWorkerCalls_DoNotGrowQueuedActionDepth()
    {
        UnityMainThreadDispatcher.BackgroundThreadInstanceWaitTimeout = TimeSpan.FromMilliseconds(100);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Record.Exception(() => UnityMainThreadDispatcher.Instance())))
            .ToArray();

        await Task.WhenAll(attempts);

        Assert.All(attempts, task => Assert.IsType<InvalidOperationException>(task.Result));
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CreateRequestQueuedForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);

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

        var workerTask = Task.Run(() => UnityMainThreadDispatcher.Instance());

        await Task.Delay(450);
        UnityMainThreadDispatcher.ProcessPendingMainThreadWork();

        var instance = await workerTask;
        Assert.NotNull(instance);
        Assert.Equal(1, createCalls);
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

        Assert.True(dispatcher.TryEnqueue(() => { }));
        Assert.False(dispatcher.TryEnqueue(() => { }, 1f));
        var delayed = (System.Collections.IEnumerator)typeof(UnityMainThreadDispatcher)
            .GetMethod("EnqueueDelayed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, new object[] { (Action)(() => { }), 0f })!;

        Assert.True(delayed.MoveNext());
        var error = Record.Exception(() => delayed.MoveNext());
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("RejectNewWork", error!.Message);
        Assert.Contains("delayed", error.Message);

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.RejectedEnqueueCountForTests);
    }

    [Fact]
    public void EnqueueOrThrow_WhenOverflowModeRejectNewWork_ThrowsWithModeAndWorkType()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.RejectNewWork;

        dispatcher.EnqueueOrThrow(() => { });
        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.EnqueueOrThrow(() => { }));
        Assert.Contains("RejectNewWork", error.Message);
        Assert.Contains("immediate", error.Message);
    }

    [Fact]
    public void EnqueueOrThrow_WhenOverflowModeCoalesce_ThrowsForDuplicateDelayedWork()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 1;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.Coalesce;

        Action action = () => { };
        dispatcher.EnqueueOrThrow(action, 1f);
        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.EnqueueOrThrow(action, 1f));
        Assert.Contains("Coalesce", error.Message);
        Assert.Contains("delayed", error.Message);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.CoalescedEnqueueCountForTests);
    }

    [Fact]
    public void EnqueueOrThrow_WhenOverflowModeDropOldest_DoesNotThrowAndReplacesOldest()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.MaxQueueDepthProvider = () => 2;
        UnityMainThreadDispatcher.QueueOverflowBehaviorProvider = () => UnityMainThreadDispatcher.QueueOverflowBehavior.DropOldest;

        var executed = new List<int>();
        dispatcher.EnqueueOrThrow(() => executed.Add(1));
        dispatcher.EnqueueOrThrow(() => executed.Add(2));
        dispatcher.EnqueueOrThrow(() => executed.Add(3));

        typeof(UnityMainThreadDispatcher)
            .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, null);

        Assert.Equal(new[] { 2, 3 }, executed);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.DroppedEnqueueCountForTests);
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
