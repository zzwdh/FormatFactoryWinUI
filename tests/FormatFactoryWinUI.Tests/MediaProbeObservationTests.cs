using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaProbeObservationTests
{
    [Theory]
    [InlineData(false, "ProbeUnavailable")]
    [InlineData(true, "ArtifactUnavailable")]
    public async Task OutputProbeSeparatesMissingInputsAndKeepsPathsOutOfFacts(bool probeExists, string outcome)
    {
        using var directory = new TemporaryDirectory();
        var probe = Path.Combine(directory.Path, "private-probe-location.exe");
        var artifact = Path.Combine(directory.Path, "private-output-file.mp4");
        var log = Path.Combine(directory.Path, "observation.log");
        if (probeExists) File.WriteAllText(probe, "not run when the artifact is missing");
        var task = Guid.NewGuid();
        var operation = Guid.NewGuid();

        Assert.False(await MediaProbeService.ValidateOutputCoreAsync(probe, artifact, CancellationToken.None,
            new OutputProbeObservation(task, operation, log)));
        LogService.FlushTask(log);

        var facts = File.ReadAllText(log);
        Assert.Contains($"task={task:N} operation={operation:N}", facts);
        Assert.Contains($"outcome={outcome}", facts);
        Assert.DoesNotContain("private-", facts);
    }

    [Fact]
    public void StrictDecodeKeepsLastValidNumericProgressFromRetainedOutput()
    {
        var process = new ProcessExecutionResult(255,
            "frame=3\nout_time_us=250000\nframe=7\nframe=private-media\nout_time_us=-1\n", "private-token")
        { OutputTruncated = true };
        var observation = MediaInputPreflightService.DescribeDecode(process, 200);

        Assert.Equal(7, observation.LastFrame);
        Assert.Equal(250000, observation.LastOutputTimeUs);
        Assert.Equal(255, observation.ExitCode);
        Assert.Equal(process.StandardError.Length, observation.ErrorCharacters);
        Assert.True(observation.Truncated);
    }
}
