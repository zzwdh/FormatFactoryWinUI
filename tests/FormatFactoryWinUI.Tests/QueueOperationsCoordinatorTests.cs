using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueOperationsCoordinatorTests
{
    [Fact]
    public void BatchEditPlanUsesOnlyIdleCompatibleSelectionAndCaseInsensitiveIntersection()
    {
        using var session = CreateSession();
        var first = Model("first.mp4");
        var second = Model("second.mp4", ConversionState.Failed);
        second.AvailableFormats.Clear();
        foreach (var format in new[] { "mp3", "WAV" }) second.AvailableFormats.Add(format);
        var completed = Model("finished.mp4", ConversionState.Completed);
        var recovering = Model("recovery.mp4");
        recovering.IsRecoveringOutput = true;
        foreach (var task in new[] { first, second, completed, recovering }) session.AddTask(task);
        var commands = new QueueOperationsCoordinator(session);

        var plan = commands.PrepareBatchEdit([first.Id, second.Id, first.Id, completed.Id, recovering.Id]);

        Assert.Equal([first, second], plan.EditableTasks);
        Assert.Equal(["MP3"], plan.CommonFormats);
        Assert.Equal(4, plan.SelectedIds.Length);
        Assert.Empty(commands.PrepareBatchEdit([completed.Id]).CommonFormats);
    }

    [Fact]
    public void RetryResetsFailedGroupOnlyAfterReadinessCheckAndPreservesBlockedState()
    {
        using var session = CreateSession();
        var group = Guid.NewGuid();
        var failed = Model("failed.mp4", ConversionState.Failed);
        failed.OutputGroupId = group;
        failed.OutputGroupFinalized = true;
        failed.OutputHistoryRecorded = true;
        failed.OutputPath = "old-result.mp4";
        failed.Progress = 70;
        var sibling = Model("sibling.mp4", ConversionState.Completed);
        sibling.OutputGroupId = group;
        sibling.OutputGroupFinalized = true;
        var unavailable = Model("unavailable.mp4", ConversionState.Failed);
        unavailable.OutputPath = "keep-diagnostic-output.mp4";
        var blocked = Model("blocked.mp4", ConversionState.Blocked);
        foreach (var task in new[] { failed, sibling, unavailable, blocked }) session.AddTask(task);
        var started = new List<MediaTask>();
        var commands = new QueueOperationsCoordinator(session);

        var count = commands.RetryFailed(task => task != unavailable, task => started.Add(task));

        Assert.Equal(2, count);
        Assert.Equal([failed, blocked], started);
        Assert.Equal(ConversionState.Waiting, failed.State);
        Assert.Equal(0, failed.Progress);
        Assert.Empty(failed.OutputPath);
        Assert.False(failed.OutputHistoryRecorded);
        Assert.False(failed.OutputGroupFinalized);
        Assert.False(sibling.OutputGroupFinalized);
        Assert.Equal(ConversionState.Blocked, blocked.State);
        Assert.Equal(ConversionState.Failed, unavailable.State);
        Assert.Equal("keep-diagnostic-output.mp4", unavailable.OutputPath);
    }

    [Fact]
    public void PauseTogglePausesRunningWorkBeforeResumingAnyPausedWork()
    {
        using var session = CreateSession();
        var running = Model("running.mp4", ConversionState.Running);
        var paused = Model("paused.mp4", ConversionState.Paused);
        var waiting = Model("waiting.mp4");
        foreach (var task in new[] { running, paused, waiting }) session.AddTask(task);
        var commands = new QueueOperationsCoordinator(session);
        var pausedCalls = new List<Guid>();
        var resumedCalls = new List<Guid>();
        void Pause(MediaTask task) { pausedCalls.Add(task.Id); task.State = ConversionState.Paused; }
        void Resume(MediaTask task) { resumedCalls.Add(task.Id); task.State = ConversionState.Running; }

        commands.TogglePause(Pause, Resume);
        Assert.Equal([running.Id], pausedCalls);
        Assert.Empty(resumedCalls);
        commands.TogglePause(Pause, Resume);

        Assert.Equal([running.Id, paused.Id], resumedCalls);
        Assert.Equal(ConversionState.Waiting, waiting.State);
    }

    [Fact]
    public async Task RemovalSeparatesUndoableManualRowsFromPermanentAutomaticRows()
    {
        using var session = CreateSession();
        var manual = Model("manual.mp4");
        var automatic = Model("automatic.mp4", ConversionState.Failed);
        automatic.Options["automation.ruleSnapshot"] = "{}";
        var running = Model("running.mp4", ConversionState.Running);
        var recovering = Model("recovering.mp4");
        recovering.IsRecoveringOutput = true;
        foreach (var task in new[] { manual, automatic, running, recovering }) session.AddTask(task);
        var commands = new QueueOperationsCoordinator(session);

        var result = await commands.RemoveWithUndoAsync([manual.Id, automatic.Id, running.Id, recovering.Id, manual.Id]);

        Assert.Same(manual, Assert.Single(result.RemovedWithUndo));
        Assert.Same(automatic, Assert.Single(result.AutomaticRemovals));
        Assert.Equal(4, result.CandidateCount);
        Assert.Equal(2, result.RemovedCount);
        Assert.DoesNotContain(manual, session.Tasks);
        Assert.Contains(automatic, session.Tasks);
        Assert.Contains(running, session.Tasks);
        Assert.Contains(recovering, session.Tasks);
        Assert.Equal(QueueRemovalAction.None, commands.ResolveRemoval(recovering));
        Assert.Equal(QueueRemovalAction.Cancel, commands.ResolveRemoval(running));
        Assert.Equal(1, session.UndoLastAction());
        Assert.Contains(manual, session.Tasks);
    }

    [Fact]
    public void StartWaitingSkipsUnreadyAndRecoveringRowsAndStopSelectionExcludesIdleQueue()
    {
        using var session = CreateSession();
        var ready = Model("ready.mp4");
        var unavailable = Model("unavailable.mp4");
        var recovering = Model("recovering.mp4");
        recovering.IsRecoveringOutput = true;
        var running = Model("running.mp4", ConversionState.Running);
        foreach (var task in new[] { ready, unavailable, recovering, running }) session.AddTask(task);
        var commands = new QueueOperationsCoordinator(session);
        var started = new List<MediaTask>();

        var count = commands.StartWaiting(task => task != unavailable, task => started.Add(task));

        Assert.Equal(1, count);
        Assert.Same(ready, Assert.Single(started));
        Assert.Same(running, Assert.Single(commands.SelectToStop()));
        Assert.Equal(3, commands.SelectPending().Length);
        Assert.Equal(QueueRemovalAction.RemoveWithUndo, commands.ResolveRemoval(ready));
    }

    [Fact]
    public void PrioritizingIdleQueueMovesWholeOutputGroupWithoutStartingExecution()
    {
        using var session = CreateSession();
        var other = Model("other.mp4");
        var group = Guid.NewGuid();
        var first = Model("first.mp4"); first.OutputGroupId = group;
        var second = Model("second.mp4"); second.OutputGroupId = group;
        foreach (var task in new[] { other, first, second }) session.AddTask(task);
        var commands = new QueueOperationsCoordinator(session);
        var starts = 0;

        var result = commands.Prioritize(second.Id, _ => starts++);

        Assert.True(result.Changed > 0);
        Assert.False(result.Scheduled);
        Assert.Equal(0, starts);
        Assert.Equal([second.Id, first.Id, other.Id], session.Tasks.Select(task => task.Id));
    }

    private static QueueSession CreateSession() => new(new MemoryPersistence(),
        (_, _) => Task.FromResult(new MediaImportItemResult(string.Empty, null, null)),
        (_, _, _) => Task.FromResult(new ConversionWorkflowResult(ConversionOutcome.Completed,
            new(true, false, [new ConversionArtifact("result", "mp4", "output.mp4")], "completed"), "test")),
        (_, _) => Task.CompletedTask);

    private static MediaTask Model(string path, ConversionState state = ConversionState.Waiting)
    {
        var task = new MediaTask { InputPath = path, State = state, ContentKind = ContentKind.Video, TargetFormat = "MP4" };
        task.AvailableFormats.Add("MP4"); task.AvailableFormats.Add("MP3");
        return task;
    }

    private sealed class MemoryPersistence : IQueuePersistence
    {
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, []));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
