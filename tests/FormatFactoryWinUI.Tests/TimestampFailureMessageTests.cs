using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class TimestampFailureMessageTests
{
    private const string TimestampFailure = "[aost#0:1/aac @ synthetic] Non-monotonic DTS; previous: 71, current: 17; Error submitting a packet to the muxer: Invalid argument";
    private const string Shutdown = "Terminating thread: synthetic muxer shutdown";

    [Theory]
    [InlineData("balanced", "synthetic.mp4", "ConversionFailed")]
    [InlineData("lossless", "synthetic.mp4", "LosslessFailed")]
    [InlineData("balanced", "synthetic.m4s", "M4sFailed")]
    public void VisibleMessage_RetainsExplicitFatalTimestampDiagnostic(string mode, string input, string key)
    {
        var task = new MediaTask { ConversionMode = mode, InputPath = input };

        Assert.Equal(LocalizationService.T(key, TimestampFailure),
            FfmpegErrorCapture.BuildFailureMessage(task, [TimestampFailure, Shutdown]));
    }

    [Theory]
    [InlineData(TimestampFailure)]
    [InlineData("[AOST#0:0/AAC @ SYNTHETIC] NON-MONOTONIC DTS; PREVIOUS: 9, CURRENT: 2; ERROR SUBMITTING A PACKET TO THE MUXER: INVALID ARGUMENT")]
    public void Capture_RecognizesFatalTimestampPacketRejection(string failure)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        capture.Add(Shutdown);

        Assert.Equal(failure, capture.FailureDetail);
    }

    [Theory]
    [InlineData("[aac @ synthetic] Queue input is backward in time")]
    [InlineData("[aost#0:1/aac @ synthetic] Non-monotonic DTS; previous: 71, current: 17")]
    [InlineData("[aost#0:1/aac @ synthetic] Non-monotonic DTS; previous: 71, current: 17; changing to 72. This may result in incorrect timestamps in the output file.")]
    [InlineData("Non-monotonous DTS in output stream 0:1; previous: 71, current: 17; changing to 72.")]
    [InlineData("Error submitting a packet to the muxer: Invalid argument")]
    [InlineData("[out#0/mp4 @ synthetic] Error muxing a packet")]
    [InlineData("[out#0/mp4 @ synthetic] Task finished with error code: -22 (Invalid argument)")]
    public void Capture_DoesNotPromoteWarningsOrGenericMuxerErrors(string diagnostic)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(diagnostic);
        capture.Add(Shutdown);

        Assert.Null(capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", Shutdown),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
    }

    [Fact]
    public void Capture_DoesNotCombineSeparateWarningsAndPacketErrors()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add("Non-monotonic DTS; previous: 71, current: 17; changing to 72.");
        capture.Add("Error submitting a packet to the muxer: synthetic unrelated error");
        capture.Add(Shutdown);

        Assert.Null(capture.FailureDetail);
    }

    [Theory]
    [InlineData("Unknown decoder 'synthetic_codec'", true)]
    [InlineData("Unknown decoder 'synthetic_codec'", false)]
    [InlineData("Invalid data found when processing input", true)]
    [InlineData("Invalid data found when processing input", false)]
    public void ExistingHigherPriorityDiagnostic_RemainsPreferred(string higherPriority, bool timestampFirst)
    {
        var lines = timestampFirst ? new[] { TimestampFailure, higherPriority, Shutdown }
            : new[] { higherPriority, TimestampFailure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);

        Assert.Equal(higherPriority, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", higherPriority),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), lines));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SamePriorityDiagnostic_KeepsFirstFailurePolicy(bool timestampFirst)
    {
        const string encoder = "Unknown encoder 'synthetic_encoder'";
        var lines = timestampFirst ? new[] { TimestampFailure, encoder, Shutdown }
            : new[] { encoder, TimestampFailure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);

        var expected = timestampFirst ? TimestampFailure : encoder;
        Assert.Equal(expected, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", expected),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), lines));
    }

    [Fact]
    public void Capture_PreservesTimestampCauseAfterShutdownRollsOutOfTail()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(TimestampFailure);
        for (var i = 0; i < 100; i++) capture.Add($"Synthetic shutdown {i}");
        capture.Add(Shutdown);

        Assert.DoesNotContain(TimestampFailure, capture.LastLines);
        Assert.Equal(TimestampFailure, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", TimestampFailure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
        Assert.True(capture.LastLines.Count <= 20);
    }

    [Fact]
    public void Capture_BoundsUntrustedFatalDetailWithoutSplittingSurrogates()
    {
        var prefix = TimestampFailure + " ";
        var failure = prefix + new string('x', FfmpegErrorCapture.MaximumFailureCharacters - prefix.Length - 1) + "🙂";
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        for (var i = 0; i < 100; i++) capture.Add(new string('x', 100000));

        Assert.NotNull(capture.FailureDetail);
        Assert.True(capture.FailureDetail.Length <= FfmpegErrorCapture.MaximumFailureCharacters);
        Assert.False(char.IsHighSurrogate(capture.FailureDetail[^1]));
        Assert.True(capture.LastLines.Count <= 20);
        Assert.True(capture.LastLines.Sum(line => line.Length) <= BoundedProcessOutput.TailBytes / 4);
    }

    [Fact]
    public async Task ProcessAndFeedback_PreserveFatalTimestampCauseInReport()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "synthetic-timestamp.log");
        var stderr = string.Join(Environment.NewLine, new[] { TimestampFailure }
            .Concat(Enumerable.Range(0, 100).Select(i => $"Synthetic shutdown {i}")).Append(Shutdown));
        var task = new MediaTask { InputPath = "synthetic.mp4" };
        var result = await new FfmpegProcessRunner().RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", "", stderr, "-22"],
            logPath, _ => { }, default);
        LogService.FlushTask(logPath);

        Assert.Equal(-22, result.ExitCode);
        Assert.DoesNotContain(TimestampFailure, result.LastErrorLines);
        Assert.Equal(TimestampFailure, result.FailureDetail);
        Assert.Contains(TimestampFailure, await File.ReadAllTextAsync(logPath));
        var message = FfmpegErrorCapture.BuildFailureMessage(task, result.LastErrorLines, result.FailureDetail);
        var feedback = new FeedbackReportService(directory.Path);
        feedback.RecordError(message, logPath);
        using var report = await feedback.CreateAsync("Synthetic timestamp diagnostic", true, "synthetic");
        Assert.Equal(LocalizationService.T("ConversionFailed", TimestampFailure), report.ErrorMessage);

        using var archive = ZipFile.OpenRead(report.ArchivePath);
        using var reader = new StreamReader(archive.GetEntry("report.txt")!.Open());
        var summary = await reader.ReadToEndAsync();
        Assert.Contains(TimestampFailure, summary);
        Assert.DoesNotContain(Shutdown, summary);
    }
}
