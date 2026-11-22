using System.Collections.Concurrent;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueOrderingTests
{
    [Fact]
    public async Task PriorityAndMultiselectMove_ChangeActualScheduledExecutionAndPersistOrder()
    {
        var persistence = new MemoryPersistence();
        var release = Signal();
        var started = Signal();
        var executions = new ConcurrentQueue<Guid>();
        var tasks = Enumerable.Range(0, 5).Select(index => Model($"{index}.mp4")).ToArray();
        using var session = Session(persistence, async (task, _, token) =>
        {
            executions.Enqueue(task.Id);
            if (task.Id == tasks[0].Id) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return Success();
        });
        foreach (var task in tasks) session.AddTask(task);
        foreach (var task in tasks) Assert.True(session.TryStart(task));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(session.PrioritizePendingTask(tasks[4].Id) > 0);
            Assert.True(session.MovePendingTasks([tasks[2].Id, tasks[3].Id, tasks[2].Id], tasks[4].Id) > 0);
            var expected = new[] { tasks[0].Id, tasks[2].Id, tasks[3].Id, tasks[4].Id, tasks[1].Id };
            Assert.Equal(expected, session.Tasks.Select(task => task.Id));
            await session.SaveAsync();
            Assert.Equal(expected, persistence.LastSaved.Select(task => task.TaskId));
            Assert.False(session.CanReorderTask(tasks[0].Id));
            release.TrySetResult();
            await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, executions);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task NewlyScheduledUrgentTask_SynchronizesAdmissionEvenWhenDisplayOrderIsUnchanged()
    {
        var release = Signal();
        var started = Signal();
        var executions = new ConcurrentQueue<Guid>();
        var running = Model("running.mp4");
        var waiting = Model("waiting.mp4");
        var urgent = Model("urgent.mp4");
        using var session = Session(new(), async (task, _, token) =>
        {
            executions.Enqueue(task.Id);
            if (task == running) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return Success();
        });
        foreach (var task in new[] { running, waiting, urgent }) session.AddTask(task);
        Assert.True(session.TryStart(running));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.TryStart(waiting));
        try
        {
            Assert.True(session.PrioritizePendingTask(urgent.Id) > 0);
            Assert.True(session.TryStart(urgent));
            Assert.Equal(0, session.PrioritizePendingTask(urgent.Id));
            release.TrySetResult();
            await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([running.Id, urgent.Id, waiting.Id], executions);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledUrgentTask_DoesNotExecuteOrConsumeAParallelSlot()
    {
        using var queue = new ConversionQueueCoordinator();
        var tasks = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var release = Signal();
        var cancelled = Signal();
        var executions = new ConcurrentQueue<Guid>();
        foreach (var id in tasks)
            Assert.True(queue.TryStart(id, async token =>
            {
                executions.Enqueue(id);
                if (id == tasks[0]) await release.Task.WaitAsync(token);
            }, () => { if (id == tasks[3]) cancelled.TrySetResult(); return Task.CompletedTask; },
                _ => Task.CompletedTask, () => Task.CompletedTask));
        try
        {
            Assert.True(queue.TryReorderPendingOperations([tasks[3], tasks[2], tasks[1]], () => { }));
            Assert.True(queue.Cancel(tasks[3]));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(queue.CanReorder(tasks[0]));
            release.TrySetResult();
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([tasks[0], tasks[2], tasks[1]], executions);
            Assert.False(queue.HasActiveOperations);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ReorderingHonorsDynamicParallelismAndNeverMovesAdmittedOperations()
    {
        using var queue = new ConversionQueueCoordinator();
        queue.ConfigureParallelism(2);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var started = ids.Select(_ => Signal()).ToArray();
        var release = ids.Select(_ => Signal()).ToArray();
        var finished = ids.Select(_ => Signal()).ToArray();
        var active = 0;
        var maximum = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            var index = i;
            Assert.True(queue.TryStart(ids[index], async token =>
            {
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                started[index].TrySetResult();
                try { await release[index].Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active); }
            }, () => Task.CompletedTask, _ => { finished[index].TrySetResult(); return Task.CompletedTask; },
                () => Task.CompletedTask));
        }
        try
        {
            await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            var published = false;
            Assert.False(queue.TryReorderPendingOperations([ids[1], ids[4]], () => published = true));
            Assert.False(published);
            Assert.True(queue.TryReorderPendingOperations([ids[4], ids[3], ids[2]], () => { }));
            queue.ConfigureParallelism(1);
            release[0].TrySetResult();
            await finished[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(started[4].Task.IsCompleted);
            release[1].TrySetResult();
            await started[4].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(started[2].Task.IsCompleted);
            queue.ConfigureParallelism(2);
            await started[3].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(started[2].Task.IsCompleted);
        }
        finally
        {
            foreach (var signal in release) signal.TrySetResult();
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, maximum);
    }

    [Fact]
    public void Priority_MovesPendingOutputGroupWithSelectedOutputFirst_AndLeavesOtherStatesFixed()
    {
        var persistence = new MemoryPersistence();
        using var session = Session(persistence);
        var group = Guid.NewGuid();
        var first = Model("first.mp4");
        var running = Model("group.mp4", ConversionState.Running, group, 0);
        var sibling = Model("group.mp4", group: group, order: 1);
        var paused = Model("group.mp4", ConversionState.Paused, group, 2);
        var urgent = Model("group.mp4", group: group, order: 3);
        var completed = Model("group.mp4", ConversionState.Completed, group, 4);
        foreach (var task in new[] { first, running, sibling, paused, urgent, completed }) session.AddTask(task);
        Assert.True(session.PrioritizePendingTask(urgent.Id) > 0);
        Assert.Equal([urgent.Id, running.Id, sibling.Id, paused.Id, first.Id, completed.Id], session.Tasks.Select(task => task.Id));
        Assert.Equal(3, urgent.OutputOrder);
        Assert.Equal(1, sibling.OutputOrder);
        Assert.Equal(group, urgent.OutputGroupId);
        Assert.Equal("preserved", urgent.Options["custom"]);
        foreach (var task in new[] { running, paused, completed })
        {
            Assert.Equal(0, session.PrioritizePendingTask(task.Id));
            Assert.Equal(0, session.MovePendingTasks([urgent.Id], task.Id));
            Assert.Equal(0, session.MovePendingTasks([urgent.Id, task.Id], first.Id));
        }
    }

    [Fact]
    public void CrossGroupDrop_MovesWholePendingSourceGroupPastTargetGroup_WhileWithinGroupMoveIsLocal()
    {
        using var session = Session(new MemoryPersistence());
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var a = Model("a", group: groupA);
        var b = Model("b", group: groupA, order: 1);
        var c = Model("c", group: groupB);
        var d = Model("d", group: groupB, order: 1);
        foreach (var task in new[] { a, b, c, d }) session.AddTask(task);
        Assert.True(session.MovePendingTasks([b.Id], c.Id, insertAfter: true) > 0);
        Assert.Equal([c.Id, d.Id, b.Id, a.Id], session.Tasks.Select(task => task.Id));
        Assert.True(session.MovePendingTasks([a.Id], b.Id) > 0);
        Assert.Equal([c.Id, d.Id, a.Id, b.Id], session.Tasks.Select(task => task.Id));
        Assert.Equal(0, session.MovePendingTasks([a.Id], a.Id));
        Assert.Equal(0, session.MovePendingTasks([Guid.NewGuid()], b.Id));
    }

    private static QueueSession Session(MemoryPersistence persistence,
        Func<MediaTask, IProgress<ConversionExecutionProgress>?, CancellationToken, Task<ConversionWorkflowResult>>? execute = null) =>
        new(persistence, (_, _) => Task.FromResult(new MediaImportItemResult("", null, null)),
            execute ?? ((_, _, _) => Task.FromResult(Success())), (_, _) => Task.CompletedTask);

    private static ConversionWorkflowResult Success() =>
        new(ConversionOutcome.Completed, new(true, false, [new ConversionArtifact("result", "mp4", "output.mp4")], "completed"), "test");

    private static MediaTask Model(string path, ConversionState state = ConversionState.Waiting, Guid? group = null, int order = 0)
    {
        var task = new MediaTask
        {
            InputPath = path, SourceFormatId = "mp4", TargetFormatId = "mp4", State = state,
            OutputGroupId = group, OutputOrder = order
        };
        task.Options["custom"] = "preserved";
        return task;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class MemoryPersistence : IQueuePersistence
    {
        public PersistedQueueItem[] LastSaved { get; private set; } = [];
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Missing, []));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
        {
            LastSaved = tasks.Select(QueueStore.ToPersistedItem).ToArray();
            return Task.CompletedTask;
        }
    }
}
