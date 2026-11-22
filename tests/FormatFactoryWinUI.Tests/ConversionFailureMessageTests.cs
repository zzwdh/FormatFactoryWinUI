using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ConversionFailureMessageTests
{
    [Theory]
    [InlineData("balanced", "input.mp4", "ConversionFailed")]
    [InlineData("lossless", "input.mp4", "LosslessFailed")]
    [InlineData("balanced", "input.m4s", "M4sFailed")]
    public void DecoderFailure_IsNotHiddenByFinalOutputError(string mode, string input, string messageKey)
    {
        var task = new MediaTask { InputPath = input, ConversionMode = mode };
        const string decoderError = "Decoding requested, but no decoder found for: synthetic_codec";

        var message = FfmpegErrorCapture.BuildFailureMessage(task,
            [decoderError, "Error opening output files: Invalid argument"]);

        Assert.Equal(LocalizationService.T(messageKey, decoderError), message);
    }

    [Fact]
    public void OtherFailures_KeepLastUsefulDetailAndEmptyFallback()
    {
        var task = new MediaTask { InputPath = "input.mp4" };
        Assert.Equal(LocalizationService.T("ConversionFailed", "Synthetic output failure"),
            FfmpegErrorCapture.BuildFailureMessage(task, ["Earlier diagnostic", "Synthetic output failure", " "]));
        Assert.Equal(LocalizationService.T("ConversionFailed", LocalizationService.T("FfmpegNoReason")),
            FfmpegErrorCapture.BuildFailureMessage(task, []));
    }

    [Theory]
    [InlineData("balanced", "input.mp3", "ConversionFailed")]
    [InlineData("lossless", "input.mp4", "LosslessFailed")]
    [InlineData("balanced", "input.m4s", "M4sFailed")]
    public void CorruptInputDecoderFailure_IsNotReplacedByEncoderShutdown(string mode, string input, string messageKey)
    {
        var task = new MediaTask { InputPath = input, ConversionMode = mode };
        const string decoderError = "Error submitting packet to decoder: Invalid data found when processing input";

        var message = FfmpegErrorCapture.BuildFailureMessage(task,
            ["Could not find codec parameters for stream 0", decoderError,
             "Neither number of channels nor channel layout specified",
             "Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.",
             "Nothing was written into output file, because at least one of its streams received no packets."]);

        Assert.Equal(LocalizationService.T(messageKey, decoderError), message);
    }

    [Theory]
    [InlineData("Could not find codec parameters for stream 0: unspecified frame size")]
    [InlineData("Unknown encoder 'synthetic_encoder'")]
    [InlineData("Error while opening encoder: unsupported sample format")]
    [InlineData("Failed to configure output pad on Parsed_scale_0")]
    [InlineData("Trailing garbage after stream specifier: -1")]
    public void SpecificFailure_IsNotHiddenByAnEmptyOutputSummary(string failure)
    {
        Assert.Equal(LocalizationService.T("ConversionFailed", failure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(),
                [failure, "Nothing was written into output file, because at least one of its streams received no packets."]));
    }

    [Fact]
    public void ErrorCapture_RetainsDecoderFailureAfterTheTailRollsOver()
    {
        var capture = new FfmpegErrorCapture();
        const string decoderError = "Error submitting packet to decoder: Invalid data found when processing input";
        capture.Add(decoderError);
        for (var index = 0; index < 100; index++) capture.Add($"Shutdown diagnostic {index}");
        capture.Add("Nothing was written into output file, because at least one of its streams received no packets.");

        Assert.DoesNotContain(decoderError, capture.LastLines);
        Assert.Equal(decoderError, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", decoderError),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
    }

    [Fact]
    public void ErrorCapture_KeepsBothDiagnosticsAndTailBoundedWithoutSplittingSurrogates()
    {
        var capture = new FfmpegErrorCapture();
        const string prefix = "Unknown decoder ";
        capture.Add(prefix + new string('x', FfmpegErrorCapture.MaximumFailureCharacters - prefix.Length - 1) + "🙂");
        for (var index = 0; index < 100; index++) capture.Add(new string('x', 100000));

        Assert.NotNull(capture.FailureDetail);
        Assert.True(capture.FailureDetail.Length <= FfmpegErrorCapture.MaximumFailureCharacters);
        Assert.False(char.IsHighSurrogate(capture.FailureDetail[^1]));
        Assert.True(capture.LastLines.Count <= 20);
        Assert.True(capture.LastLines.Sum(line => line.Length) <= BoundedProcessOutput.TailBytes / 4);
    }
}
