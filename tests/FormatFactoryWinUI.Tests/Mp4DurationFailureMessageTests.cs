using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class Mp4DurationFailureMessageTests
{
    // Fixed FFmpeg diagnostic grammar; identities and numbers are synthetic.
    private const string Failure = "[mp4 @ synthetic] Application provided duration: 9876543210 in stream 2 is invalid";
    private const string Shutdown = "Synthetic muxer shutdown";

    [Theory]
    [InlineData("balanced", "synthetic.mp4", "ConversionFailed")]
    [InlineData("lossless", "synthetic.mkv", "LosslessFailed")]
    [InlineData("balanced", "synthetic.m4s", "M4sFailed")]
    public void VisibleMessage_PreservesInvalidDurationCause(string mode, string input, string key)
    {
        Assert.Equal(LocalizationService.T(key, Failure), FfmpegErrorCapture.BuildFailureMessage(
            new MediaTask { ConversionMode = mode, InputPath = input }, [Failure, Shutdown]));
    }

    [Theory]
    [InlineData(Failure)]
    [InlineData("  [MP4 @ SYNTHETIC_2] APPLICATION PROVIDED DURATION: -7 IN STREAM 0 IS INVALID  ")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 0 in stream 1 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: -9223372036854775808 in stream 2147483647 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 9223372036854775807 in stream 0 is invalid")]
    public void Capture_RecognizesCompleteBoundedMp4FatalLine(string failure)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        capture.Add(Shutdown);
        Assert.Equal(failure, capture.FailureDetail);
    }

    [Theory]
    [InlineData("[mp4 @ synthetic] Packet duration: 17 / dts: 17 in stream 2 is out of range")]
    [InlineData("[mp4 @ synthetic] Non-monotonic DTS; previous: 19, current: 11; changing to 20")]
    [InlineData("[mp4 @ synthetic] warning: Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[matroska @ synthetic] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[mov @ synthetic] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[out#0/mp4 @ synthetic] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[mp4 @ ] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic label] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[mp4 @ [synthetic] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] unrelated [mp4 @ synthetic] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream 2 is invalid; continuation")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream 2")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream 2 is invalid\n")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17\r in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic\n] Application provided duration: 17 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: +17 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 1.7 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: ١٧ in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: - in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration:  in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration:  is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 0 in stream is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 9223372036854775808 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: -9223372036854775809 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 000000000000000000000 in stream 2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream -2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream +2 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream ٢ is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream 2147483648 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream 00000000000 is invalid")]
    [InlineData("[mp4 @ synthetic] Application provided duration: 17 in stream  is invalid")]
    public void Capture_RejectsWarningsOtherMuxersAndMalformedLines(string line)
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
    public void HigherPriorityInputFailure_RemainsPreferred(string higher, bool durationFirst)
    {
        var lines = durationFirst ? new[] { Failure, higher, Shutdown } : new[] { higher, Failure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);
        Assert.Equal(higher, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", higher),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), lines));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SamePriorityFailure_RetainsFirstCause(bool durationFirst)
    {
        const string encoder = "Unknown encoder 'synthetic_encoder'";
        var lines = durationFirst ? new[] { Failure, encoder, Shutdown } : new[] { encoder, Failure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);
        Assert.Equal(durationFirst ? Failure : encoder, capture.FailureDetail);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    [InlineData(200000, false)]
    public void Capture_BoundsLoggerIdentity(int identityLength, bool expected)
    {
        var failure = $"[mp4 @ {new string('x', identityLength)}] Application provided duration: 17 in stream 2 is invalid";
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        Assert.Equal(expected ? failure : null, capture.FailureDetail);
    }

    [Fact]
    public void Capture_RetainsFatalCauseWithinExistingTailBounds()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(Failure);
        for (var i = 0; i < 100; i++) capture.Add($"Synthetic shutdown {i}: {new string('x', 300)}");
        Assert.DoesNotContain(Failure, capture.LastLines);
        Assert.Equal(Failure, capture.FailureDetail);
        Assert.True(capture.LastLines.Count <= 20);
        Assert.True(capture.LastLines.Sum(line => line.Length) <= BoundedProcessOutput.TailBytes / 4);
        Assert.True(capture.FailureDetail!.Length <= FfmpegErrorCapture.MaximumFailureCharacters);
    }

    [Fact]
    public void Capture_RejectsExcessivePaddingWithoutChangingFailureBound()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(new string(' ', FfmpegErrorCapture.MaximumFailureCharacters) + Failure);
        Assert.Null(capture.FailureDetail);
    }

    [Fact]
    public async Task ProcessAndFeedback_PreserveCauseWithoutChangingFailedExit()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "synthetic-mp4.log");
        var stderr = string.Join(Environment.NewLine, new[] { Failure }
            .Concat(Enumerable.Range(0, 100).Select(i => $"Synthetic shutdown {i}")));
        var task = new MediaTask { InputPath = "synthetic.mp4" };
        var result = await new FfmpegProcessRunner().RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", "", stderr, "-22"], logPath, _ => { }, default);
        LogService.FlushTask(logPath);
        Assert.Equal(-22, result.ExitCode);
        Assert.DoesNotContain(Failure, result.LastErrorLines);
        Assert.Equal(Failure, result.FailureDetail);
        var feedback = new FeedbackReportService(directory.Path);
        feedback.RecordError(FfmpegErrorCapture.BuildFailureMessage(task, result.LastErrorLines, result.FailureDetail), logPath);
        using var report = await feedback.CreateAsync("Synthetic duration diagnostic", true, "synthetic");
        Assert.Equal(LocalizationService.T("ConversionFailed", Failure), report.ErrorMessage);
        using var archive = ZipFile.OpenRead(report.ArchivePath);
        using var reader = new StreamReader(archive.GetEntry("report.txt")!.Open());
        Assert.Contains(Failure, await reader.ReadToEndAsync());
    }
}
