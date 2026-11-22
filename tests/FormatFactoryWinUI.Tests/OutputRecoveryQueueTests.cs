using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputRecoveryQueueTests
{
    [Fact]
    public async Task FailedCommit_RemainsFailedWithoutHistoryUntilSaveAsCompletes()
    {
        using var directory = new TemporaryDirectory();
        var task = Model(directory.Path, ConversionState.Waiting);
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var record = Preserve(recovery, task, directory.Path);
        var persistence = new MemoryPersistence();
        var history = new List<ConversionHistoryItem>();
        var executions = 0;
        using var session = CreateSession(persistence, history, (_, _, _) =>
        {
            executions++;
            return Task.FromResult(FailedCommit(record));
        });

        Assert.True(session.TryStart(task));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConversionState.Failed, task.State);
        Assert.Empty(task.OutputPath);
        Assert.Equal(record.Id, task.RecoverableOutputId);
        Assert.Equal(record.ArtifactPath, task.PreservedOutputPath);
        Assert.Empty(history);
        var failedSnapshot = Assert.Single(persistence.LastSaved);
        Assert.Equal(ConversionState.Failed, failedSnapshot.State);
        Assert.Equal(record.Id, failedSnapshot.RecoverableOutputId);

        var savesBeforeRecovery = persistence.SaveAttempts;
        var saved = await recovery.SaveAsAsync(record.Id, Path.Combine(directory.Path, "saved"));
        // Merely copying/committing does not publish a successful queue result or history.
        Assert.Equal(ConversionState.Failed, task.State);
        Assert.Empty(history);
        await session.CompleteRecoveredOutputAsync(task, saved, record);

        Assert.Equal(ConversionState.Completed, task.State);
        Assert.Equal(100, task.Progress);
        Assert.Equal(saved, task.OutputPath);
        Assert.Empty(task.LastError);
        Assert.Empty(task.BlockedReason);
        Assert.Null(task.RecoverableOutputId);
        Assert.Null(task.PreservedOutputPath);
        Assert.Equal(savesBeforeRecovery + 1, persistence.SaveAttempts);
        var completedSnapshot = Assert.Single(persistence.LastSaved);
        Assert.Equal(ConversionState.Completed, completedSnapshot.State);
        Assert.Null(completedSnapshot.RecoverableOutputId);
        Assert.Null(completedSnapshot.PreservedOutputPath);
        var completedHistory = Assert.Single(history);
        Assert.Equal(task.Id, completedHistory.TaskId);
        Assert.Equal(saved, completedHistory.OutputPath);
        Assert.Equal(1, executions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteRecoveredOutputAsync(task, saved, record));
        Assert.Single(history);
        Assert.Equal(savesBeforeRecovery + 1, persistence.SaveAttempts);
    }

    [Fact]
    public async Task MissingSourceAfterRestart_DoesNotPreventSavingValidatedOutput()
    {
        using var directory = new TemporaryDirectory();
        var sourceTask = Model(directory.Path, ConversionState.Failed);
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var record = Preserve(recovery, sourceTask, directory.Path);
        sourceTask.RecoverableOutputId = record.Id;
        sourceTask.PreservedOutputPath = record.ArtifactPath;
        sourceTask.LastError = "output commit failed";
        var persistence = new MemoryPersistence([QueueStore.ToPersistedItem(sourceTask)]);
        var history = new List<ConversionHistoryItem>();
        var imports = 0;
        var executions = 0;
        using var session = CreateSession(persistence, history, (_, _, _) =>
        {
            executions++;
            return Task.FromResult(FailedCommit(record));
        }, (item, _) =>
        {
            imports++;
            return Task.FromResult(new MediaImportItemResult(item.InputPath, null, new FileNotFoundException("source removed")));
        });

        await session.RestoreAsync();
        var restored = Assert.Single(session.Tasks);
        Assert.Equal(sourceTask.Id, restored.Id);
        Assert.Equal(ConversionState.Blocked, restored.State);
        Assert.Equal(ConversionState.Failed, restored.StateBeforeBlocked);
        Assert.Equal(record.Id, restored.RecoverableOutputId);
        Assert.Empty(history);
        Assert.False(File.Exists(restored.InputPath));

        var retained = new OutputRecoveryService(Path.Combine(directory.Path, "recovery")).Get(restored.Id)!;
        var output = await recovery.SaveAsAsync(retained.Id, Path.Combine(directory.Path, "saved"));
        await session.CompleteRecoveredOutputAsync(restored, output, retained);

        Assert.Equal(ConversionState.Completed, restored.State);
        Assert.Null(restored.StateBeforeBlocked);
        Assert.Empty(restored.BlockedReason);
        Assert.Equal(output, Assert.Single(history).OutputPath);
        Assert.Equal(1, imports);
        Assert.Equal(0, executions);
        Assert.Equal(ConversionState.Completed, Assert.Single(persistence.LastSaved).State);
    }

    [Fact]
    public async Task ChangedFailedTaskOptions_HistoryUsesOriginalOutputFormatModeAndEngine()
    {
        using var directory = new TemporaryDirectory();
        var task = Model(directory.Path, ConversionState.Failed);
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var record = Preserve(recovery, task, directory.Path);
        task.RecoverableOutputId = record.Id;
        task.PreservedOutputPath = record.ArtifactPath;
        var persistence = new MemoryPersistence();
        var history = new List<ConversionHistoryItem>();
        using var session = CreateSession(persistence, history);
        session.AddTask(task);

        Assert.Equal(1, session.ApplyBatchOptions([task.Id], "MP3", "fast"));
        task.EngineId = "edited-engine";
        Assert.Equal("mp3", task.TargetFormatId);
        Assert.Equal("fast", task.ConversionMode);
        var saved = await recovery.SaveAsAsync(record.Id, Path.Combine(directory.Path, "saved"));
        await session.CompleteRecoveredOutputAsync(task, saved, record);

        Assert.Equal("mp4", task.TargetFormatId);
        Assert.Equal("MP4", task.TargetFormat);
        Assert.Equal("lossless", task.ConversionMode);
        Assert.Equal("ffmpeg", task.EngineId);
        var snapshot = Assert.Single(persistence.LastSaved);
        Assert.Equal("mp4", snapshot.TargetFormatId);
        Assert.Equal("lossless", snapshot.ConversionMode);
        Assert.Equal("ffmpeg", snapshot.EngineId);
        var completed = Assert.Single(history);
        Assert.Equal("mp4", completed.TargetFormatId);
        Assert.Equal("lossless", completed.ConversionMode);
        Assert.Equal("ffmpeg", completed.EngineId);
        Assert.Equal(saved, completed.OutputPath);
    }

    [Fact]
    public async Task WrongTaskIdentityOrMissingOutput_CannotPublishSuccessOrHistory()
    {
        using var directory = new TemporaryDirectory();
        var task = Model(directory.Path, ConversionState.Failed);
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var record = Preserve(recovery, task, directory.Path);
        task.RecoverableOutputId = record.Id;
        task.PreservedOutputPath = record.ArtifactPath;
        var history = new List<ConversionHistoryItem>();
        var persistence = new MemoryPersistence();
        using var session = CreateSession(persistence, history);
        session.AddTask(task);
        var existing = Path.Combine(directory.Path, "existing.mp4");
        File.WriteAllText(existing, "existing output");

        await Assert.ThrowsAsync<InvalidDataException>(() => session.CompleteRecoveredOutputAsync(task, existing,
            record with { TaskId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.CompleteRecoveredOutputAsync(task,
            Path.Combine(directory.Path, "missing.mp4"), record));

        Assert.Equal(ConversionState.Failed, task.State);
        Assert.Empty(task.OutputPath);
        Assert.Equal(record.Id, task.RecoverableOutputId);
        Assert.Empty(history);
        Assert.Equal(0, persistence.SaveAttempts);
    }

    [Theory]
    [InlineData(ConversionState.Failed)]
    [InlineData(ConversionState.Blocked)]
    public void RecoveringOutput_ProtectsTaskFromRetryBatchEditRemovalAndCancellation(ConversionState state)
    {
        using var directory = new TemporaryDirectory();
        var task = Model(directory.Path, state);
        task.RecoverableOutputId = Guid.NewGuid().ToString("N");
        task.IsRecoveringOutput = true;
        var persistence = new MemoryPersistence();
        var history = new List<ConversionHistoryItem>();
        using var session = CreateSession(persistence, history);
        session.AddTask(task);
        using var viewModel = new MediaTaskViewModel(task);

        Assert.False(session.TryStart(task));
        Assert.Empty(session.GetRetryableTasks());
        Assert.Empty(session.GetEditableTasks([task.Id]));
        Assert.Equal(0, session.ApplyBatchOptions([task.Id], "MP3", "fast"));
        Assert.False(viewModel.CanEditConversionOptions);
        Assert.False(viewModel.CanRecoverOutput);
        session.RemoveTask(task);
        Assert.Same(task, Assert.Single(session.Tasks));
        session.Cancel(task);
        Assert.Equal(state, task.State);
        Assert.Equal("mp4", task.TargetFormatId);
        Assert.Equal("lossless", task.ConversionMode);
        Assert.Empty(history);
        Assert.Equal(0, persistence.SaveAttempts);

        task.IsRecoveringOutput = false;
        Assert.True(viewModel.CanEditConversionOptions);
        Assert.True(viewModel.CanRecoverOutput);
        Assert.Same(task, Assert.Single(session.GetEditableTasks([task.Id])));
    }

    private static QueueSession CreateSession(MemoryPersistence persistence, List<ConversionHistoryItem> history,
        Func<MediaTask, IProgress<ConversionExecutionProgress>?, CancellationToken, Task<ConversionWorkflowResult>>? execute = null,
        Func<PersistedQueueItem, CancellationToken, Task<MediaImportItemResult>>? import = null) =>
        new(persistence,
            import ?? ((item, _) => Task.FromResult(new MediaImportItemResult(item.InputPath, QueueSession.FromPersisted(item), null))),
            execute ?? ((_, _, _) => throw new InvalidOperationException("Recovery must not re-encode the source.")),
            (item, _) => { history.Add(item); return Task.CompletedTask; }, diagnostics: _ => { });

    private static ConversionWorkflowResult FailedCommit(OutputRecoveryRecord record) =>
        new(ConversionOutcome.Failed, new(false, false, [], "output commit failed")
        {
            RecoverableOutputId = record.Id, PreservedOutputPath = record.ArtifactPath
        }, "ffmpeg");

    private static OutputRecoveryRecord Preserve(OutputRecoveryService recovery, MediaTask task, string directory)
    {
        var source = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial.mp4");
        File.WriteAllText(source, "validated output");
        return recovery.PreserveValidated(task.Id, source, Path.Combine(directory, "result.mp4"), false,
            task.TargetFormatId, task.ConversionMode, task.EngineId);
    }

    private static MediaTask Model(string directory, ConversionState state)
    {
        var task = new MediaTask
        {
            InputPath = Path.Combine(directory, "missing-source.mov"), SourceFormatId = "mov",
            ContentKind = ContentKind.Video, TargetFormatId = "mp4", ConversionMode = "lossless",
            EngineId = "ffmpeg", State = state
        };
        task.AvailableFormats.Add("MP4");
        task.AvailableFormats.Add("MP3");
        return task;
    }

    private sealed class MemoryPersistence(PersistedQueueItem[]? items = null) : IQueuePersistence
    {
        public int SaveAttempts { get; private set; }
        public PersistedQueueItem[] LastSaved { get; private set; } = [];
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, items ?? []));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            LastSaved = tasks.Select(QueueStore.ToPersistedItem).ToArray();
            return Task.CompletedTask;
        }
    }
}
