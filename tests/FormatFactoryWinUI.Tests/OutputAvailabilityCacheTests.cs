using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputAvailabilityCacheTests
{
    [Fact]
    public async Task SlowOutputProbeDoesNotBlockProgressOrTaskbarAndCancellationStopsWaiting()
    {
        using var page = new QueuePageViewModel();
        var completed = page.AddTask(new MediaTask { InputPath = "source.mp4", State = ConversionState.Completed,
            OutputPath = @"\\offline\share\output.mp4", TargetFormatId = "mp4" });
        var running = page.AddTask(new MediaTask { InputPath = "running.mp4", State = ConversionState.Running, TargetFormatId = "mp4" });
        var generation = page.BeginProgress(running);
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = OutputAvailabilityCache.ProbeAsync(page.CaptureOutputAvailabilityChecks(), _ =>
        {
            started.TrySetResult();
            try { release.Wait(); return true; }
            finally { workerFinished.TrySetResult(); }
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(probe.IsCompleted);
            Assert.True(page.PublishProgress(running, generation, 37, "1x", "soon", "working"));
            Assert.Same(running, Assert.Single(page.DrainProgress()));
            Assert.Equal(37, running.Progress);
            Assert.False(page.CreateTaskbarState().HasOutput);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(workerFinished.Task.IsCompleted);
            Assert.True(page.RemoveTask(completed));
        }
        finally
        {
            release.Set();
            await workerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void RemovedAndReplacedOutputsRejectOldProbeResultsAndRefreshAfterInvalidation()
    {
        long now = 1;
        var cache = new OutputAvailabilityCache(() => now);
        var id = Guid.NewGuid();
        var original = new OutputAvailabilityTarget(id, "first.mp4");
        var firstCheck = Assert.Single(cache.CaptureChecks([original]));
        Assert.Empty(cache.CaptureChecks([original]));
        cache.Invalidate(id);
        var replacement = new OutputAvailabilityTarget(id, "second.mp4");
        var replacementCheck = Assert.Single(cache.CaptureChecks([replacement]));
        cache.Apply([new(firstCheck, true)]);
        Assert.False(cache.IsAvailable(id, replacement.Path));
        cache.Apply([new(replacementCheck, true)]);
        Assert.True(cache.IsAvailable(id, replacement.Path));
        Assert.Empty(cache.CaptureChecks([replacement]));
        now += 30_000;
        var refresh = Assert.Single(cache.CaptureChecks([replacement]));
        Assert.True(cache.IsAvailable(id, replacement.Path));
        cache.Apply([new(refresh, false)]);
        Assert.False(cache.IsAvailable(id, replacement.Path));
        cache.CaptureChecks([]);
        cache.Apply([new(replacementCheck, true)]);
        Assert.False(cache.IsAvailable(id, replacement.Path));
    }

    [Fact]
    public void QueueIgnoresLateAvailabilityAfterTaskRemovalRetryAndOutputChange()
    {
        using var page = new QueuePageViewModel();
        var task = page.AddTask(new MediaTask { InputPath = "source.mp4", State = ConversionState.Completed,
            OutputPath = "published.mp4", TargetFormatId = "mp4" });
        var original = Assert.Single(page.CaptureOutputAvailabilityChecks());
        task.OutputPath = "changed.mp4";
        page.ApplyOutputAvailabilityResults([new(original, true)]);
        Assert.False(page.CreateTaskbarState().HasOutput);
        var changed = Assert.Single(page.CaptureOutputAvailabilityChecks());
        page.ApplyOutputAvailabilityResults([new(changed, true)]);
        Assert.True(page.CreateTaskbarState().HasOutput);
        task.State = ConversionState.Running;
        Assert.False(page.CreateTaskbarState().HasOutput);
        page.InvalidateOutputAvailability(task.Id);
        task.State = ConversionState.Completed;
        var retried = Assert.Single(page.CaptureOutputAvailabilityChecks());
        page.ApplyOutputAvailabilityResults([new(changed, true)]);
        Assert.False(page.CreateTaskbarState().HasOutput);
        Assert.True(page.RemoveTask(task));
        page.ApplyOutputAvailabilityResults([new(retried, true)]);
        Assert.False(page.CreateTaskbarState().HasOutput);
    }
}
