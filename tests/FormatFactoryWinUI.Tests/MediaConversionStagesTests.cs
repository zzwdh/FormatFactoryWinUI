using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaConversionStagesTests
{
    [Fact]
    public async Task FailedHardwareAttemptDiscardsPartialBytesBeforeCpuFallback()
    {
        using var directory = new TemporaryDirectory();
        var temporary = Path.Combine(directory.Path, "encoded.tmp.mp4");
        var task = VideoTask(directory.Path);
        var options = new MediaConversionOptions { MaxWidth = 32 };
        options.ApplyTo(task.Options);
        var codecs = new List<string>();
        var hardware = new HardwareCapabilitiesService((_, backend, _, _) =>
            Task.FromResult(backend == EncoderBackend.Nvidia));
        var encoder = new MediaEncodingService((_, _, arguments, _, _, _) =>
        {
            // A failed hardware attempt must not contaminate the next encoder's output.
            Assert.False(File.Exists(temporary));
            var codec = arguments.Contains("h264_nvenc") ? "h264_nvenc" : "libx264";
            Assert.Contains(codec, arguments);
            codecs.Add(codec);
            File.WriteAllText(temporary, codec);
            return Task.FromResult(new FfmpegProcessResult(codec == "libx264" ? 0 : 1, ["GPU failed"]));
        }, hardware);

        var failure = await encoder.EncodeAsync(task, "synthetic-ffmpeg.exe", temporary, "task.log",
            new(options, new AppSettings { HardwareAcceleration = true }, null, null, null), _ => { }, default);

        Assert.Null(failure);
        Assert.Equal(new[] { "h264_nvenc", "libx264" }, codecs);
        Assert.Equal("libx264", await File.ReadAllTextAsync(temporary));
    }

    [Fact]
    public async Task DirectCancellationAfterHardwareFailurePreventsCpuRetry()
    {
        using var directory = new TemporaryDirectory();
        var task = VideoTask(directory.Path);
        var options = new MediaConversionOptions { MaxWidth = 32 };
        options.ApplyTo(task.Options);
        var operations = new MediaConversionOperations();
        await using var operation = operations.Begin(task.Id, default);
        var calls = 0;
        var hardware = new HardwareCapabilitiesService((_, backend, _, _) =>
            Task.FromResult(backend == EncoderBackend.Nvidia));
        var encoder = new MediaEncodingService((_, _, _, _, _, _) =>
        {
            calls++;
            operations.Cancel(task.Id);
            return Task.FromResult(new FfmpegProcessResult(1, ["Interrupted GPU"]));
        }, hardware);

        var failure = await encoder.EncodeAsync(task, "synthetic-ffmpeg.exe", Path.Combine(directory.Path, "output.tmp"),
            "task.log", new(options, new AppSettings { HardwareAcceleration = true }, null, null, null), _ => { }, operation.Token);

        Assert.NotNull(failure);
        Assert.True(failure.Cancelled);
        Assert.False(failure.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedOutputIsRejectedBeforeProbingOrPublishing()
    {
        using var directory = new TemporaryDirectory();
        var temporary = Path.Combine(directory.Path, "output.tmp.mp4");
        await File.WriteAllTextAsync(temporary, "Output exceeds the requested byte limit.");
        var task = VideoTask(directory.Path);
        var options = new MediaConversionOptions { TargetSizeBytes = 4 };
        options.ApplyTo(task.Options);
        var probed = false;
        var probe = new MediaProbeService(null, null, (_, _, _, _) =>
        {
            probed = true;
            throw new InvalidOperationException("Oversized output must be rejected before probing.");
        });
        var validator = new MediaOutputValidator(probe);

        await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(task,
            Path.Combine(directory.Path, "missing-engine", "ffmpeg.exe"), temporary, "task.log",
            new(options, new AppSettings(), null, null, null), task.Probe, false, default));

        Assert.False(probed);
        Assert.True(File.Exists(temporary)); // The enclosing path lease owns exceptional cleanup.
        Assert.False(File.Exists(Path.Combine(directory.Path, "final.mp4")));
    }

    [Fact]
    public async Task InvalidOutputIsRemovedAndCannotBePresentedAsSuccessful()
    {
        using var directory = new TemporaryDirectory();
        var temporary = Path.Combine(directory.Path, "output.tmp.mp4");
        await File.WriteAllTextAsync(temporary, "Unvalidated bytes.");
        var task = VideoTask(directory.Path);
        var validator = new MediaOutputValidator(new MediaProbeService());

        var failure = await validator.ValidateAsync(task, Path.Combine(directory.Path, "missing-engine", "ffmpeg.exe"),
            temporary, "task.log", new(new(), new(), null, null, null), task.Probe, false, default);

        Assert.NotNull(failure);
        Assert.False(failure.Success);
        Assert.False(failure.Cancelled);
        Assert.Empty(failure.OutputPath);
        Assert.Equal("task.log", failure.LogPath);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task CompletionObserverRunsAfterPublicationAndCannotInvalidateCommittedBytes()
    {
        using var directory = new TemporaryDirectory();
        var final = Path.Combine(directory.Path, "final.mp4");
        var temporary = Path.Combine(directory.Path, "output.tmp.mp4");
        File.WriteAllText(temporary, "Validated media bytes.");
        using var lease = new OutputPathLease(final, temporary, false, () => { });
        var committer = new MediaOutputCommitter(new(Path.Combine(directory.Path, "recovery")));
        var reported = false;
        var percent = 0d;
        string? observedBytes = null;
        var privateOutputStillExists = true;
        var task = VideoTask(directory.Path);
        var operations = new MediaConversionOperations();
        await using var operation = operations.Begin(task.Id, default);

        var result = committer.Commit(task, lease, "task.log", progress =>
        {
            reported = true;
            percent = progress.Percent;
            observedBytes = File.ReadAllText(final);
            privateOutputStillExists = File.Exists(temporary);
            operations.Cancel(task.Id);
            throw new InvalidOperationException("Observer disconnected.");
        }, operation.Token);

        lease.Dispose();
        Assert.True(reported);
        Assert.Equal(100, percent);
        Assert.Equal("Validated media bytes.", observedBytes);
        Assert.False(privateOutputStillExists);
        Assert.True(result.Success);
        Assert.Equal(final, result.OutputPath);
        Assert.Equal("Validated media bytes.", File.ReadAllText(final));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtCommitBoundaryNeverPublishesOrReportsCompletion(bool directCancellation)
    {
        using var directory = new TemporaryDirectory();
        var final = Path.Combine(directory.Path, "final.mp4");
        var temporary = Path.Combine(directory.Path, "output.tmp.mp4");
        File.WriteAllText(temporary, "Validated media bytes.");
        using var lease = new OutputPathLease(final, temporary, false, () => { });
        using var cancellation = new CancellationTokenSource();
        var operations = new MediaConversionOperations();
        var task = VideoTask(directory.Path);
        await using var operation = operations.Begin(task.Id, cancellation.Token);
        if (directCancellation) operations.Cancel(task.Id);
        else cancellation.Cancel();
        var committer = new MediaOutputCommitter(new(Path.Combine(directory.Path, "recovery")));
        var reported = false;

        Assert.ThrowsAny<OperationCanceledException>(() => committer.Commit(task, lease,
            "task.log", _ => reported = true, operation.Token));

        Assert.False(reported);
        Assert.False(File.Exists(final));
        Assert.True(File.Exists(temporary));
        lease.Dispose();
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void PublicationConflictKeepsValidatedOutputRecoverableAndNeverReportsSuccess()
    {
        using var directory = new TemporaryDirectory();
        var final = Path.Combine(directory.Path, "final.mp4");
        var temporary = Path.Combine(directory.Path, "output.tmp.mp4");
        File.WriteAllText(final, "Unrelated existing file.");
        File.WriteAllText(temporary, "Validated media bytes.");
        using var lease = new OutputPathLease(final, temporary, false, () => { });
        var committer = new MediaOutputCommitter(new(Path.Combine(directory.Path, "recovery")));
        var reported = false;

        var error = Assert.Throws<OutputCommitException>(() => committer.Commit(VideoTask(directory.Path), lease,
            "task.log", _ => reported = true, default));

        lease.Dispose();
        Assert.False(reported);
        Assert.NotNull(error.RecoveryId);
        Assert.Equal("Validated media bytes.", File.ReadAllText(error.PreservedPath));
        Assert.Equal("Unrelated existing file.", File.ReadAllText(final));
    }

    private static MediaTask VideoTask(string directory) => new()
    {
        InputPath = Path.Combine(directory, "input.mp4"), TargetFormat = "MP4",
        Probe = new MediaProbeResult
        {
            Kind = MediaKind.Video, VideoCodec = "h264", Width = 64, Height = 48,
            PixelFormat = "yuv420p", Duration = TimeSpan.FromSeconds(1)
        }
    };
}
