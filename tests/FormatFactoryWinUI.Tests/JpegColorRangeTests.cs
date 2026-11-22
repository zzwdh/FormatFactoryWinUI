using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class JpegColorRangeTests
{
    [Fact]
    public void RejectedColorRange_ExplainsTheRestrictionAndChoicesInEveryLanguage()
    {
        const string key = "ConversionUiLosslessJpegRangeUnsupported";
        var task = CreateTask("yuv420p", "tv");
        try
        {
            LocalizationService.SetLanguage("en-US");
            var english = LocalizationService.T(key);
            foreach (var language in LocalizationService.LanguageOptions.Where(item => item.Key != "auto"))
            {
                LocalizationService.SetLanguage(language.Key);
                var error = Assert.Throws<NotSupportedException>(() =>
                    new PresetService().BuildArguments(task, "output.jpg", EncoderBackend.Cpu));
                Assert.Equal(LocalizationService.T(key), error.Message);
                Assert.DoesNotContain(key, error.Message);
                Assert.False(string.IsNullOrWhiteSpace(error.Message));
                if (language.Key != "en-US") Assert.NotEqual(english, error.Message);
            }
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Theory]
    [InlineData("yuv420p", "tv")]
    [InlineData("yuv422p", "mpeg")]
    [InlineData("yuv444p", "limited")]
    [InlineData("yuv420p", "")]
    [InlineData("yuv422p", "unknown")]
    [InlineData("yuv444p", "unspecified")]
    [InlineData("yuv420p", null)]
    [InlineData("yuv420p", "future-range")]
    [InlineData("YUV420P", " TV ")]
    public void LosslessJpeg_NonFullRangeYuvRejectsBeforeBuildingEncoderArguments(string pixelFormat, string? colorRange)
    {
        var task = CreateTask(pixelFormat, colorRange);

        var error = Assert.Throws<NotSupportedException>(() =>
            new PresetService().BuildArguments(task, "output.jpg", EncoderBackend.Cpu));

        Assert.Equal(LocalizationService.T("ConversionUiLosslessJpegRangeUnsupported"), error.Message);
        Assert.Equal(pixelFormat, task.Probe.PixelFormat);
        Assert.Equal(colorRange, task.Probe.ColorRange);
        Assert.Empty(task.Options);
    }

    [Theory]
    [InlineData("pc")]
    [InlineData("jpeg")]
    [InlineData("full")]
    [InlineData(" PC ")]
    [InlineData("JpEg")]
    [InlineData(" FULL ")]
    public void LosslessJpeg_FullRangeYuvKeepsItsSamplesAndRange(string colorRange)
    {
        foreach (var pixelFormat in new[] { "yuv420p", "yuv422p", "yuv444p" })
        {
            var task = CreateTask(pixelFormat, colorRange);

            AssertUnchangedJpegArguments(task);

            Assert.Equal(pixelFormat, task.Probe.PixelFormat);
            Assert.Equal(colorRange, task.Probe.ColorRange);
        }
    }

    [Theory]
    [InlineData("yuvj420p", "")]
    [InlineData("yuvj422p", "unknown")]
    [InlineData("yuvj444p", "unspecified")]
    public void LosslessJpeg_FullRangePixelFormatDoesNotRequireDuplicateRangeMetadata(string pixelFormat, string colorRange)
        => AssertUnchangedJpegArguments(CreateTask(pixelFormat, colorRange));

    [Theory]
    [InlineData("rgb24")]
    [InlineData("bgr24")]
    [InlineData("rgba")]
    [InlineData("bgra")]
    [InlineData("gray")]
    public void LosslessJpeg_NonYuvInputsKeepExistingRouteAndOutputValidation(string pixelFormat)
        => AssertUnchangedJpegArguments(CreateTask(pixelFormat, ""));

    [Theory]
    [InlineData("balanced")]
    [InlineData("quality")]
    [InlineData("fast")]
    public void OrdinaryJpegModes_DoNotApplyLosslessRangeRestriction(string mode)
    {
        var task = CreateTask("yuv420p", "tv");
        task.ConversionMode = mode;

        var arguments = new PresetService().BuildArguments(task, "output.jpg", EncoderBackend.Cpu).ToList();

        Assert.Equal("mjpeg", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.Equal("tv", task.Probe.ColorRange);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("tiff")]
    [InlineData("webp")]
    [InlineData("avif")]
    [InlineData("bmp")]
    [InlineData("tga")]
    public void OtherLosslessImageTargets_KeepExistingRangeHandling(string target)
    {
        var task = CreateTask("yuv420p", "tv");
        task.TargetFormatId = target;

        var arguments = new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu);

        Assert.DoesNotContain("ljpeg", arguments);
        Assert.Equal("tv", task.Probe.ColorRange);
    }

    [Theory]
    [InlineData(MediaKind.Audio)]
    [InlineData(MediaKind.Video)]
    public void NonImageInputs_RemainOutsideImageRangePolicy(MediaKind kind)
    {
        var task = CreateTask("yuv420p", "tv");
        task.Kind = kind;

        Assert.Null(ImagePreservationPolicy.ValidateFfmpegInput(task));
    }

    [Fact]
    public void CancelledInputCheck_RemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ImagePreservationPolicy.ValidateFfmpegInput(CreateTask("yuv420p", "tv"), cancellation.Token));
    }

    [Fact]
    public async Task RealLimitedRangeSource_IsRejectedAfterProbeBeforeOutputReservation()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.avif");
        await GenerateSourceAsync(ffmpeg, input, "tv");
        var sourceBytes = await File.ReadAllBytesAsync(input);
        var task = CreateTask("", "");
        task.InputPath = input;
        var output = Path.Combine(directory.Path, "converted");
        var service = new ConversionService();
        var progressReported = false;

        var result = await service.ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = output, HardwareAcceleration = false },
            _ => progressReported = true, default);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(result.OutputPath);
        Assert.Equal(LocalizationService.T("ConversionUiLosslessJpegRangeUnsupported"), result.Message);
        Assert.Equal("yuv420p", task.Probe.PixelFormat);
        Assert.Equal("tv", task.Probe.ColorRange);
        Assert.False(progressReported);
        Assert.False(service.HasActiveTasks);
        Assert.False(Directory.Exists(output));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(input));
    }

    [Theory]
    [InlineData("pc")]
    [InlineData("rgb")]
    public async Task RealFullRangeAndRgbSources_StillProducePixelIdenticalJpeg(string sourceKind)
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, sourceKind == "rgb" ? "source.png" : "source.avif");
        await GenerateSourceAsync(ffmpeg, input, sourceKind);
        var sourceBytes = await File.ReadAllBytesAsync(input);
        var task = CreateTask("", "");
        task.InputPath = input;
        task.SourceFormatId = sourceKind == "rgb" ? "png" : "avif";

        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "source", HardwareAcceleration = false, CpuThreadsPerTask = 1 },
            _ => { }, default);

        Assert.True(result.Success, result.Message);
        Assert.True(await new MediaProbeService().ValidateOutputAsync(ffprobe, result.OutputPath, task,
            sourceAnimation: null, ffmpegPath: ffmpeg));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(input));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    private static MediaTask CreateTask(string pixelFormat, string? colorRange) => new()
    {
        InputPath = "synthetic.avif", SourceFormatId = "avif", TargetFormatId = "jpg",
        Kind = MediaKind.Image, ConversionMode = "lossless",
        Probe = new MediaProbeResult
        {
            Kind = MediaKind.Image, VideoCodec = "av1", Width = 32, Height = 24,
            PixelFormat = pixelFormat, ColorRange = colorRange!
        }
    };

    private static void AssertUnchangedJpegArguments(MediaTask task)
    {
        var arguments = new PresetService().BuildArguments(task, "output.jpg", EncoderBackend.Cpu).ToList();
        Assert.Equal("ljpeg", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.DoesNotContain("-pix_fmt", arguments);
        Assert.DoesNotContain("-color_range", arguments);
        Assert.DoesNotContain("-strict", arguments);
    }

    private static async Task GenerateSourceAsync(string ffmpeg, string path, string sourceKind)
    {
        string[] arguments = sourceKind == "rgb"
            ? ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc=s=32x24:d=0.04",
                "-frames:v", "1", "-c:v", "png", "-pix_fmt", "rgb24", path]
            : ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=s=32x24:d=0.04",
                "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-crf", "0", "-cpu-used", "8",
                "-pix_fmt", "yuv420p", "-color_range", sourceKind, path];
        var generated = await ProcessRunner.RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
    }

    private static string RequireEngine(string name) =>
        LocalFfmpegSmokeTests.FindExecutable(name) ?? throw SkipException.ForSkip("Local FFmpeg is unavailable.");
}
