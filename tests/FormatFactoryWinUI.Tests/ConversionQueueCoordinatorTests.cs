using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ConversionQueueCoordinatorTests
{
    [Fact]
    public async Task Parallelism_LimitsConcurrentOperationsAndCompletesBatchOnce()
    {
        using var coordinator = new ConversionQueueCoordinator();
        coordinator.ConfigureParallelism(2);
        var release = NewSignal();
        var twoStarted = NewSignal();
        var running = 0;
        var maximumRunning = 0;
        var started = 0;
        var completed = 0;
        var batchCompleted = 0;

        for (var index = 0; index < 6; index++)
        {
            Assert.True(coordinator.TryStart(
                Guid.NewGuid(),
                async token =>
                {
                    var current = Interlocked.Increment(ref running);
                    UpdateMaximum(ref maximumRunning, current);
                    if (Interlocked.Increment(ref started) == 2)
                    {
                        twoStarted.TrySetResult(null);
                    }

                    try
                    {
                        await release.Task.WaitAsync(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                    }
                },
                () => Task.CompletedTask,
                _ =>
                {
                    Interlocked.Increment(ref completed);
                    return Task.CompletedTask;
                },
                () =>
                {
                    Interlocked.Increment(ref batchCompleted);
                    return Task.CompletedTask;
                }));
        }

        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref started));
        Assert.Equal(2, Volatile.Read(ref maximumRunning));

        release.TrySetResult(null);
        await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(6, started);
        Assert.Equal(6, completed);
        Assert.Equal(1, batchCompleted);
        Assert.Equal(0, coordinator.ActiveOperationCount);
    }

    [Fact]
    public async Task Cancel_QueuedOperationCallsCancelledBeforeStartWithoutRunningIt()
    {
        using var coordinator = new ConversionQueueCoordinator();
        coordinator.ConfigureParallelism(1);
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var queuedCancelled = NewSignal();
        var queuedStarted = 0;
        var queuedId = Guid.NewGuid();

        Assert.True(coordinator.TryStart(
            Guid.NewGuid(),
            async token =>
            {
                firstStarted.TrySetResult(null);
                await releaseFirst.Task.WaitAsync(token);
            },
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => Task.CompletedTask));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(coordinator.TryStart(
            queuedId,
            _ =>
            {
                Interlocked.Increment(ref queuedStarted);
                return Task.CompletedTask;
            },
            () =>
            {
                queuedCancelled.TrySetResult(null);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            () => Task.CompletedTask));

        Assert.True(coordinator.Cancel(queuedId));
        await queuedCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, queuedStarted);

        releaseFirst.TrySetResult(null);
        await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, queuedStarted);
    }

    [Fact]
    public async Task Cancel_RunningOperationSignalsItsTokenAndCompletesTracking()
    {
        using var coordinator = new ConversionQueueCoordinator();
        var taskId = Guid.NewGuid();
        var started = NewSignal();
        var cancellationObserved = NewSignal();
        var cancelledBeforeStart = 0;
        var operationCompleted = NewSignal();

        Assert.True(coordinator.TryStart(
            taskId,
            async token =>
            {
                started.TrySetResult(null);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    if (token.IsCancellationRequested)
                    {
                        cancellationObserved.TrySetResult(null);
                    }
                }
            },
            () =>
            {
                Interlocked.Increment(ref cancelledBeforeStart);
                return Task.CompletedTask;
            },
            _ =>
            {
                operationCompleted.TrySetResult(null);
                return Task.CompletedTask;
            },
            () => Task.CompletedTask));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.Cancel(taskId));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await operationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, cancelledBeforeStart);
        Assert.False(coordinator.HasActiveOperations);
    }

    [Fact]
    public async Task RequestRemoval_CancelsAndPublishesDelayedRemovalAfterOperation()
    {
        using var coordinator = new ConversionQueueCoordinator();
        var taskId = Guid.NewGuid();
        var started = NewSignal();
        var completion = new TaskCompletionSource<ConversionQueueOperationCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(coordinator.TryStart(
            taskId,
            async token =>
            {
                started.TrySetResult(null);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            () => Task.CompletedTask,
            result =>
            {
                completion.TrySetResult(result);
                return Task.CompletedTask;
            },
            () => Task.CompletedTask));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.RequestRemoval(taskId));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(taskId, result.TaskId);
        Assert.True(result.RemoveRequested);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task StopAndWait_CancelsAllWaitsForCallbacksAndRejectsNewWork()
    {
        using var coordinator = new ConversionQueueCoordinator();
        coordinator.ConfigureParallelism(2);
        var bothStarted = NewSignal();
        var bothCallbacksEntered = NewSignal();
        var allowCallbacks = NewSignal();
        var started = 0;
        var callbackCount = 0;
        var batchCount = 0;

        for (var index = 0; index < 2; index++)
        {
            Assert.True(coordinator.TryStart(
                Guid.NewGuid(),
                async token =>
                {
                    if (Interlocked.Increment(ref started) == 2)
                    {
                        bothStarted.TrySetResult(null);
                    }

                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                },
                () => Task.CompletedTask,
                async _ =>
                {
                    if (Interlocked.Increment(ref callbackCount) == 2)
                    {
                        bothCallbacksEntered.TrySetResult(null);
                    }

                    await allowCallbacks.Task;
                },
                () =>
                {
                    Interlocked.Increment(ref batchCount);
                    return Task.CompletedTask;
                }));
        }

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = coordinator.StopAndWaitAsync();
        await bothCallbacksEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        Assert.False(coordinator.TryStart(
            Guid.NewGuid(),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => Task.CompletedTask));

        allowCallbacks.TrySetResult(null);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, callbackCount);
        Assert.Equal(1, batchCount);
        Assert.False(coordinator.HasActiveOperations);
    }

    [Fact]
    public async Task CompletionCallback_CanRestartTheSameTaskAndKeepTheBatchOpen()
    {
        using var coordinator = new ConversionQueueCoordinator();
        var taskId = Guid.NewGuid();
        var resumed = NewSignal();
        var releaseResumed = NewSignal();
        var restartAccepted = false;
        var completed = 0;
        var batches = 0;
        Task BatchCompleted()
        {
            Interlocked.Increment(ref batches);
            return Task.CompletedTask;
        }

        Assert.True(coordinator.TryStart(
            taskId,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref completed);
                restartAccepted = coordinator.TryStart(
                    taskId,
                    async token =>
                    {
                        resumed.TrySetResult(null);
                        await releaseResumed.Task.WaitAsync(token);
                    },
                    () => Task.CompletedTask,
                    _ =>
                    {
                        Interlocked.Increment(ref completed);
                        return Task.CompletedTask;
                    },
                    BatchCompleted);
                return Task.CompletedTask;
            },
            BatchCompleted));

        Assert.True(restartAccepted);
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.IsActive(taskId));
        Assert.Equal(0, batches);
        Assert.False(coordinator.TryStart(taskId, _ => Task.CompletedTask,
            () => Task.CompletedTask, _ => Task.CompletedTask, BatchCompleted));

        releaseResumed.TrySetResult(null);
        await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, completed);
        Assert.Equal(1, batches);
        Assert.False(coordinator.HasActiveOperations);
    }

    [Fact]
    public async Task RestartedTask_WaitForAllIncludesThePreviousCompletionCallback()
    {
        using var coordinator = new ConversionQueueCoordinator();
        var taskId = Guid.NewGuid();
        var previousCallbackEntered = NewSignal();
        var releasePreviousCallback = NewSignal();
        var restartAccepted = false;
        var batches = 0;
        Task BatchCompleted()
        {
            Interlocked.Increment(ref batches);
            return Task.CompletedTask;
        }

        Assert.True(coordinator.TryStart(
            taskId,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            async _ =>
            {
                restartAccepted = coordinator.TryStart(taskId, _ => Task.CompletedTask,
                    () => Task.CompletedTask, _ => Task.CompletedTask, BatchCompleted);
                previousCallbackEntered.TrySetResult(null);
                await releasePreviousCallback.Task;
            },
            BatchCompleted));

        try
        {
            await previousCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(restartAccepted);
            Assert.False(coordinator.IsActive(taskId));
            Assert.Equal(1, coordinator.ActiveOperationCount);
            Assert.Equal(0, batches);
            Assert.False(coordinator.WaitForAllAsync().IsCompleted);
        }
        finally
        {
            releasePreviousCallback.TrySetResult(null);
            await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, batches);
        Assert.False(coordinator.HasActiveOperations);
    }

    [Fact]
    public async Task RestartedTask_RequestRemovalCancelsOnlyTheCurrentOperation()
    {
        using var coordinator = new ConversionQueueCoordinator();
        var taskId = Guid.NewGuid();
        var currentStarted = NewSignal();
        var completion = new TaskCompletionSource<ConversionQueueOperationCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restartAccepted = false;
        var previousRemoveRequested = false;

        Assert.True(coordinator.TryStart(
            taskId,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            previous =>
            {
                previousRemoveRequested = previous.RemoveRequested;
                restartAccepted = coordinator.TryStart(
                    taskId,
                    async token =>
                    {
                        currentStarted.TrySetResult(null);
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    },
                    () => Task.CompletedTask,
                    result =>
                    {
                        completion.TrySetResult(result);
                        return Task.CompletedTask;
                    },
                    () => Task.CompletedTask);
                return Task.CompletedTask;
            },
            () => Task.CompletedTask));

        Assert.True(restartAccepted);
        await currentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.RequestRemoval(taskId));
        var current = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(previousRemoveRequested);
        Assert.True(current.RemoveRequested);
        Assert.Null(current.Error);
        Assert.False(coordinator.HasActiveOperations);
    }

    [Fact]
    public async Task ReorderPublicationCanCancelItsHeadAndIncreaseCapacityWithoutStartingIt()
    {
        using var queue = new ConversionQueueCoordinator(() => ConversionResourceSnapshot.Unknown);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var started = ids.Select(_ => NewSignal()).ToArray();
        var cancelled = NewSignal();
        var release = NewSignal();
        try
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var index = i;
                Assert.True(queue.TryStart(ids[i], async token =>
                {
                    started[index].TrySetResult(null);
                    await release.Task.WaitAsync(token);
                }, () => { if (index == 3) cancelled.TrySetResult(null); return Task.CompletedTask; },
                _ => Task.CompletedTask, () => Task.CompletedTask));
            }
            await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(queue.TryReorderPendingOperations([ids[3], ids[2], ids[1]], () =>
            {
                queue.ConfigureParallelism(3);
                Assert.True(queue.Cancel(ids[3]));
                Assert.False(started[1].Task.IsCompleted);
                Assert.False(started[2].Task.IsCompleted);
                Assert.False(started[3].Task.IsCompleted);
            }));
            await Task.WhenAll(cancelled.Task, started[1].Task, started[2].Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(started[3].Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult(null);
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FailedOrderPublicationLeavesThePreviousFifoInPlace()
    {
        using var queue = new ConversionQueueCoordinator(() => ConversionResourceSnapshot.Unknown);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var first = NewSignal();
        var release = NewSignal();
        var order = new List<Guid>();
        void Start(Guid id) => Assert.True(queue.TryStart(id, async token =>
        {
            order.Add(id);
            if (id == ids[0]) { first.TrySetResult(null); await release.Task.WaitAsync(token); }
        }, () => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask));

        Start(ids[0]);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Start(ids[1]);
        Start(ids[2]);
        try
        {
            Assert.Throws<InvalidOperationException>(() => queue.TryReorderPendingOperations([ids[2], ids[1]], () =>
            {
                Start(ids[3]);
                throw new InvalidOperationException("Publication failed");
            }));
        }
        finally
        {
            release.TrySetResult(null);
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(ids, order);
    }

    private static TaskCompletionSource<object?> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
