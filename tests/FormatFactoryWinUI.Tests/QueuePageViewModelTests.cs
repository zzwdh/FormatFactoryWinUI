using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class QueuePageViewModelTests
{
    [Fact]
    public void FilterKeepsRowIdentityAndDropsSelectionOutsideVisibleQueue()
    {
        using var page = new QueuePageViewModel();
        var video = page.AddTask(Model("video.mp4", MediaKind.Video));
        var audio = page.AddTask(Model("audio.mp3", MediaKind.Audio));
        var image = page.AddTask(Model("image.png", MediaKind.Image));
        page.RefreshVisibleTasks();
        page.SetSelection([video.Id, audio.Id, audio.Id, Guid.NewGuid()]);
        Assert.Equal([video.Id, audio.Id], page.SelectedTaskIds);

        page.Filter = "video";
        page.RefreshVisibleTasks();

        Assert.Same(video, Assert.Single(page.VisibleTasks));
        Assert.Same(video, Assert.Single(page.SelectedTasks));
        Assert.False(page.MatchesFilter(image));
        page.Filter = "all";
        page.RefreshVisibleTasks();
        Assert.Equal([video, audio, image], page.VisibleTasks);
        Assert.Same(video, Assert.Single(page.SelectedTasks));
        Assert.Same(video, page.AddTask(video.Model));
        Assert.Equal(3, page.Tasks.Count);
    }

    [Fact]
    public void SummaryAndTaskbarDistinguishScheduledWaitingFromIdleWaiting()
    {
        var scheduled = new HashSet<Guid>();
        using var page = new QueuePageViewModel(scheduled.Contains);
        foreach (var state in Enum.GetValues<ConversionState>())
            page.AddTask(Model(state + ".mp4", state: state));
        var waiting = page.Tasks.Single(task => task.State == ConversionState.Waiting);
        var completed = page.Tasks.Single(task => task.State == ConversionState.Completed);
        completed.OutputPath = "published.mp4";
        scheduled.Add(waiting.Id);

        var summary = page.CreateSummary();
        page.ApplyOutputAvailabilityResults(page.CaptureOutputAvailabilityChecks().Select(check =>
            new Services.OutputAvailabilityResult(check, check.Path == completed.OutputPath)));
        var taskbar = page.CreateTaskbarState();

        Assert.Equal(Enum.GetValues<ConversionState>().Length, summary.Total);
        Assert.Equal(3, summary.Active);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Waiting);
        Assert.Equal(4, summary.StopCount);
        Assert.True(summary.HasTasks && summary.CanClearPending && summary.CanStop);
        Assert.Equal(4, taskbar.Activities.Length);
        Assert.Equal(2, taskbar.Activities.Count(item => item.Active));
        Assert.Single(taskbar.Activities, item => item.Paused);
        Assert.Single(taskbar.Activities, item => item.Failed);
        Assert.True(taskbar.HasOutput);
        Assert.Equal(summary.StopCount, taskbar.StopCount);
        Assert.Equal(completed.OutputPath, page.FindLastOutput(_ => true));
        scheduled.Clear();
        Assert.Equal(3, page.CreateSummary().StopCount);
    }

    [Fact]
    public void SynchronizeOrderPreservesRowsAndUnknownRowsRemainAtEnd()
    {
        using var page = new QueuePageViewModel();
        var first = page.AddTask(Model("a.mp4"));
        var unknown = page.AddTask(Model("b.mp4"));
        var last = page.AddTask(Model("c.mp4"));
        page.RefreshVisibleTasks();
        page.SetSelection([unknown.Id]);

        page.SynchronizeOrder([last.Id, first.Id]);
        page.RefreshVisibleTasks();

        Assert.Equal([last, first, unknown], page.Tasks);
        Assert.Equal([last, first, unknown], page.VisibleTasks);
        Assert.Same(unknown, Assert.Single(page.SelectedTasks));
    }

    [Fact]
    public async Task BackgroundProgressCoalescesWithoutChangingBoundRowsUntilDrain()
    {
        using var page = new QueuePageViewModel();
        var task = page.AddTask(Model("running.mp4", state: ConversionState.Running));
        var generation = page.BeginProgress(task);
        var timerRequests = await Task.Run(() => Enumerable.Range(1, 100).Count(value =>
            page.PublishProgress(task, generation, value, value + "x", "soon", "latest " + value)));

        Assert.Equal(1, timerRequests);
        Assert.Equal(0, task.Progress);
        Assert.Same(task, Assert.Single(page.DrainProgress()));
        Assert.Equal(100, task.Progress);
        Assert.Equal("100x", task.Speed);
        Assert.Equal("latest 100", task.Message);
        Assert.Empty(page.DrainProgress());
        Assert.True(page.PublishProgress(task, generation, 80, "1x", "later", "new drain"));
    }

    [Fact]
    public void PreviousAttemptCannotOverwriteNewAttemptsPendingProgress()
    {
        using var page = new QueuePageViewModel();
        var task = page.AddTask(Model("retry.mp4", state: ConversionState.Running));
        var oldGeneration = page.BeginProgress(task);
        page.PublishProgress(task, oldGeneration, 90, "old", "old", "old");
        var generation = page.BeginProgress(task);
        page.PublishProgress(task, generation, 20, "new", "new", "retry");

        Assert.False(page.PublishProgress(task, oldGeneration, 99, "late", "late", "stale"));
        page.DrainProgress();

        Assert.Equal(20, task.Progress);
        Assert.Equal("retry", task.Message);
        Assert.Equal("new", task.Speed);
    }

    [Fact]
    public void ProgressCannotChangeTerminalOrRemovedRows()
    {
        using var page = new QueuePageViewModel();
        var task = page.AddTask(Model("finished.mp4", state: ConversionState.Running));
        var generation = page.BeginProgress(task);
        page.PublishProgress(task, generation, 10, "1x", "soon", "late");
        task.State = ConversionState.Completed;
        task.Progress = 100;
        Assert.Empty(page.DrainProgress());
        Assert.Equal(100, task.Progress);
        task.State = ConversionState.Running;
        generation = page.BeginProgress(task);
        page.PublishProgress(task, generation, 50, "1x", "soon", "removed");

        Assert.True(page.RemoveTask(task));
        Assert.False(page.PublishProgress(task, generation, 90, "1x", "soon", "after removal"));
        Assert.Empty(page.DrainProgress());
        Assert.Empty(page.Tasks);
    }

    [Fact]
    public void BatchResultDrainsEndedWorkAndKeepsActiveWorkForNextCompletion()
    {
        var active = new HashSet<Guid>();
        using var page = new QueuePageViewModel(active.Contains);
        var finished = Model("first.mp4", state: ConversionState.Completed);
        finished.OutputPath = "first-result.mp4";
        finished.ResultStatistics = new(400, 200, 50);
        var failed = Model("failed.mp4", state: ConversionState.Failed);
        var stillRunning = Model("running.mp4", state: ConversionState.Running);
        active.Add(stillRunning.Id);
        foreach (var task in new[] { finished, failed, stillRunning }) page.TrackBatchTask(task);

        var first = page.CompleteBatch(_ => true);

        Assert.Equal(1, first.FailedCount);
        Assert.Equal(finished.OutputPath, first.OutputPath);
        Assert.NotEmpty(first.Message);
        Assert.Empty(page.CompleteBatch(_ => true).Message);
        stillRunning.State = ConversionState.Completed;
        stillRunning.OutputPath = "second-result.mp4";
        active.Clear();
        var next = page.CompleteBatch(_ => true);
        Assert.Equal(stillRunning.OutputPath, next.OutputPath);
        Assert.Equal(0, next.FailedCount);
        Assert.Empty(page.CompleteBatch(_ => true).Message);
    }

    private static MediaTask Model(string path, MediaKind kind = MediaKind.Video, ConversionState state = ConversionState.Waiting) =>
        new() { InputPath = path, Kind = kind, TargetFormatId = "mp4", State = state };
}
