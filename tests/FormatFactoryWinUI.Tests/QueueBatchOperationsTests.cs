using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueBatchOperationsTests
{
    [Fact]
    public void BatchEdit_ExcludesBusyAndCompletedTasksAndDeduplicatesSelection()
    {
        using var session = CreateSession(new MemoryPersistence());
        var tasks = Enum.GetValues<ConversionState>().Select(state => Model("input.mp4", state)).ToArray();
        foreach (var task in tasks) session.AddTask(task);

        var changed = session.ApplyBatchOptions(tasks.SelectMany(task => new[] { task.Id, task.Id }), "MP3", "quality");

        Assert.Equal(4, changed);
        foreach (var task in tasks)
        {
            var editable = task.State is ConversionState.Waiting or ConversionState.Failed or ConversionState.Cancelled or ConversionState.Blocked;
            Assert.Equal(editable ? "MP3" : "MP4", task.TargetFormat);
            Assert.Equal(editable ? "quality" : "balanced", task.ConversionMode);
            Assert.Equal("preserved", task.Options["custom"]);
        }
    }

    [Fact]
    public void BatchEdit_ValidatesCompatibilityBeforeChangingAnyParameter()
    {
        using var session = CreateSession(new MemoryPersistence());
        var supported = Model("audio.mp4", ConversionState.Waiting);
        var incompatible = Model("document.docx", ConversionState.Waiting);
        incompatible.AvailableFormats.Clear();
        incompatible.AvailableFormats.Add("PDF");
        session.AddTask(supported);
        session.AddTask(incompatible);

        Assert.Equal(1, session.ApplyBatchOptions([supported.Id, incompatible.Id], "mp3", "fast"));
        Assert.Equal("mp3", supported.TargetFormatId);
        Assert.Equal("MP4", incompatible.TargetFormat);
        Assert.Equal("balanced", incompatible.ConversionMode);
        Assert.Throws<ArgumentException>(() => session.ApplyBatchOptions([supported.Id], conversionMode: "invalid"));
        Assert.Equal("fast", supported.ConversionMode);
        Assert.Equal(0, session.ApplyBatchOptions([supported.Id]));
    }

    [Fact]
    public async Task Sort_LeavesNonwaitingRowsInPlaceAndPersistsStableOrder()
    {
        var persistence = new MemoryPersistence();
        using var session = CreateSession(persistence);
        var last = Model("z.mp4", ConversionState.Waiting, 100);
        var failed = Model("failed.mp4", ConversionState.Failed, 10);
        var first = Model("a.mp4", ConversionState.Waiting, 300);
        var running = Model("running.mp4", ConversionState.Running, 50);
        var tied = Model("m.mp4", ConversionState.Waiting, 100);
        foreach (var task in new[] { last, failed, first, running, tied }) session.AddTask(task);

        Assert.Equal(3, session.ReorderPendingTasks(QueueSortOrder.FileName));
        Assert.Equal([first.Id, failed.Id, tied.Id, running.Id, last.Id], session.Tasks.Select(task => task.Id));
        Assert.Equal(3, session.ReorderPendingTasks(QueueSortOrder.SmallestFirst));
        Assert.Equal([tied.Id, failed.Id, last.Id, running.Id, first.Id], session.Tasks.Select(task => task.Id));
        await session.SaveAsync();
        Assert.Equal(session.Tasks.Select(task => task.Id), persistence.LastSaved.Select(task => task.TaskId));
        Assert.All(persistence.LastSaved, task => Assert.Equal("preserved", task.Options["custom"]));
        Assert.Equal(0, session.ReorderPendingTasks(QueueSortOrder.SmallestFirst));
    }

    [Fact]
    public async Task ScheduledWaitingTask_IsNotEditedMovedOrRetriedWhileWaitingForAdmission()
    {
        using var coordinator = new ConversionQueueCoordinator();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningTask = Model("running.mp4", ConversionState.Running);
        var scheduledTask = Model("scheduled.mp4", ConversionState.Waiting);
        var last = Model("z.mp4", ConversionState.Waiting);
        var first = Model("a.mp4", ConversionState.Waiting);
        coordinator.TryStart(runningTask.Id, async _ => { running.TrySetResult(); await release.Task; },
            () => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.TryStart(scheduledTask.Id, _ => Task.CompletedTask,
            () => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask);
        using var session = CreateSession(new MemoryPersistence(), coordinator);
        foreach (var task in new[] { last, scheduledTask, first, runningTask }) session.AddTask(task);
        try
        {
            Assert.Empty(session.GetEditableTasks([scheduledTask.Id]));
            Assert.Equal(0, session.ApplyBatchOptions([scheduledTask.Id], "MP3", "fast"));
            Assert.Equal(2, session.ReorderPendingTasks(QueueSortOrder.FileName));
            Assert.Equal([first.Id, scheduledTask.Id, last.Id, runningTask.Id], session.Tasks.Select(task => task.Id));
            scheduledTask.State = ConversionState.Failed;
            Assert.Empty(session.GetRetryableTasks());
        }
        finally
        {
            release.TrySetResult();
            await coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Single(session.GetRetryableTasks());
    }

    [Fact]
    public async Task RecoveryPublication_IsProtectedUntilSourceProbeFinishes()
    {
        var source = Model("offline.mp4", ConversionState.Failed);
        var persistence = new MemoryPersistence([QueueStore.ToPersistedItem(source)]);
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<MediaImportItemResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new QueueSession(persistence,
            async (_, _) => { probing.TrySetResult(); return await release.Task; },
            (_, _, _) => Task.FromResult(Success()), (_, _) => Task.CompletedTask);
        var restoring = session.RestoreAsync();
        await probing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(session.GetRetryableTasks());
        Assert.Empty(session.GetEditableTasks([source.Id]));
        Assert.Equal(0, session.ApplyBatchOptions([source.Id], "MP3"));
        release.TrySetResult(new(source.InputPath, null, new IOException("offline")));
        await restoring;
        Assert.Equal(source.Id, Assert.Single(session.GetRetryableTasks()).Id);
    }

    private static QueueSession CreateSession(IQueuePersistence persistence, ConversionQueueCoordinator? coordinator = null) =>
        new(persistence, (_, _) => Task.FromResult(new MediaImportItemResult("", null, null)),
            (_, _, _) => Task.FromResult(Success()), (_, _) => Task.CompletedTask,
            coordinator: coordinator);

    private static ConversionWorkflowResult Success() =>
        new(ConversionOutcome.Completed, new(true, false, [new ConversionArtifact("result", "mp4", "output.mp4")], "completed"), "test");

    private static MediaTask Model(string path, ConversionState state, long size = 0)
    {
        var task = new MediaTask
        {
            InputPath = path, SourceFormatId = "mp4", ContentKind = ContentKind.Video,
            TargetFormat = "MP4", State = state, FileSize = size
        };
        task.AvailableFormats.Add("MP4");
        task.AvailableFormats.Add("MP3");
        task.Options["custom"] = "preserved";
        return task;
    }

    private sealed class MemoryPersistence(PersistedQueueItem[]? items = null) : IQueuePersistence
    {
        public PersistedQueueItem[] LastSaved { get; private set; } = [];
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, items ?? []));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
        {
            LastSaved = tasks.Select(QueueStore.ToPersistedItem).ToArray();
            return Task.CompletedTask;
        }
    }
}