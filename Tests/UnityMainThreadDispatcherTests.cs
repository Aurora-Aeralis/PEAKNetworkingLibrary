using System;
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
