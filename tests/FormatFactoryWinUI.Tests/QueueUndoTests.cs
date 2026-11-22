using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueUndoTests
{
    [Fact]
    public async Task RemovedRows_RestoreOriginalIdentityOrderParametersAndPersistence()
    {
        var persistence = new MemoryPersistence();
        var released = new List<MediaTask>();
        using var session = Session(persistence, released);
        var first = Model("first.mp4");
        var second = Model("second.mp4");
        var third = Model("third.mp4");
        var fourth = Model("fourth.mp4");
        second.Options["custom.option"] = "keep";
        second.SelectedAudioTrack = 2;
        foreach (var task in new[] { first, second, third, fourth }) session.AddTask(task);

        Assert.Equal([second, fourth], session.RemoveTasksWithUndo([fourth.Id, second.Id, fourth.Id]));
        Assert.Equal([first, third], session.Tasks);
        Assert.Empty(released);
        var added = Model("new.mp4");
        session.AddTask(added);
        Assert.Equal(2, session.UndoLastAction());
        Assert.Equal([first, second, third, fourth, added], session.Tasks);
        Assert.Same(second, session.Tasks[1]);
        Assert.Equal("keep", second.Options["custom.option"]);
        Assert.Equal(2, second.SelectedAudioTrack);
        Assert.Null(session.UndoState);
        Assert.Equal(0, session.UndoLastAction());
        await session.SaveAsync();
        Assert.Equal(session.Tasks.Select(task => task.Id), persistence.LastSaved.Select(item => item.TaskId));
        Assert.Empty(released);
    }

    [Fact]
    public void Removal_ReplacementExpiryAndDisposeReleaseOnlyRemovedImportsOnce()
    {
        var released = new List<MediaTask>();
        var session = Session(new(), released);
        var first = Model("first.mp4");
        var second = Model("second.mp4");
        var retained = Model("retained.mp4");
        foreach (var task in new[] { first, second, retained }) session.AddTask(task);
        session.RemoveTasksWithUndo([first.Id]);
        Assert.Empty(released);
        session.RemoveTasksWithUndo([second.Id]);
        Assert.Equal([first], released);
        session.ExpireUndo(session.UndoState!.ExpiresAtUtc);
        Assert.Equal([first, second], released);
        Assert.Equal(0, session.UndoLastAction());
        session.RemoveTasksWithUndo([retained.Id]);
        session.Dispose();
        session.Dispose();
        Assert.Equal([first, second, retained], released);
    }

    [Fact]
    public async Task Removal_DoesNotFinalizeIncompleteMultiOutputGroupUntilCommitted()
    {
        var output = Path.GetTempFileName();
        try
        {
            using var session = Session(new(), []);
            var groupId = Guid.NewGuid();
            var completed = Model("input.mp4");
            var pending = Model("input.mp4");
            foreach (var task in new[] { completed, pending })
            {
                task.OutputGroupId = groupId;
                task.OutputExpectedCount = 2;
                session.AddTask(task);
            }
            completed.State = ConversionState.Completed;
            completed.OutputHistoryRecorded = true;
            completed.OutputPath = output;
            pending.OutputConfigurationId = Guid.NewGuid();
            pending.OutputOrder = 1;
            pending.OutputLabel = "Audio";

            session.RemoveTasksWithUndo([pending.Id]);
            Assert.Equal(2, completed.OutputExpectedCount);
            Assert.True(session.HasPendingRemovalUndo(groupId));
            Assert.False(await session.TryFinalizeManualOutputGroupAsync(groupId));
            Assert.False(completed.OutputGroupFinalized);
            Assert.Equal(1, session.UndoLastAction());
            Assert.False(session.HasPendingRemovalUndo(groupId));
            Assert.Equal([completed, pending], session.GetOutputGroup(groupId));
            Assert.Equal("Audio", pending.OutputLabel);
            Assert.Equal(2, pending.OutputExpectedCount);

            session.RemoveTasksWithUndo([pending.Id]);
            session.DiscardUndo(finalizeGroups: false);
            Assert.Equal(1, completed.OutputExpectedCount);
            Assert.True(await session.TryFinalizeManualOutputGroupAsync(groupId));
            Assert.True(completed.OutputGroupFinalized);
            Assert.True(File.Exists(output));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task UndoCompletedMember_FinalizesGroupWhoseOtherOutputCompletedDuringRemovalHold()
    {
        var firstOutput = Path.GetTempFileName();
        var secondOutput = Path.GetTempFileName();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new QueueSession(new MemoryPersistence(),
            (_, _) => Task.FromResult(new MediaImportItemResult("", null, null)),
            async (_, _, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new ConversionWorkflowResult(ConversionOutcome.Completed,
                    new(true, false, [new ConversionArtifact("result", "mp4", secondOutput)], "completed"), "test");
            }, (_, _) => Task.CompletedTask);
        try
        {
            var completed = Model("input.mp4");
            var running = Model("input.mp4");
            var groupId = Guid.NewGuid();
            foreach (var task in new[] { completed, running })
            {
                task.OutputGroupId = groupId;
                task.OutputExpectedCount = 2;
                session.AddTask(task);
            }
            completed.State = ConversionState.Completed;
            completed.OutputHistoryRecorded = true;
            completed.OutputPath = firstOutput;
            Assert.True(session.TryStart(running, completed: async _ =>
            {
                await session.TryFinalizeManualOutputGroupAsync(groupId);
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal([completed], session.RemoveTasksWithUndo([completed.Id]));
            release.TrySetResult();
            await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ConversionState.Completed, running.State);
            Assert.True(running.OutputHistoryRecorded);
            Assert.False(running.OutputGroupFinalized);

            Assert.Equal(1, session.UndoLastAction());
            Assert.Equal([completed, running], session.GetOutputGroup(groupId));
            Assert.All(session.Tasks, task => Assert.True(task.OutputGroupFinalized));
        }
        finally
        {
            release.TrySetResult();
            await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(firstOutput);
            File.Delete(secondOutput);
        }
    }

    [Fact]
    public void BatchUndo_RestoresCustomEngineAndOnlyTheRouteFieldsItChanged()
    {
        using var session = Session(new(), []);
        var first = Model("first.mp4");
        first.EngineId = "custom-engine";
        first.Options["quality"] = "89";
        first.SelectedAudioTrack = 3;
        session.AddTask(first);
        Assert.Equal(1, session.ApplyBatchOptionsWithUndo([first.Id], "MP3", "quality"));
        Assert.Equal("mp3", first.TargetFormatId);
        Assert.Equal("quality", first.ConversionMode);
        Assert.Equal(1, session.UndoLastAction());
        Assert.Equal("MP4", first.TargetFormat);
        Assert.Equal("mp4", first.TargetFormatId);
        Assert.Equal("custom-engine", first.EngineId);
        Assert.Equal("balanced", first.ConversionMode);
        Assert.Equal("89", first.Options["quality"]);
        Assert.Equal(3, first.SelectedAudioTrack);
    }

    [Fact]
    public void BatchUndo_SkipsLaterEditsSourceRelocationAndExecutionWithoutOverwritingThem()
    {
        using var session = Session(new(), []);
        var tasks = Enumerable.Range(0, 5).Select(index => Model($"input-{index}.mp4")).ToArray();
        foreach (var task in tasks) session.AddTask(task);
        Assert.Equal(5, session.ApplyBatchOptionsWithUndo(tasks.Select(task => task.Id), "MP3", "fast"));
        tasks[0].ConversionMode = "quality";
        tasks[1].Options["custom.option"] = "later change";
        tasks[2].InputPath = "relocated.mp4";
        // A completed attempt can return to Waiting after a retry/reset; the
        // attempt stamp still prevents reverting its later execution settings.
        tasks[3].LastAttemptAtUtc = DateTimeOffset.UtcNow;

        Assert.Equal(1, session.UndoLastAction());
        Assert.All(tasks.Take(4), task => Assert.Equal("mp3", task.TargetFormatId));
        Assert.Equal("quality", tasks[0].ConversionMode);
        Assert.Equal("later change", tasks[1].Options["custom.option"]);
        Assert.Equal("relocated.mp4", tasks[2].InputPath);
        Assert.Equal("mp4", tasks[4].TargetFormatId);
        Assert.Equal("balanced", tasks[4].ConversionMode);
    }

    [Fact]
    public async Task UndoAndRemoval_DoNotTouchScheduledOrRunningWorkOrAutomationOwnership()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new QueueSession(new MemoryPersistence(), (_, _) => Task.FromResult(new MediaImportItemResult("", null, null)),
            async (_, _, _) => { entered.TrySetResult(); await release.Task; return Success(); }, (_, _) => Task.CompletedTask);
        var running = Model("running.mp4");
        var scheduled = Model("scheduled.mp4");
        var automatic = Model("automatic.mp4");
        automatic.Options["automation.ruleSnapshot"] = "owned";
        foreach (var task in new[] { running, scheduled, automatic }) session.AddTask(task);
        Assert.Equal(3, session.ApplyBatchOptionsWithUndo([running.Id, scheduled.Id, automatic.Id], "MP3"));
        Assert.True(session.TryStart(running));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.TryStart(scheduled));
        try
        {
            Assert.Empty(session.RemoveTasksWithUndo([running.Id, scheduled.Id, automatic.Id]));
            Assert.Equal(1, session.UndoLastAction());
            Assert.Equal("mp3", running.TargetFormatId);
            Assert.Equal("mp3", scheduled.TargetFormatId);
            Assert.Equal("mp4", automatic.TargetFormatId);
            Assert.Equal("owned", automatic.Options["automation.ruleSnapshot"]);
            Assert.Equal(3, session.Tasks.Count);
        }
        finally
        {
            release.TrySetResult();
            await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Removal_SkipsUnfinishedRestoreProbe()
    {
        var source = Model("restored.mp4");
        var persistence = new MemoryPersistence([QueueStore.ToPersistedItem(source)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<MediaImportItemResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new QueueSession(persistence, async (_, _) => { entered.TrySetResult(); return await release.Task; },
            (_, _, _) => Task.FromResult(Success()), (_, _) => Task.CompletedTask);
        var restore = session.RestoreAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(session.RemoveTasksWithUndo([source.Id]));
        Assert.Null(session.UndoState);
        release.TrySetResult(new(source.InputPath, null, new IOException("missing")));
        await restore;
        Assert.Single(session.RemoveTasksWithUndo([source.Id]));
        Assert.Equal(1, session.UndoLastAction());
        Assert.Equal(ConversionState.Blocked, Assert.Single(session.Tasks).State);
    }

    [Fact]
    public void NoOpOrInvalidBatchEdit_PreservesExistingUndo()
    {
        using var session = Session(new(), []);
        var removed = Model("removed.mp4");
        var retained = Model("retained.mp4");
        session.AddTask(removed);
        session.AddTask(retained);
        session.RemoveTasksWithUndo([removed.Id]);
        Assert.Equal(0, session.ApplyBatchOptionsWithUndo([retained.Id], "MP4"));
        Assert.Throws<ArgumentException>(() => session.ApplyBatchOptionsWithUndo([retained.Id], conversionMode: "invalid"));
        Assert.Equal(QueueUndoKind.RemoveTasks, session.UndoState!.Kind);
        Assert.Equal(1, session.UndoLastAction());
        Assert.Same(removed, session.Tasks[0]);
    }

    [Fact]
    public async Task Shutdown_ReleasesHeldRemovalAndPersistsRemainingGroupCount()
    {
        var released = new List<MediaTask>();
        var persistence = new MemoryPersistence();
        using var session = Session(persistence, released);
        var first = Model("input.mp4");
        var second = Model("input.mp4");
        first.OutputGroupId = second.OutputGroupId = Guid.NewGuid();
        first.OutputExpectedCount = second.OutputExpectedCount = 2;
        session.AddTask(first);
        session.AddTask(second);
        session.RemoveTasksWithUndo([first.Id]);
        Assert.True((await session.ShutdownAsync()).CanClose);
        Assert.Equal([first], released);
        Assert.Null(session.UndoState);
        Assert.Equal(1, Assert.Single(persistence.LastSaved).OutputExpectedCount);
    }

    [Fact]
    public async Task WaitingAdmissionRemoval_CanBeUndoneWithoutCancellingRunningWorkOrStartingTheRemovedTask()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new List<Guid>();
        var running = Model("running.mp4");
        var waiting = Model("waiting.mp4");
        using var session = new QueueSession(new MemoryPersistence(), (_, _) => Task.FromResult(new MediaImportItemResult("", null, null)),
            async (task, _, token) =>
            {
                executed.Add(task.Id);
                if (task == running) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
                return Success();
            }, (_, _) => Task.CompletedTask);
        session.AddTask(running);
        session.AddTask(waiting);
        Assert.True(session.TryStart(running));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.TryStart(waiting));
        try
        {
            Assert.Equal([waiting], await session.RemoveTasksWithUndoAsync([running.Id, waiting.Id]));
            Assert.True(session.Coordinator.IsActive(running.Id));
            Assert.False(session.Coordinator.IsActive(waiting.Id));
            Assert.Equal(ConversionState.Waiting, waiting.State);
            Assert.Equal(1, session.UndoLastAction());
            Assert.Equal([running, waiting], session.Tasks);
            release.TrySetResult();
            await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([running.Id], executed);
            Assert.True(session.TryStart(waiting));
            await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([running.Id, waiting.Id], executed);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void DetailedParameterUndo_RestoresAllSettingsAndRollsBackFailedEdits()
    {
        using var session = Session(new(), []);
        var first = Model("first.mp4");
        var second = Model("second.mp4");
        first.Options["custom"] = "original";
        session.AddTask(first);
        session.AddTask(second);
        var originalAudio = first.SelectedAudioTrack;
        Assert.Equal(2, session.ApplyTaskOptionsWithUndo([first.Id, second.Id], task =>
        {
            task.Options.Clear();
            task.Options["custom"] = "changed";
            task.SelectedAudioTrack = 4;
            task.ParametersVersion = 2;
            task.ConversionMode = "quality";
        }));
        Assert.Equal(2, session.UndoLastAction());
        Assert.Equal("original", first.Options["custom"]);
        Assert.Empty(second.Options);
        Assert.Equal(originalAudio, first.SelectedAudioTrack);
        Assert.Equal("balanced", first.ConversionMode);
        Assert.Throws<InvalidOperationException>(() => session.ApplyTaskOptionsWithUndo([first.Id, second.Id], task =>
        {
            task.Options["custom"] = "partial";
            if (task == second) throw new InvalidOperationException("Cannot apply settings");
        }));
        Assert.Equal("original", first.Options["custom"]);
        Assert.Empty(second.Options);
        Assert.Null(session.UndoState);
    }

    [Fact]
    public void ConsecutiveRemovals_InSameOutputGroupRetainTheUndoMemberInExpectedCount()
    {
        using var session = Session(new(), []);
        var group = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 3).Select(_ => Model("group.mp4")).ToArray();
        foreach (var task in tasks)
        {
            task.OutputGroupId = group;
            task.OutputExpectedCount = 3;
            session.AddTask(task);
        }
        session.RemoveTasksWithUndo([tasks[0].Id]);
        session.RemoveTasksWithUndo([tasks[1].Id]);
        Assert.Equal(2, tasks[2].OutputExpectedCount);
        Assert.True(session.HasPendingRemovalUndo(group));
        Assert.Equal(1, session.UndoLastAction());
        Assert.Equal([tasks[1], tasks[2]], session.Tasks);
        Assert.All(session.Tasks, task => Assert.Equal(2, task.OutputExpectedCount));
    }

    private static QueueSession Session(MemoryPersistence persistence, List<MediaTask> released) =>
        new(persistence, (_, _) => Task.FromResult(new MediaImportItemResult("", null, null)),
            (_, _, _) => Task.FromResult(Success()), (_, _) => Task.CompletedTask, releaseImport: released.Add);

    private static MediaTask Model(string path)
    {
        var task = new MediaTask { InputPath = path, ContentKind = ContentKind.Video,
            SourceFormatId = "mp4", TargetFormat = "MP4", ConversionMode = "balanced" };
        task.AvailableFormats.Add("MP4");
        task.AvailableFormats.Add("MP3");
        return task;
    }

    private static ConversionWorkflowResult Success() =>
        new(ConversionOutcome.Completed, new(true, false, [new ConversionArtifact("result", "mp4", "output.mp4")], "completed"), "test");

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
