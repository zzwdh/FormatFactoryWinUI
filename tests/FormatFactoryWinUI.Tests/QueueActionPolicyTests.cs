using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueActionPolicyTests
{
    [Fact]
    public void ClearPending_SelectsOnlyWaitingRowsFromMixedQueue()
    {
        var tasks = Enum.GetValues<ConversionState>().Select(state => new MediaTask { State = state }).ToArray();

        Assert.Equal([tasks.Single(task => task.State == ConversionState.Waiting).Id], QueueActionPolicy.SelectPending(tasks));
    }

    [Fact]
    public void StopAll_PreservesUnstartedAndTerminalRows()
    {
        var scheduledWaiting = new MediaTask { State = ConversionState.Waiting };
        var unstartedWaiting = new MediaTask { State = ConversionState.Waiting };
        var running = new MediaTask { State = ConversionState.Running };
        var paused = new MediaTask { State = ConversionState.Paused };
        var probing = new MediaTask { State = ConversionState.Probing };
        MediaTask[] tasks = [scheduledWaiting, unstartedWaiting, running, paused, probing,
            new() { State = ConversionState.Completed }, new() { State = ConversionState.Failed }, new() { State = ConversionState.Cancelled }];

        Assert.Equal([scheduledWaiting.Id, running.Id, paused.Id, probing.Id],
            QueueActionPolicy.SelectToStop(tasks, id => id == scheduledWaiting.Id));
        Assert.Equal(ConversionState.Waiting, unstartedWaiting.State);
    }

    [Fact]
    public async Task ClearPending_CancelsScheduledWaitingWithoutCancellingRunningWork()
    {
        using var coordinator = new ConversionQueueCoordinator();
        var running = new MediaTask { State = ConversionState.Running };
        var waiting = new MediaTask { State = ConversionState.Waiting };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningCancelled = false;
        var waitingRan = false;
        coordinator.TryStart(running.Id, async token =>
        {
            started.SetResult();
            try { await release.Task.WaitAsync(token); }
            finally { runningCancelled = token.IsCancellationRequested; }
        }, () => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.TryStart(waiting.Id, _ =>
        {
            waitingRan = true;
            return Task.CompletedTask;
        }, () => Task.CompletedTask, result =>
        {
            removed.SetResult(result.RemoveRequested);
            return Task.CompletedTask;
        }, () => Task.CompletedTask);

        try
        {
            foreach (var id in QueueActionPolicy.SelectPending([running, waiting]))
            {
                Assert.True(coordinator.RequestRemoval(id));
            }
            Assert.True(await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(coordinator.IsActive(running.Id));
            Assert.False(waitingRan);
        }
        finally
        {
            release.TrySetResult();
            await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(runningCancelled);
    }
}
