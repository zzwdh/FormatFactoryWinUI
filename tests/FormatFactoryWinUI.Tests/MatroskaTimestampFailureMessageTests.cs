using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MatroskaTimestampFailureMessageTests
{
    // Fixed FFmpeg muxer diagnostic; every logger identity and input here is synthetic.
    private const string Failure = "[matroska @ synthetic] Can't write packet with unknown timestamp";
    private const string Shutdown = "Terminating thread: synthetic muxer shutdown";

    [Theory]
    [InlineData("balanced", "synthetic.mp4", "ConversionFailed")]
    [InlineData("lossless", "synthetic.mkv", "LosslessFailed")]
    [InlineData("balanced", "synthetic.m4s", "M4sFailed")]
    public void VisibleMessage_RetainsMissingTimestampCause(string mode, string input, string key)
    {
        var task = new MediaTask { ConversionMode = mode, InputPath = input };
        Assert.Equal(LocalizationService.T(key, Failure),
            FfmpegErrorCapture.BuildFailureMessage(task, [Failure, Shutdown]));
    }

    [Theory]
    [InlineData(Failure)]
    [InlineData("  [MATROSKA @ SYNTHETIC] CAN'T WRITE PACKET WITH UNKNOWN TIMESTAMP  ")]
    public void Capture_RecognizesCompleteMatroskaFatalLine(string failure)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        capture.Add(Shutdown);
        Assert.Equal(failure, capture.FailureDetail);
    }

    [Theory]
    [InlineData("[matroska @ synthetic] Timestamps are unset in a packet for stream 0.")]
    [InlineData("[out#0/matroska @ synthetic] Error muxing a packet")]
    [InlineData("Error submitting a packet to the muxer: Invalid argument")]
    [InlineData("[mp4 @ synthetic] Can't write packet with unknown timestamp")]
    [InlineData("Can't write packet with unknown timestamp")]
    [InlineData("[matroska @ synthetic] warning: Can't write packet with unknown timestamp")]
    [InlineData("[matroska @ synthetic] Can't write packet with unknown timestamp; synthetic continuation")]
    [InlineData("[matroska @ synthetic\n] Can't write packet with unknown timestamp")]
    [InlineData("[matroska @ synthetic] Can't write packet with\nunknown timestamp")]
    public void Capture_DoesNotPromoteWarningsOrPartialMessages(string line)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(line);
        capture.Add(Shutdown);
        Assert.Null(capture.FailureDetail);
    }

    [Theory]
    [InlineData("Unknown decoder 'synthetic_codec'", true)]
    [InlineData("Unknown decoder 'synthetic_codec'", false)]
    [InlineData("Invalid data found when processing input", true)]
    [InlineData("Invalid data found when processing input", false)]
    public void HigherPriorityInputFailure_RemainsPreferred(string higher, bool timestampFirst)
    {
        var lines = timestampFirst ? new[] { Failure, higher, Shutdown } : new[] { higher, Failure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);
        Assert.Equal(higher, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", higher),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), lines));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SamePriorityFailure_KeepsFirstCause(bool timestampFirst)
    {
        const string encoder = "Unknown encoder 'synthetic_encoder'";
        var lines = timestampFirst ? new[] { Failure, encoder, Shutdown } : new[] { encoder, Failure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);
        var expected = timestampFirst ? Failure : encoder;
        Assert.Equal(expected, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", expected),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), lines));
    }

    [Fact]
    public void Capture_RetainsCauseAfterShutdownRollsOutOfTail()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(Failure);
        for (var i = 0; i < 100; i++) capture.Add($"Synthetic shutdown {i}");
        capture.Add(Shutdown);
        Assert.DoesNotContain(Failure, capture.LastLines);
        Assert.Equal(Failure, capture.FailureDetail);
        Assert.True(capture.LastLines.Count <= 20);
        Assert.Equal(LocalizationService.T("LosslessFailed", Failure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask { ConversionMode = "lossless" },
                capture.LastLines, capture.FailureDetail));
    }

    [Fact]
    public async Task ProcessAndFeedback_PreserveCauseWithoutChangingExitCode()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "synthetic-matroska.log");
        var stderr = string.Join(Environment.NewLine, new[] { Failure }
            .Concat(Enumerable.Range(0, 100).Select(i => $"Synthetic shutdown {i}")).Append(Shutdown));
        var task = new MediaTask { InputPath = "synthetic.mkv", ConversionMode = "lossless" };
        var result = await new FfmpegProcessRunner().RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", "", stderr, "-22"],
            logPath, _ => { }, default);
        LogService.FlushTask(logPath);

        Assert.Equal(-22, result.ExitCode);
        Assert.DoesNotContain(Failure, result.LastErrorLines);
        Assert.Equal(Failure, result.FailureDetail);
        var feedback = new FeedbackReportService(directory.Path);
        feedback.RecordError(FfmpegErrorCapture.BuildFailureMessage(task, result.LastErrorLines, result.FailureDetail), logPath);
        using var report = await feedback.CreateAsync("Synthetic muxer diagnostic", true, "synthetic");
        Assert.Equal(LocalizationService.T("LosslessFailed", Failure), report.ErrorMessage);
        using var archive = ZipFile.OpenRead(report.ArchivePath);
        using var reader = new StreamReader(archive.GetEntry("report.txt")!.Open());
        var summary = await reader.ReadToEndAsync();
        Assert.Contains(Failure, summary);
        Assert.DoesNotContain(Shutdown, summary);
    }
}
