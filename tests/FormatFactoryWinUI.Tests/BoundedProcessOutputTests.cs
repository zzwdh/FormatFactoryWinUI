using System.Text;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class BoundedProcessOutputTests
{
    [Fact]
    public async Task ParsedOutput_OverflowTerminatesWriterInsteadOfReturningPartialData()
    {
        using var directory = new TemporaryDirectory();
        var pidPath = Path.Combine(directory.Path, "writer.pid");
        var run = RunAsync(new ProcessRunOptions { MaximumOutputBytes = 1024 }, "65536", "wait", pidPath);
        var error = await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("stdout", error.StreamName);
        await RepositoryTestContext.AssertProcessExitedAsync(int.Parse(await File.ReadAllTextAsync(pidPath)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LoggedOutput_KeepsFullFilesAndOnlyBoundedTailsInMemory()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "converter.log");
        var result = await RunAsync(new ProcessRunOptions
        {
            CaptureCompleteOutput = false,
            MaximumOutputBytes = 1024,
            MaximumErrorBytes = 1024,
            LogPath = logPath
        }, "1048576", "both");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.EndsWith("END-OUT", result.StandardOutput);
        Assert.EndsWith("END-ERR", result.StandardError);
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(result.StandardOutput));
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(result.StandardError));
        Assert.Equal(1048576 + 7, new FileInfo(logPath).Length);
        Assert.Equal(1048576 + 7, new FileInfo(logPath + ".stderr").Length);
    }

    [Fact]
    public async Task CaptureLimit_IsUtf8BytesAndExactBoundaryIsAccepted()
    {
        var data = Encoding.UTF8.GetBytes("中文🙂");
        var result = await BoundedProcessOutput.ReadAsync(new MemoryStream(data), "stdout", data.Length, true);
        Assert.Equal("中文🙂", result.Text);
        Assert.False(result.Truncated);
        await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
            BoundedProcessOutput.ReadAsync(new MemoryStream(data), "stdout", data.Length - 1, true));
    }

    [Fact]
    public async Task UnterminatedProgressLine_IsBoundedAndFailsExplicitly()
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65537))));
        await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(async () =>
        {
            await foreach (var unused in BoundedProcessOutput.ReadLinesAsync(reader)) { }
        });
    }

    [Fact]
    public async Task OpticalConversion_HugeDiagnosticLineKeepsFullLogAndFinishesSuccessfully()
    {
        var command = new OpticalDiscToolCommand(OpticalDiscOperation.DiscToIso, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "flood-output", "1048576", "both"]);
        var result = await new OpticalDiscCommandExecutor(TimeSpan.FromSeconds(20)).ExecuteAsync(command);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.EndsWith("END-OUT" + Environment.NewLine, result.StandardOutput);
        Assert.EndsWith("END-ERR" + Environment.NewLine, result.StandardError);
        Assert.True(Encoding.UTF8.GetByteCount(result.StandardOutput) <= BoundedProcessOutput.TailBytes);
        Assert.Equal(1048576 + 7, Encoding.UTF8.GetByteCount(await File.ReadAllTextAsync(result.LogPath!)));
    }

    [Fact]
    public async Task OpticalInspection_HugeParsedLineFailsInsteadOfReturningIncompleteDriveList()
    {
        var command = new OpticalDiscToolCommand(OpticalDiscOperation.InspectMakeMkvDrives, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "flood-output", "1048576", "both"]);
        await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
            new OpticalDiscCommandExecutor(TimeSpan.FromSeconds(20)).ExecuteAsync(command));
    }

    [Fact]
    public async Task MediaConversion_HugeStderrLineKeepsBoundedErrorsAndFullLog()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "media.log");
        var result = await new FfmpegProcessRunner().RunProcessAsync(new MediaTask(), RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "flood-output", "1048576", "error"], logPath, _ => { }, CancellationToken.None);
        LogService.FlushTask(logPath);
        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("END-ERR", Assert.Single(result.LastErrorLines));
        Assert.True(result.LastErrorLines.Sum(line => Encoding.UTF8.GetByteCount(line)) <= BoundedProcessOutput.TailBytes);
        Assert.True(new FileInfo(logPath).Length >= 1048576 + 7);
    }

    private static Task<ProcessExecutionResult> RunAsync(ProcessRunOptions options, params string[] arguments) =>
        ProcessRunner.RunAsync(RepositoryTestContext.DotNetHost,
            new[] { RepositoryTestContext.TestHelperAssembly, "flood-output" }.Concat(arguments),
            TimeSpan.FromSeconds(20), options);
}
