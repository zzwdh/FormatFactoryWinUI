using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaConversionLifetimeTests
{
    [Fact]
    public async Task ATaskIdRemainsOwnedUntilCancellationCallbacksFinish()
    {
        var operations = new MediaConversionOperations();
        var id = Guid.NewGuid();
        var operation = operations.Begin(id, default);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = operation.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });

        operations.Cancel(id);
        var finishing = operation.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            operations.CancelAll();
            Assert.False(finishing.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => operations.Begin(id, default));
        }
        finally
        {
            release.Set();
            await finishing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await using var retry = operations.Begin(id, default);
        Assert.False(retry.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task DirectCancellationTerminatesTheRegisteredEncoder()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operations = new MediaConversionOperations();
        var task = new MediaTask();
        await using var operation = operations.Begin(task.Id, timeout.Token);
        var pidFile = Path.Combine(directory.Path, "encoder.pid");
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = operation.Runner.RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "progress-and-wait", pidFile],
            Path.Combine(directory.Path, "encoder.log"), _ => reported.TrySetResult(), operation.Token);
        try
        {
            await reported.Task.WaitAsync(timeout.Token);
            operations.Cancel(task.Id);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(timeout.Token));
            await RepositoryTestContext.AssertProcessExitedAsync(
                int.Parse(await File.ReadAllTextAsync(pidFile)), TimeSpan.FromSeconds(5));
            Assert.False(operation.Runner.HasActiveProcess);
        }
        finally
        {
            operations.Cancel(task.Id);
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReachesPreflightAndAllowsRetry(bool cancelAll)
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bothProbing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCount = 0;
        var preflight = new MediaInputPreflightService(async (_, _, token) =>
        {
            var attempt = Interlocked.Increment(ref probeCount);
            if (attempt == 2) bothProbing.TrySetResult();
            if (attempt <= 2) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidDataException("Retry reached the input probe.");
        }, (_, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
            (_, _) => Task.FromResult(false));
        var service = new ConversionService(preflight);
        var first = new MediaTask { InputPath = Path.Combine(directory.Path, "a.mp4"), Kind = MediaKind.Video };
        var second = new MediaTask { InputPath = Path.Combine(directory.Path, "b.mp4"), Kind = MediaKind.Video };
        Task<ConversionResult> Start(MediaTask task) => service.ConvertAsync(task,
            Path.Combine(directory.Path, "ffmpeg.exe"), new AppSettings(), _ => { }, timeout.Token);

        var firstRun = Start(first);
        var secondRun = Start(second);
        await bothProbing.Task.WaitAsync(timeout.Token);
        Assert.True(service.HasActiveTasks);
        Assert.False(service.Pause(first.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(first));
        if (cancelAll) service.CancelAll();
        else service.Cancel(first.Id);

        Assert.True((await firstRun.WaitAsync(timeout.Token)).Cancelled);
        if (!cancelAll)
        {
            Assert.True(service.HasActiveTasks);
            Assert.False(secondRun.IsCompleted);
            service.Cancel(second.Id);
        }
        Assert.True((await secondRun.WaitAsync(timeout.Token)).Cancelled);
        Assert.False(service.HasActiveTasks);
        service.Cancel(first.Id);
        var retry = await Start(first);
        Assert.False(retry.Cancelled);
        Assert.Contains("Retry reached the input probe.", retry.Message);
        Assert.False(service.HasActiveTasks);
    }
}
