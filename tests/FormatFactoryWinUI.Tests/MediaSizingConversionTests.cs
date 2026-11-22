using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaSizingConversionTests
{
    [Theory]
    [InlineData(48, 32, 24, 32)]
    [InlineData(480, 320, 48, 64)]
    public async Task CustomVideoDimensions_FollowDisplayRotationKeepAspectRatioAndNeverEnlarge(
        int maximumWidth, int maximumHeight, int expectedWidth, int expectedHeight)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var coded = Path.Combine(directory.Path, "landscape-coded.mp4");
        var input = Path.Combine(directory.Path, "portrait.mp4");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=64x48:r=10",
             "-t", "1.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", coded], TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var rotated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-display_rotation:v:0", "90", "-i", coded,
             "-c", "copy", input], TimeSpan.FromSeconds(20));
        Assert.True(rotated.ExitCode == 0, rotated.StandardError);
        var rotation = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream_side_data=rotation",
             "-of", "default=nw=1:nk=1", input], TimeSpan.FromSeconds(20));
        Assert.Equal(0, rotation.ExitCode);
        Assert.Equal("90", rotation.StandardOutput.Trim());
        var probe = new MediaProbeService();
        var task = new MediaTask { InputPath = input, SourceFormatId = "mp4", TargetFormatId = "mp4",
            ConversionMode = "fast", Probe = await probe.ProbeAsync(ffprobe, input) };
        Assert.Equal(64, task.Probe.Width);
        Assert.Equal(48, task.Probe.Height);
        new MediaConversionOptions { TargetSizeBytes = 50000, MaxWidth = maximumWidth, MaxHeight = maximumHeight }.ApplyTo(task.Options);
        var result = await new ConversionService().ConvertAsync(task, ffmpeg, Settings(directory), _ => { }, default);
        Assert.True(result.Success, result.Message);
        var actual = await probe.ProbeAsync(ffprobe, result.OutputPath);
        Assert.Equal(expectedWidth, actual.Width);
        Assert.Equal(expectedHeight, actual.Height);
        Assert.Equal(0.75, (double)actual.Width / actual.Height);
        Assert.InRange(actual.Width, 2, Math.Min(maximumWidth, 48));
        Assert.InRange(actual.Height, 2, Math.Min(maximumHeight, 64));
        Assert.Equal(0, actual.Width % 2);
        Assert.Equal(0, actual.Height % 2);
        Assert.True(MediaSizeBudget.HasCompleteDuration(task.Probe.Duration, actual.Duration));
        Assert.InRange(new FileInfo(result.OutputPath).Length, 1, 50000);
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("mov")]
    [InlineData("webm")]
    [InlineData("hevc")]
    [InlineData("av1")]
    public async Task StrictVideoSize_PreservesCompleteDurationAndHonorsCustomDimensions(string target)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.mkv");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=64x48:r=10",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=1.2", "-t", "1.2", "-c:v", "ffv1", "-c:a", "pcm_s16le", input],
            TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var probe = new MediaProbeService();
        var task = new MediaTask { InputPath = input, SourceFormatId = "mkv", TargetFormatId = target,
            ConversionMode = "fast", Probe = await probe.ProbeAsync(ffprobe, input) };
        new MediaConversionOptions { TargetSizeBytes = 50000, MaxWidth = 32, MaxHeight = 32, FrameRate = 5,
            AudioBitrateKbps = 64, Channels = 1 }.ApplyTo(task.Options);
        var result = await new ConversionService().ConvertAsync(task, ffmpeg, Settings(directory), _ => { }, default);
        Assert.True(result.Success, result.Message);
        Assert.InRange(new FileInfo(result.OutputPath).Length, 1, 50000);
        var actual = await probe.ProbeAsync(ffprobe, result.OutputPath);
        Assert.True(MediaSizeBudget.HasCompleteDuration(task.Probe.Duration, actual.Duration, 5));
        Assert.Equal(32, actual.Width);
        Assert.Equal(24, actual.Height);
        Assert.Equal(1, Assert.Single(actual.AudioTracks).Channels);
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(result.OutputPath)!));
    }

    [Fact]
    public async Task ImpossibleSize_DoesNotPublishAnyOutputOrPartialFile()
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.mkv");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=64x48:r=10", "-t", "1.2", "-c:v", "ffv1", input],
            TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var task = new MediaTask { InputPath = input, SourceFormatId = "mkv", TargetFormatId = "mp4",
            Probe = await new MediaProbeService().ProbeAsync(ffprobe, input) };
        task.Options[MediaConversionOptions.TargetSizeKey] = "1000";
        var settings = Settings(directory);
        var result = await new ConversionService().ConvertAsync(task, ffmpeg, settings, _ => { }, default);
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(result.OutputPath);
        Assert.True(!Directory.Exists(settings.CustomOutputDirectory) || !Directory.EnumerateFileSystemEntries(settings.CustomOutputDirectory).Any());
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("opus")]
    public async Task StrictAudioSize_ProducesCompleteAudio(string target)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.wav");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", input], TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var probe = new MediaProbeService();
        var task = new MediaTask { InputPath = input, SourceFormatId = "wav", TargetFormatId = target,
            Probe = await probe.ProbeAsync(ffprobe, input) };
        new MediaConversionOptions { TargetSizeBytes = 24000, SampleRate = 16000, Channels = 1 }.ApplyTo(task.Options);
        var result = await new ConversionService().ConvertAsync(task, ffmpeg, Settings(directory), _ => { }, default);
        Assert.True(result.Success, result.Message);
        Assert.InRange(new FileInfo(result.OutputPath).Length, 1, 24000);
        Assert.True(MediaSizeBudget.HasCompleteDuration(task.Probe.Duration, (await probe.ProbeAsync(ffprobe, result.OutputPath)).Duration));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("avif")]
    public async Task StrictImageSize_KeepsOriginalDimensions(string target)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.png");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=128x96", "-frames:v", "1", input], TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var task = new MediaTask { InputPath = input, SourceFormatId = "png", TargetFormatId = target,
            Probe = await new MediaProbeService().ProbeAsync(ffprobe, input) };
        task.Options[MediaConversionOptions.TargetSizeKey] = "7000";
        var result = await new ConversionService().ConvertAsync(task, ffmpeg, Settings(directory), _ => { }, default);
        Assert.True(result.Success, result.Message);
        Assert.InRange(new FileInfo(result.OutputPath).Length, 1, 7000);
        var actual = await new MediaProbeService().ProbeAsync(ffprobe, result.OutputPath);
        Assert.Equal(128, actual.Width);
        Assert.Equal(96, actual.Height);
        Assert.False(task.Options.ContainsKey(MediaConversionOptions.ImageQualityKey));
    }

    private static AppSettings Settings(TemporaryDirectory directory) => new()
    { OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"), HardwareAcceleration = false, CpuThreadsPerTask = 1 };
    private static (string Ffmpeg, string Ffprobe) Engines() =>
        (LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe") ?? throw SkipException.ForSkip("Local FFmpeg is unavailable."),
         LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe") ?? throw SkipException.ForSkip("Local FFprobe is unavailable."));
}
