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

        for (var i = 0; i < 6; i++) dispatcher.Enqueue(() => { });

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
        dispatcher.Enqueue(() => executed.Add(1));
        dispatcher.Enqueue(() => executed.Add(2));
        dispatcher.Enqueue(() => executed.Add(3));

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

        dispatcher.Enqueue(() => { });
        var delayed = (System.Collections.IEnumerator)typeof(UnityMainThreadDispatcher)
            .GetMethod("EnqueueDelayed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, new object[] { (Action)(() => { }), 0f })!;

        Assert.True(delayed.MoveNext());
        Assert.False(delayed.MoveNext());

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.RejectedEnqueueCountForTests);
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
}
