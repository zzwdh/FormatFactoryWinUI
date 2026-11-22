using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ConversionServiceProcessTests
{
    [Fact]
    public async Task FailedEngineObservation_CancellationDoesNotReplaceTheProcessFailure()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logPath = Path.Combine(directory.Path, "not-created.log");

        await FfmpegProcessRunner.AppendFailedEngineObservationAsync(logPath,
            Path.Combine(directory.Path, "missing-engine.exe"), Path.Combine(directory.Path, "missing-input.bin"), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public async Task RunProcess_AlreadyCancelledDoesNotStartTheEncoder()
    {
        var runner = new FfmpegProcessRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunProcessAsync(
            new MediaTask(),
            Path.Combine(Path.GetTempPath(), $"missing-encoder-{Guid.NewGuid():N}.exe"),
            [],
            "unused.log",
            _ => { },
            cancellation.Token));
    }

    [Fact]
    public async Task RunProcess_NormalParentExitTerminatesChildrenHoldingOutputPipes()
    {
        using var directory = new TemporaryDirectory();
        var childPidPath = Path.Combine(directory.Path, "child.pid");
        var runner = new FfmpegProcessRunner();
        var task = new MediaTask();
        using var cancellation = new CancellationTokenSource();
        var run = RunHelperAsync(runner, task, cancellation.Token, _ => { },
            "spawn-and-exit", childPidPath);

        try
        {
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, result.ExitCode);
            await RepositoryTestContext.WaitForFileAsync(childPidPath, TimeSpan.FromSeconds(2));
            await RepositoryTestContext.AssertProcessExitedAsync(
                int.Parse(await File.ReadAllTextAsync(childPidPath)), TimeSpan.FromSeconds(5));
            Assert.False(runner.HasActiveProcess);
        }
        finally
        {
            await CancelAndObserveAsync(cancellation, run);
        }
    }

    [Fact]
    public async Task RunProcess_ProgressReaderFailureTerminatesEncoderAndPropagatesError()
    {
        using var directory = new TemporaryDirectory();
        var pidPath = Path.Combine(directory.Path, "encoder.pid");
        var runner = new FfmpegProcessRunner();
        var task = new MediaTask();
        var expected = new IOException("Progress destination is unavailable.");
        using var cancellation = new CancellationTokenSource();
        var run = RunHelperAsync(runner, task, cancellation.Token, _ => throw expected,
            "progress-and-wait", pidPath);

        try
        {
            var actual = await Assert.ThrowsAsync<IOException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Same(expected, actual);
            await RepositoryTestContext.WaitForFileAsync(pidPath, TimeSpan.FromSeconds(2));
            await RepositoryTestContext.AssertProcessExitedAsync(
                int.Parse(await File.ReadAllTextAsync(pidPath)), TimeSpan.FromSeconds(5));
            Assert.False(runner.HasActiveProcess);
        }
        finally
        {
            await CancelAndObserveAsync(cancellation, run);
        }
    }

    [Theory]
    [InlineData(0, "00000000")]
    [InlineData(1, "00000001")]
    [InlineData(-22, "FFFFFFEA")]
    [InlineData(-1073741819, "C0000005")]
    public async Task RunProcess_SilentExitRetainsItsSignedAndHexCodeInTaskDiagnostics(int exitCode, string hexCode)
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "silent-exit.log");
        var runner = new FfmpegProcessRunner();

        var result = await runner.RunProcessAsync(new MediaTask(), RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", "", "", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            logPath, _ => { }, default);
        LogService.FlushTask(logPath);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.All(result.LastErrorLines, line => Assert.True(string.IsNullOrWhiteSpace(line)));
        Assert.Null(result.FailureDetail);
        Assert.False(runner.HasActiveProcess);
        var log = await File.ReadAllTextAsync(logPath);
        Assert.Contains($"Process exit code: {exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)} (0x{hexCode})", log);
        Assert.Equal(1, log.Split("Process exit code:", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task RunProcess_Exit255KeepsObservedPipeCountsAndProgressOnTheSameAttempt()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "encoder-probe.log");
        var task = new MediaTask();
        const string output = "out_time_us=12000\nprogress=end";
        var result = await new FfmpegProcessRunner().RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", output, "", "255"], logPath, _ => { }, default);
        LogService.FlushTask(logPath);

        Assert.Equal(255, result.ExitCode);
        var facts = File.ReadAllLines(logPath).Where(line => line.StartsWith("Encoder process:")).ToArray();
        var start = Assert.Single(facts, line => line.Contains("stage=Started"));
        var finish = Assert.Single(facts, line => line.Contains("stage=Finished"));
        var operation = start.Split(' ').Single(part => part.StartsWith("operation="));
        Assert.Contains(operation, finish);
        Assert.Contains($"task={task.Id:N}", finish);
        Assert.Contains("attempt=1", finish);
        Assert.Contains("exit=255", finish);
        Assert.Contains($"stdout_chars={output.Length + Environment.NewLine.Length} stderr_chars={Environment.NewLine.Length}", finish);
        Assert.Contains("stdout_drain=RanToCompletion stderr_drain=RanToCompletion", finish);
        Assert.Contains("token_termination_requested=False", finish);
        Assert.Contains("last_progress_us=12000", finish);
        Assert.DoesNotContain(directory.Path, string.Join('\n', facts));
    }

    [Fact]
    public async Task RunProcess_CancellationRecordsARequestAndAnIndependentTerminalObservation()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var logPath = Path.Combine(directory.Path, "cancelled-encoder.log");
        var pidPath = Path.Combine(directory.Path, "encoder.pid");
        var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new MediaTask();
        var run = new FfmpegProcessRunner().RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "progress-and-wait", pidPath],
            logPath, _ => progress.TrySetResult(), cancellation.Token);
        try { await progress.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        LogService.FlushTask(logPath);

        var facts = File.ReadAllLines(logPath).Where(line => line.StartsWith("Encoder process:")).ToArray();
        var request = Assert.Single(facts, line => line.Contains("stage=TerminationRequested reason=ConversionToken"));
        var finish = Assert.Single(facts, line => line.Contains("stage=Finished"));
        Assert.Contains(request.Split(' ').Single(part => part.StartsWith("operation=")), finish);
        Assert.Contains("token_cancelled=True", finish);
        Assert.Contains("last_progress_us=1000000", finish);
        await RepositoryTestContext.AssertProcessExitedAsync(int.Parse(await File.ReadAllTextAsync(pidPath)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunProcess_PreservesDecoderDiagnosticBeforeLongEncoderShutdown()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "media.log");
        const string decoderError = "Error submitting packet to decoder: Invalid data found when processing input";
        var stderr = string.Join(Environment.NewLine,
            new[] { decoderError }.Concat(Enumerable.Range(0, 30).Select(index => $"Shutdown diagnostic {index}"))
                .Append("Nothing was written into output file, because at least one of its streams received no packets."));

        var result = await new FfmpegProcessRunner().RunProcessAsync(new MediaTask(), RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", "", stderr, "1"], logPath, _ => { }, default);
        LogService.FlushTask(logPath);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(decoderError, result.LastErrorLines);
        Assert.Equal(decoderError, result.FailureDetail);
        Assert.Contains(decoderError, await File.ReadAllTextAsync(logPath));
    }

    private static Task<FfmpegProcessResult> RunHelperAsync(
        FfmpegProcessRunner runner,
        MediaTask task,
        CancellationToken cancellationToken,
        Action<ConversionProgress> report,
        params string[] arguments) => runner.RunProcessAsync(
            task,
            RepositoryTestContext.DotNetHost,
            new[] { RepositoryTestContext.TestHelperAssembly }.Concat(arguments).ToArray(),
            "unused.log",
            report,
            cancellationToken);

    private static async Task CancelAndObserveAsync(
        CancellationTokenSource cancellation,
        Task run)
    {
        cancellation.Cancel();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Observe the expected failure after ensuring no helper remains running.
        }
    }
}
