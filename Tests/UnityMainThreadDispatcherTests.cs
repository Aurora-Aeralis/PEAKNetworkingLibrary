using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
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
    public void Enqueue_UnderLimit_IsAccepted()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        UnityMainThreadDispatcher.TestHooks.MaxQueuedActionsForTests = 2;

        dispatcher.Enqueue(() => { });

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.DroppedActionCountForTests);
    }

    [Fact]
    public void Enqueue_AtLimit_DropsAdditionalActions()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        var warnings = new List<string>();
        UnityMainThreadDispatcher.TestHooks.MaxQueuedActionsForTests = 2;
        UnityMainThreadDispatcher.TestHooks.SetWarningLoggerForTests(message => warnings.Add(message));

        dispatcher.Enqueue(() => { });
        dispatcher.Enqueue(() => { });
        dispatcher.Enqueue(() => { });

        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Equal(0, UnityMainThreadDispatcher.TestHooks.DroppedActionCountForTests);
        Assert.Single(warnings);
        Assert.Contains("dropped 1 queued action", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Enqueue_OverflowWarnings_AreThrottled()
    {
        var dispatcher = (UnityMainThreadDispatcher)FormatterServices.GetUninitializedObject(typeof(UnityMainThreadDispatcher));
        var warnings = new List<string>();
        UnityMainThreadDispatcher.TestHooks.MaxQueuedActionsForTests = 1;
        UnityMainThreadDispatcher.TestHooks.DropWarningThrottleForTests = TimeSpan.FromSeconds(30);
        UnityMainThreadDispatcher.TestHooks.SetWarningLoggerForTests(message => warnings.Add(message));

        dispatcher.Enqueue(() => { });
        dispatcher.Enqueue(() => { });
        dispatcher.Enqueue(() => { });
        dispatcher.Enqueue(() => { });

        Assert.Equal(1, UnityMainThreadDispatcher.TestHooks.QueueDepthForTests);
        Assert.Single(warnings);
        Assert.Contains("dropped 1 queued action", warnings[0], StringComparison.Ordinal);
        Assert.Equal(2, UnityMainThreadDispatcher.TestHooks.DroppedActionCountForTests);
    }

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
}
