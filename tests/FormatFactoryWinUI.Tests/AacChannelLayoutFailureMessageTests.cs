using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AacChannelLayoutFailureMessageTests
{
    // Fixed FFmpeg AAC diagnostic; logger identities and layouts here are synthetic.
    private const string Failure = "[aac @ synthetic] Unsupported channel layout \"11 channels\"";
    private const string EncoderFailure = "[aost#0:0/aac @ synthetic] Error while opening encoder - synthetic failure";
    private const string Shutdown = "Synthetic encoder shutdown";

    [Theory]
    [InlineData("balanced", "synthetic.mp4", "ConversionFailed")]
    [InlineData("lossless", "synthetic.mkv", "LosslessFailed")]
    [InlineData("balanced", "synthetic.m4s", "M4sFailed")]
    public void VisibleMessage_RetainsSpecificAacFailure(string mode, string input, string key)
    {
        Assert.Equal(LocalizationService.T(key, Failure), FfmpegErrorCapture.BuildFailureMessage(
            new MediaTask { ConversionMode = mode, InputPath = input }, [Failure, EncoderFailure, Shutdown]));
    }

    [Theory]
    [InlineData(Failure)]
    [InlineData("  [AAC @ SYNTHETIC] UNSUPPORTED CHANNEL LAYOUT \"synthetic-layout\"  ")]
    [InlineData("[aac @ synthetic_2] Unsupported channel layout \"11 channels (FL+FR)\"")]
    public void Capture_RecognizesCompleteBoundedAacFatalLine(string failure)
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        capture.Add(EncoderFailure);
        Assert.Equal(failure, capture.FailureDetail);
    }

    [Theory]
    [InlineData("[aac @ synthetic] Guessed Channel Layout: stereo")]
    [InlineData("[aac @ synthetic] Using a PCE to encode channel layout \"synthetic-layout\"")]
    [InlineData("[aac @ synthetic] warning: Unsupported channel layout \"11 channels\"")]
    [InlineData("[opus @ synthetic] Unsupported channel layout \"11 channels\"")]
    [InlineData("[aac_fixed @ synthetic] Unsupported channel layout \"11 channels\"")]
    [InlineData("[aost#0:0/aac @ synthetic] Unsupported channel layout \"11 channels\"")]
    [InlineData("Unsupported channel layout \"11 channels\"")]
    [InlineData("[aac @ ] Unsupported channel layout \"11 channels\"")]
    [InlineData("[aac @ synthetic label] Unsupported channel layout \"11 channels\"")]
    [InlineData("[aac @ [synthetic] Unsupported channel layout \"11 channels\"")]
    [InlineData("[aac @ synthetic] unrelated [aac @ synthetic] Unsupported channel layout \"11 channels\"")]
    [InlineData("[aac @ synthetic] Unsupported channel layout \"\"")]
    [InlineData("[aac @ synthetic] Unsupported channel layout \"synthetic\"extra\"")]
    [InlineData("[aac @ synthetic] Unsupported channel layout \"11 channels\"; synthetic continuation")]
    [InlineData("[aac @ synthetic] Unsupported channel layout 11 channels")]
    [InlineData("[aac @ synthetic] Unsupported channel layout \"11\nchannels\"")]
    [InlineData("[aac @ synthetic\n] Unsupported channel layout \"11 channels\"")]
    [InlineData("[aac @ synthetic] Unsupported channel layout \"11 channels\"\n")]
    [InlineData("[aac @ synthetic] Unsupported channel layout \"synthetic\tchannels\"")]
    public void Capture_DoesNotPromoteWarningsOrMalformedPayloads(string line)
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
    public void HigherPriorityInputFailure_RemainsPreferred(string higher, bool aacFirst)
    {
        var lines = aacFirst ? new[] { Failure, higher, Shutdown } : new[] { higher, Failure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);
        Assert.Equal(higher, capture.FailureDetail);
        Assert.Equal(LocalizationService.T("ConversionFailed", higher),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), lines));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SamePriorityFailure_KeepsFirstCause(bool aacFirst)
    {
        var lines = aacFirst ? new[] { Failure, EncoderFailure, Shutdown }
            : new[] { EncoderFailure, Failure, Shutdown };
        var capture = new FfmpegErrorCapture();
        foreach (var line in lines) capture.Add(line);
        Assert.Equal(aacFirst ? Failure : EncoderFailure, capture.FailureDetail);
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void Capture_RespectsFfmpegLayoutBufferBound(int layoutLength, bool expected)
    {
        var failure = $"[aac @ synthetic] Unsupported channel layout \"{new string('x', layoutLength)}\"";
        var capture = new FfmpegErrorCapture();
        capture.Add(failure);
        Assert.Equal(expected ? failure : null, capture.FailureDetail);
    }

    [Fact]
    public void Capture_RetainsCauseAfterItRollsOutOfTail()
    {
        var capture = new FfmpegErrorCapture();
        capture.Add(Failure);
        capture.Add(EncoderFailure);
        for (var i = 0; i < 100; i++) capture.Add($"Synthetic shutdown {i}");
        Assert.DoesNotContain(Failure, capture.LastLines);
        Assert.Equal(Failure, capture.FailureDetail);
        Assert.True(capture.LastLines.Count <= 20);
        Assert.True(capture.FailureDetail!.Length <= FfmpegErrorCapture.MaximumFailureCharacters);
        Assert.Equal(LocalizationService.T("ConversionFailed", Failure),
            FfmpegErrorCapture.BuildFailureMessage(new MediaTask(), capture.LastLines, capture.FailureDetail));
    }

    [Fact]
    public async Task ProcessAndFeedback_PreserveAacCauseAndFailureExitCode()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "synthetic-aac.log");
        var stderr = string.Join(Environment.NewLine, new[] { Failure, EncoderFailure }
            .Concat(Enumerable.Range(0, 100).Select(i => $"Synthetic shutdown {i}")));
        var task = new MediaTask { InputPath = "synthetic.mp4" };
        var result = await new FfmpegProcessRunner().RunProcessAsync(task, RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "echo", "", stderr, "-22"],
            logPath, _ => { }, default);
        LogService.FlushTask(logPath);
        Assert.Equal(-22, result.ExitCode);
        Assert.DoesNotContain(Failure, result.LastErrorLines);
        Assert.Equal(Failure, result.FailureDetail);
        var feedback = new FeedbackReportService(directory.Path);
        feedback.RecordError(FfmpegErrorCapture.BuildFailureMessage(task, result.LastErrorLines, result.FailureDetail), logPath);
        using var report = await feedback.CreateAsync("Synthetic AAC diagnostic", true, "synthetic");
        Assert.Equal(LocalizationService.T("ConversionFailed", Failure), report.ErrorMessage);
        using var archive = ZipFile.OpenRead(report.ArchivePath);
        using var reader = new StreamReader(archive.GetEntry("report.txt")!.Open());
        var summary = await reader.ReadToEndAsync();
        Assert.Contains(Failure, summary);
        Assert.DoesNotContain(EncoderFailure, summary);
    }
}
