using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MemoryAllocationFailureMessageTests
{
    private const string AllocationFailure = "x264 [error]: malloc of size 3145728 failed";
    private const string Shutdown = "Terminating thread: synthetic encoder shutdown";

    [Theory]
    [InlineData("balanced", "synthetic.mp4", "ConversionFailed")]
    [InlineData("lossless", "synthetic.mp4", "LosslessFailed")]
    [InlineData("balanced", "synthetic.m4s", "M4sFailed")]
    public void VisibleMessage_RetainsAllocationFailure(string mode, string input, string key)
    {
        var task = new MediaTask { ConversionMode = mode, InputPath = input };

        Assert.Equal(LocalizationService.T(key, AllocationFailure),
            FfmpegErrorCapture.BuildFailureMessage(task, [AllocationFailure, Shutdown]));
    }

    [Theory]
    [InlineData("x264 [error]: malloc of size 73 failed")]
    [InlineData("X264 [ERROR]: MALLOC OF SIZE 32768 FAILED")]
    [InlineData("  x264 [error]: malloc of size 0 failed  ")]
    public void Capture_RecognizesOnlyExplicitX264AllocationFailures(string failure)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        capture.Add(Shutdown);

        Assert.Equal(failure, capture.FailureDetail);
    }

    [Theory]
    [InlineData("x265 [error]: malloc of size 73 failed")]
    [InlineData("x264 [info]: malloc of size 73 failed")]
    [InlineData(@"C:\synthetic\x264 [error]: malloc of size 73 failed")]
    [InlineData("x264 [error]: malloc of size -1 failed")]
    [InlineData("x264 [error]: malloc of size 1.5 failed")]
    [InlineData("x264 [error]: malloc of size many failed")]
    [InlineData("x264 [error]: malloc of size  failed")]
    [InlineData("x264 [error]: malloc of size 73 failed while reading a synthetic filename")]
    public void Capture_DoesNotClassifySimilarTextOrFilenames(string text)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(text);
        capture.Add(Shutdown);

        Assert.Null(capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", Shutdown),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
    }

    [Fact]
    public void Capture_KeepsCauseAfterTailRollsOver()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(AllocationFailure);
        for (var i = 0; i < 100; i++) capture.Add($"Synthetic shutdown {i}");
        capture.Add(Shutdown);

        Assert.DoesNotContain(AllocationFailure, capture.LastLines);
        Assert.Equal(AllocationFailure, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", AllocationFailure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
        Assert.True(capture.LastLines.Count <= 20);
    }

    [Fact]
    public void Capture_BoundsUntrustedAllocationTextWithoutNumericOverflow()
    {
        var failure = "x264 [error]: malloc of size " + new string('7', 100000) + " failed";
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        for (var i = 0; i < 100; i++) capture.Add(Shutdown);

        Assert.NotNull(capture.FailureDetail);
        Assert.Equal(FfmpegErrorCapture.MaximumFailureCharacters, capture.FailureDetail.Length);
        Assert.True(capture.LastLines.Sum(line => line.Length) <= BoundedProcessOutput.TailBytes / 4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecoderPriority_RemainsHigher(bool allocationFirst)
    {
        const string decoder = "Unknown decoder 'synthetic_codec'";
        var capture = new FfmpegErrorCapture();
        capture.Add(allocationFirst ? AllocationFailure : decoder);
        capture.Add(allocationFirst ? decoder : AllocationFailure);
        capture.Add(Shutdown);

        Assert.Equal(decoder, capture.FailureDetail);
    }

    [Fact]
    public void EqualEncoderPriority_RetainsFirstDiagnosticPolicy()
    {
        const string earlier = "Unknown encoder 'synthetic_encoder'";
        var capture = new FfmpegErrorCapture();
        capture.Add(earlier);
        capture.Add(AllocationFailure);
        capture.Add(Shutdown);

        Assert.Equal(earlier, capture.FailureDetail);
    }

    [Fact]
    public async Task ProcessAndFeedback_PreserveAllocationCauseInReport()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "synthetic-allocation.log");
        var stderr = string.Join(Environment.NewLine, new[] { AllocationFailure }
            .Concat(Enumerable.Range(0, 100).Select(i => $"Synthetic shutdown {i}")).Append(Shutdown));
        var task = new MediaTask { InputPath = "synthetic.mp4" };
        var result = await new FfmpegProcessRunner().RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", "", stderr, "1"],
            logPath, _ => { }, default);
        LogService.FlushTask(logPath);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(AllocationFailure, result.LastErrorLines);
        Assert.Equal(AllocationFailure, result.FailureDetail);
        var message = FfmpegErrorCapture.BuildFailureMessage(task, result.LastErrorLines, result.FailureDetail);
        var feedback = new FeedbackReportService(directory.Path);
        feedback.RecordError(message, logPath);
        using var report = await feedback.CreateAsync("Synthetic allocation diagnostic", true, "synthetic");
        Assert.Equal(LocalizationService.T("ConversionFailed", AllocationFailure), report.ErrorMessage);

        using var archive = ZipFile.OpenRead(report.ArchivePath);
        using var reader = new StreamReader(archive.GetEntry("report.txt")!.Open());
        var summary = await reader.ReadToEndAsync();
        Assert.Contains(AllocationFailure, summary);
        Assert.DoesNotContain(Shutdown, summary);
    }
}
