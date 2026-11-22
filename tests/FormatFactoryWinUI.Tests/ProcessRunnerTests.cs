using System.Diagnostics;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_AlreadyCancelledDoesNotAttemptToStartProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
            Path.Combine(Path.GetTempPath(), $"missing-tool-{Guid.NewGuid():N}.exe"),
            [],
            TimeSpan.FromSeconds(10),
            cancellation.Token));
    }

    [Fact]
    public void ProcessJob_WithoutAssignedProcessDoesNotClaimSuccessfulTermination()
    {
        using var job = ProcessJob.TryCreate();
        Assert.NotNull(job);

        Assert.False(job.TryTerminate());
    }

    [Fact]
    public async Task RunAsync_CapturesOutputErrorAndExitCode()
    {
        var result = await RunHelperAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            "echo",
            "hello-output",
            "hello-error",
            "7");

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("hello-output", result.StandardOutput);
        Assert.Contains("hello-error", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsParentAndDescendant()
    {
        using var directory = new TemporaryDirectory();
        var childPidPath = Path.Combine(directory.Path, "child.pid");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RunHelperAsync(
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                "spawn-and-wait",
                childPidPath));

        await RepositoryTestContext.WaitForFileAsync(childPidPath, TimeSpan.FromSeconds(2));
        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        await RepositoryTestContext.AssertProcessExitedAsync(childPid, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_CancellationKillsParentAndDescendant()
    {
        using var directory = new TemporaryDirectory();
        var childPidPath = Path.Combine(directory.Path, "child.pid");
        using var cancellation = new CancellationTokenSource();
        var run = RunHelperAsync(
            TimeSpan.FromSeconds(30),
            cancellation.Token,
            "spawn-and-wait",
            childPidPath);

        await RepositoryTestContext.WaitForFileAsync(childPidPath, TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        await RepositoryTestContext.AssertProcessExitedAsync(childPid, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_NormalParentExitClosesJobAndDoesNotHangOnInheritedPipes()
    {
        using var directory = new TemporaryDirectory();
        var childPidPath = Path.Combine(directory.Path, "child.pid");
        var stopwatch = Stopwatch.StartNew();

        var result = await RunHelperAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            "spawn-and-exit",
            childPidPath);

        stopwatch.Stop();
        Assert.Equal(0, result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Process drain took {stopwatch.Elapsed}.");
        await RepositoryTestContext.WaitForFileAsync(childPidPath, TimeSpan.FromSeconds(2));
        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        await RepositoryTestContext.AssertProcessExitedAsync(childPid, TimeSpan.FromSeconds(5));
    }

    private static Task<ProcessExecutionResult> RunHelperAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        return ProcessRunner.RunAsync(
            RepositoryTestContext.DotNetHost,
            new[] { RepositoryTestContext.TestHelperAssembly }.Concat(arguments),
            timeout,
            cancellationToken);
    }
}
