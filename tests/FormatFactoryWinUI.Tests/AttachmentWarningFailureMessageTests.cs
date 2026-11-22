using System.IO.Compression;
using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AttachmentWarningFailureMessageTests
{
    private const string AttachmentWarning = "Could not find codec parameters for stream 2 (Attachment: none): unknown codec";
    private const string SubtitleFailure = "Subtitle encoding failed: synthetic subtitle payload";
    private const string OutputSummary = "Nothing was written into output file, because at least one of its streams received no packets.";

    [Theory]
    [InlineData("balanced", "synthetic.mp4", "ConversionFailed")]
    [InlineData("lossless", "synthetic.mp4", "LosslessFailed")]
    [InlineData("balanced", "synthetic.m4s", "M4sFailed")]
    public void AttachmentWarning_DoesNotHideSubtitleFailureInVisibleMessage(string mode, string input, string key)
    {
        var task = new MediaTask { InputPath = input, ConversionMode = mode };

        Assert.Equal(LocalizationService.T(key, SubtitleFailure),
            FfmpegErrorCapture.BuildFailureMessage(task, [AttachmentWarning, SubtitleFailure, OutputSummary]));
    }

    [Theory]
    [InlineData("could not find codec parameters for stream 2 (attachment: none): unknown codec")]
    [InlineData("COULD NOT FIND CODEC PARAMETERS FOR STREAM 2 (ATTACHMENT: NONE): UNKNOWN CODEC")]
    public void ExplicitAttachmentClassification_IsCaseInsensitive(string warning)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(warning);
        capture.Add(SubtitleFailure);

        Assert.Equal(SubtitleFailure, capture.FailureDetail);
    }

    [Fact]
    public void SubtitleFailure_SurvivesLongProgressAndShutdownTail()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(AttachmentWarning);
        for (var index = 0; index < 512; index++) capture.Add($"frame={index} speed=1.0x");
        capture.Add(SubtitleFailure);
        for (var index = 0; index < 100; index++) capture.Add($"Shutdown diagnostic {index}");
        capture.Add(OutputSummary);

        Assert.DoesNotContain(AttachmentWarning, capture.LastLines);
        Assert.DoesNotContain(SubtitleFailure, capture.LastLines);
        Assert.Equal(SubtitleFailure, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", SubtitleFailure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
        Assert.True(capture.LastLines.Count <= 20);
        Assert.True(capture.LastLines.Sum(line => line.Length) <= BoundedProcessOutput.TailBytes / 4);
    }

    [Theory]
    [InlineData("Could not find codec parameters for stream 2 (Video: synthetic_video): unspecified frame size")]
    [InlineData("Could not find codec parameters for stream 2 (Audio: synthetic_audio): unspecified channel count")]
    [InlineData("Could not find codec parameters for stream 2: unspecified media parameters")]
    public void NonAttachmentCodecParameters_KeepExistingHigherPriority(string failure)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        capture.Add(AttachmentWarning);
        capture.Add(SubtitleFailure);
        capture.Add(OutputSummary);

        Assert.Equal(failure, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", failure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines));
    }

    [Theory]
    [InlineData("Decoding requested, but no decoder found for: synthetic_codec")]
    [InlineData("Error submitting packet to decoder: synthetic decoder failure")]
    [InlineData("Error while decoding stream: synthetic decoder failure")]
    public void DecoderFailure_RemainsHighestPriority(string failure)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(AttachmentWarning);
        capture.Add(SubtitleFailure);
        capture.Add(failure);
        capture.Add(SubtitleFailure);
        for (var index = 0; index < 100; index++) capture.Add($"EOF diagnostic {index}");
        capture.Add(OutputSummary);

        Assert.Equal(failure, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", failure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
    }

    [Fact]
    public void AttachmentWarningAlone_RemainsAvailableAsFailureFallback()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(AttachmentWarning);

        Assert.Null(capture.FailureDetail);
        Assert.Equal(AttachmentWarning, Assert.Single(capture.LastLines));
        Assert.Equal(LocalizationService.T("ConversionFailed", AttachmentWarning),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
    }

    [Fact]
    public void AttachmentWarning_DoesNotHideGenericFinalFailure()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(AttachmentWarning);
        const string failure = "Error opening output files: synthetic access failure";
        capture.Add(failure);

        Assert.Null(capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", failure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
    }

    [Theory]
    [InlineData("Error submitting packet to decoder: synthetic failure")]
    [InlineData("Invalid data found when processing input: synthetic failure")]
    public void AttachmentMarker_DoesNotSuppressAnotherFatalOnTheSameLine(string cause)
    {
        var capture = new FfmpegErrorCapture();
        var failure = AttachmentWarning + "; " + cause;
        capture.Add(failure);
        capture.Add(SubtitleFailure);

        Assert.Equal(failure, capture.FailureDetail);
    }

    [Fact]
    public void SubtitleFailure_RemainsBoundedWithoutSplittingSurrogates()
    {
        var capture = new FfmpegErrorCapture();
        const string prefix = "Subtitle encoding failed: ";
        capture.Add(AttachmentWarning);
        capture.Add(prefix + new string('x', FfmpegErrorCapture.MaximumFailureCharacters - prefix.Length - 1) + "🙂");
        for (var index = 0; index < 100; index++) capture.Add(new string('x', 100000));

        Assert.NotNull(capture.FailureDetail);
        Assert.StartsWith(prefix, capture.FailureDetail);
        Assert.True(capture.FailureDetail.Length <= FfmpegErrorCapture.MaximumFailureCharacters);
        Assert.False(char.IsHighSurrogate(capture.FailureDetail[^1]));
        Assert.True(capture.LastLines.Count <= 20);
        Assert.True(capture.LastLines.Sum(line => line.Length) <= BoundedProcessOutput.TailBytes / 4);
    }

    [Fact]
    public void SamePriorityFailure_KeepsExistingFirstDiagnosticPolicy()
    {
        var capture = new FfmpegErrorCapture();
        const string failure = "Unknown encoder 'synthetic_encoder'";
        capture.Add(failure);
        capture.Add(SubtitleFailure);
        capture.Add(OutputSummary);

        Assert.Equal(failure, capture.FailureDetail);
    }

    [Fact]
    public async Task FeedbackReport_MarkerAndArchiveUseTheActualSubtitleFailure()
    {
        using var directory = new TemporaryDirectory();
        var capture = new FfmpegErrorCapture();
        capture.Add(AttachmentWarning);
        for (var index = 0; index < 128; index++) capture.Add($"frame={index} speed=1.0x");
        capture.Add(SubtitleFailure);
        capture.Add(OutputSummary);
        var message = FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail);
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        var taskLog = Path.Combine(logs, "synthetic-failed-task.log");
        await File.WriteAllTextAsync(taskLog, string.Join('\n', new[] { AttachmentWarning, SubtitleFailure, OutputSummary }));
        var service = new FeedbackReportService(directory.Path);
        service.RecordError(message, taskLog);

        using var report = await service.CreateAsync("synthetic diagnostic regression", true, "synthetic");

        Assert.Equal(LocalizationService.T("ConversionFailed", SubtitleFailure), report.ErrorMessage);
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, "feedback-error.json")));
        Assert.Equal(report.ErrorMessage, marker.RootElement.GetProperty("Message").GetString());
        using var archive = ZipFile.OpenRead(report.ArchivePath);
        using var summaryReader = new StreamReader(archive.GetEntry("report.txt")!.Open());
        var summary = await summaryReader.ReadToEndAsync();
        Assert.Contains(SubtitleFailure, summary);
        Assert.DoesNotContain(AttachmentWarning, summary);
        Assert.Contains(AttachmentWarning, report.Preview);
        Assert.Contains(SubtitleFailure, report.Preview);
    }
}
