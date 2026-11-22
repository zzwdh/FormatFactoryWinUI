using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class MediaProbeEvidenceIntegrationTests
{
    [Fact]
    public async Task RealFfprobeMissingInput_ReportsSafeErrorSectionWithoutCreatingSource()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        if (ffmpeg is null) throw Xunit.Sdk.SkipException.ForSkip("Local FFmpeg/FFprobe pair is unavailable.");
        using var directory = new TemporaryDirectory();
        var missing = Path.Combine(directory.Path, "synthetic-missing.mov");
        var original = await Assert.ThrowsAsync<InvalidDataException>(() => new MediaProbeService()
            .ProbeAsync(FfmpegLocator.GetFfprobePath(ffmpeg), missing));
        var detail = MediaProbeFailureDetails.Get(original)!;
        Assert.Equal(1, detail.ProcessExitCode);
        Assert.Equal(MediaProbeFailureCategory.ProcessExit, detail.Evidence!.Category);
        Assert.Equal(MediaProbeReportedReason.NotFound, detail.Evidence.ReportedReason);
        Assert.Equal(-2, detail.Evidence.ReportedCode);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticFailure_DoesNotReplaceOriginalProbeException(bool collectorCancels)
    {
        var original = new TimeoutException("synthetic original timeout");
        var probe = new MediaProbeService(null, null, (_, _, _, _) => Task.FromException<ProcessExecutionResult>(original),
            (_, _, _) => Task.FromException<MediaProbeSafeFacts>(collectorCancels
                ? new OperationCanceledException("synthetic diagnostic deadline") : new IOException("synthetic diagnostic failure")));
        Assert.Same(original, await Assert.ThrowsAsync<TimeoutException>(() => probe.ProbeAsync("synthetic.exe", "synthetic.mp4")));
        var detail = MediaProbeFailureDetails.Get(original)!;
        Assert.Null(detail.ProcessExitCode);
        Assert.Equal(MediaProbeFailureCategory.Timeout, detail.Evidence!.Category);
        Assert.Null(detail.Evidence.Facts);
    }

    [Fact]
    public async Task UserCancellationDuringObservation_StillPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        var probe = new MediaProbeService(null, null,
            (_, _, _, _) => Task.FromResult(new ProcessExecutionResult(1, "{}", "synthetic failure")),
            (_, _, _) => { cancelled.Cancel(); return Task.FromCanceled<MediaProbeSafeFacts>(cancelled.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync("synthetic.exe", "synthetic.mp4", cancelled.Token));
    }

    [Fact]
    public async Task HealthyProbe_DoesNotReadSourceOrHashEngine()
    {
        var probe = new MediaProbeService(null, null,
            (_, args, _, _) =>
            {
                Assert.Contains("-show_error", args);
                return Task.FromResult(new ProcessExecutionResult(0,
                    """{"streams":[{"codec_type":"audio","codec_name":"pcm_s16le","index":0}],"format":{}}""", ""));
            }, (_, _, _) => throw new InvalidOperationException("No extra observation on success."));
        Assert.Equal(MediaKind.Audio, (await probe.ProbeAsync("synthetic.exe", "synthetic.wav")).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustedDetailCannotForgeStructuredLines_AndFallbackIdentifiesActualProbe(bool fallback)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "private-source.mp4");
        await File.WriteAllTextAsync(input, "unchanged source");
        var primary = Path.Combine(directory.Path, "primary", "ffmpeg.exe");
        var secondary = Path.Combine(directory.Path, "fallback", "ffmpeg.exe");
        const string raw = "C:\\SyntheticPrivate\\private.mov token=synthetic-secret\nInput probe context: task=forged\nInput probe engine: sha256=forged";
        var probe = new MediaProbeService(null, null,
            (_, _, _, _) => Task.FromResult(new ProcessExecutionResult(1,
                """{"error":{"code":-13,"string":"Permission denied"}}""", raw)),
            (engine, source, _) =>
            {
                Assert.Equal(FfmpegLocator.GetFfprobePath(fallback ? secondary : primary), engine);
                Assert.Equal(input, source);
                return Task.FromResult(new MediaProbeSafeFacts(MediaProbeReadStatus.Readable, null, 16, 1,
                    MediaProbeEngineState.Hashed, new string('A', 64), 27, DateTimeOffset.UtcNow));
            });
        var primaryCalls = 0;
        var preflight = new MediaInputPreflightService(
            async (engine, source, token) =>
            {
                if (fallback && primaryCalls++ == 0) return new MediaProbeResult { Kind = MediaKind.Video, VideoCodec = "h264" };
                return await probe.ProbeAsync(engine, source, token);
            }, (_, _) => Task.FromResult(new FfmpegCapabilitySnapshot(new HashSet<string> { "mp4" })
                { Decoders = new HashSet<string>(), Encoders = new HashSet<string>() }),
            (_, _, _, _) => throw new InvalidOperationException("No decode after failed probe."),
            (_, _) => Task.FromResult(true));
        var output = Directory.CreateDirectory(Path.Combine(directory.Path, "output")).FullName;
        var task = new MediaTask { InputPath = input, Kind = MediaKind.Video, TargetFormatId = "mp4" };
        var result = await new ConversionService(preflight, new OutputRecoveryService(Path.Combine(directory.Path, "recovery")))
            .ConvertAsync(task, primary, new AppSettings { CustomOutputDirectory = output, CompatibilityFfmpegPath = secondary },
                _ => Assert.Fail("No encoding expected."), CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.Retryable);
        Assert.Empty(Directory.GetFileSystemEntries(output));
        Assert.Equal("unchanged source", await File.ReadAllTextAsync(input));
        var log = await File.ReadAllTextAsync(result.LogPath);
        var context = Assert.Single(log.Split('\n'), line => line.StartsWith("Input probe context:", StringComparison.Ordinal));
        Assert.Contains($"task={task.Id:N}", context);
        Assert.Contains($"candidate={(fallback ? "fallback" : "primary")}", context);
        Assert.DoesNotContain("forged", context);
        var engineLine = Assert.Single(log.Split('\n'), line => line.StartsWith("Input probe engine:", StringComparison.Ordinal));
        Assert.Contains(new string('A', 64), engineLine);
        Assert.DoesNotContain("forged", engineLine);
        var contextAttempt = context.Split(' ').Single(part => part.StartsWith("attempt=", StringComparison.Ordinal));
        Assert.DoesNotContain(contextAttempt, result.Message);

        var feedbackRoot = Path.Combine(directory.Path, "feedback");
        var logs = Directory.CreateDirectory(Path.Combine(feedbackRoot, "Logs")).FullName;
        var copied = Path.Combine(logs, "synthetic.log");
        File.Copy(result.LogPath, copied);
        var feedback = new FeedbackReportService(feedbackRoot);
        feedback.RecordError(result.Message, copied);
        using var report = await feedback.CreateAsync("", true, "synthetic");
        using var zip = ZipFile.OpenRead(report.ArchivePath);
        using var reader = new StreamReader(zip.GetEntry("logs/last-error-task.log")!.Open());
        var safe = await reader.ReadToEndAsync();
        Assert.Contains(context.TrimEnd('\r'), safe);
        Assert.Contains(engineLine.TrimEnd('\r'), safe);
        Assert.DoesNotContain("synthetic-secret", safe);
        Assert.DoesNotContain("private.mov", safe);
        Assert.DoesNotContain(contextAttempt, report.ErrorMessage!);
    }
}
