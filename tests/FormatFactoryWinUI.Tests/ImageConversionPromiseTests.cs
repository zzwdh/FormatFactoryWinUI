using System.Buffers.Binary;
using System.Text;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class ImageConversionPromiseTests
{
    [Theory]
    [InlineData("Lossless")]
    [InlineData(" LOSSLESS ")]
    [InlineData("\tlossless\r\n")]
    public void RequestModeNormalization_CannotBypassLosslessEncodingAndPrecisionGuard(string mode)
    {
        var request = new ConversionRequest
        {
            Inputs = [new ConversionInput("input.png", "png", new MediaProbeResult
            {
                ContentKind = ContentKind.Image, PixelFormat = "rgba"
            })],
            TargetFormatId = "webp", Mode = mode
        };
        var task = ConversionRequestFactory.CreateMediaTask(request, "ffmpeg");
        Assert.Equal("lossless", task.ConversionMode);
        var arguments = new PresetService().BuildArguments(task, "output.webp", EncoderBackend.Cpu);
        var losslessIndex = arguments.ToList().IndexOf("-lossless");
        Assert.True(losslessIndex >= 0);
        Assert.Equal("1", arguments[losslessIndex + 1]);

        task.Probe.PixelFormat = "rgba64be";
        Assert.Throws<ImagePrecisionUnsupportedException>(() =>
            new PresetService().BuildArguments(task, "output.webp", EncoderBackend.Cpu));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void BlankTaskMode_UsesDefaultConversionMode(string? mode)
    {
        var task = new MediaTask { ConversionMode = mode! };
        Assert.Equal(FormatCatalog.DefaultConversionMode, task.ConversionMode);
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("hevc")]
    [InlineData("vp9")]
    [InlineData("av1")]
    [InlineData("frames")]
    public async Task UnverifiedVideoLosslessRoute_FailsBeforeStartingEncoderOrReservingOutput(string target)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.mkv");
        File.WriteAllBytes(input, [1]);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "mkv", Kind = MediaKind.Video,
            TargetFormatId = target, ConversionMode = " LOSSLESS "
        };
        var exception = Assert.Throws<NotSupportedException>(() =>
            new PresetService().BuildArguments(task, "output.tmp", EncoderBackend.Cpu));
        Assert.Equal(LocalizationService.T("ConversionUiLosslessRouteUnsupported"), exception.Message);
        var result = await new ConversionService().ConvertAsync(task, "missing-encoder.exe",
            new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal(exception.Message, result.Message);
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("mkv", "mp4", MediaKind.Video)]
    [InlineData("wav", "flac", MediaKind.Audio)]
    [InlineData("png", "tiff", MediaKind.Image)]
    public async Task MissingPreflightProbe_ReturnsFailureWithoutStartingEncoderOrCreatingOutput(
        string source, string target, MediaKind kind)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, $"source.{source}");
        File.WriteAllBytes(input, [1]);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = source, Kind = kind,
            TargetFormatId = target, ConversionMode = "lossless"
        };
        var output = Path.Combine(directory.Path, "output");
        var service = new ConversionService();
        var encoderStarted = false;

        var result = await service.ConvertAsync(task,
            Path.Combine(directory.Path, "missing-engine", "ffmpeg.exe"),
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = output },
            progress => encoderStarted = true, default);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(result.OutputPath);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.False(service.HasActiveTasks);
        Assert.False(encoderStarted);
        Assert.False(Directory.Exists(output));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("h264", "libx264")]
    [InlineData("hevc", "libx265")]
    [InlineData("vp9", "libvpx-vp9")]
    [InlineData("av1", "libaom-av1")]
    [InlineData("frames", "png")]
    public void VideoEncodingAndFrameExport_KeepOrdinaryMode(string target, string encoder)
    {
        var task = new MediaTask
        {
            InputPath = "source.mkv", Kind = MediaKind.Video,
            TargetFormatId = target, ConversionMode = "balanced"
        };
        var arguments = new PresetService().BuildArguments(task, "output.tmp", EncoderBackend.Cpu);
        Assert.Contains(encoder, arguments);
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("webm")]
    [InlineData("mov")]
    [InlineData("ts")]
    [InlineData("avi")]
    [InlineData("flv")]
    public void LosslessVideoContainerConversion_KeepsStreamCopy(string target)
    {
        var task = new MediaTask
        {
            InputPath = "source.mkv", Kind = MediaKind.Video,
            TargetFormatId = target, ConversionMode = "lossless"
        };
        var arguments = new PresetService().BuildArguments(task, "output.tmp", EncoderBackend.Cpu).ToList();
        Assert.Equal("copy", arguments[arguments.IndexOf("-c") + 1]);
        Assert.DoesNotContain("-crf", arguments);
        Assert.DoesNotContain("-vf", arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task LosslessGifToWebp_PreservesRealFramesTimingAndLoops(int repetitions)
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.gif");
        await GenerateGifAsync(ffmpeg, input, repetitions);
        var source = ImageAnimationInfoReader.Read(input);
        Assert.Equal(5, source.FrameDurations.Count);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "gif", TargetFormatId = "webp",
            ConversionMode = "lossless", Probe = await new MediaProbeService().ProbeAsync(ffprobe, input)
        };
        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "source", HardwareAcceleration = false }, _ => { }, default);
        Assert.True(result.Success, result.Message);
        var output = ImageAnimationInfoReader.Read(result.OutputPath);
        Assert.True(source.Matches(output, requireLossless: true));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Fact]
    public async Task AnimationValidation_RejectsARealFirstFrameOutput()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.gif");
        var truncated = Path.Combine(directory.Path, "first-frame.webp");
        await GenerateGifAsync(ffmpeg, input, 0);
        var conversion = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-i", input, "-frames:v", "1", "-c:v", "libwebp", "-lossless", "1", truncated],
            TimeSpan.FromSeconds(20));
        Assert.Equal(0, conversion.ExitCode);
        var task = new MediaTask { InputPath = input, Kind = MediaKind.Image, TargetFormatId = "webp", ConversionMode = "lossless" };
        var probe = new MediaProbeService();
        Assert.True(await probe.ValidateOutputAsync(ffprobe, truncated)); // A stream alone proves too little.
        Assert.False(await probe.ValidateOutputAsync(ffprobe, truncated, task, ImageAnimationInfoReader.Read(input)));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("version")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("extended-header")]
    public async Task AnimationValidation_RejectsRealWebpWithInvalidFrameHeader(string corruption)
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.gif");
        var output = Path.Combine(directory.Path, "output.webp");
        await GenerateGifAsync(ffmpeg, input, 0);
        await GenerateAnimatedWebpAsync(ffmpeg, input, output);
        var bytes = File.ReadAllBytes(output);
        var (offset, _) = FindFrameBitstream(bytes, "VP8L", 0);
        switch (corruption)
        {
            case "signature": bytes[offset] = 0; break;
            case "version": bytes[offset + 4] |= 0x20; break;
            case "width": bytes[offset + 1] ^= 1; break;
            case "height": bytes[offset + 2] ^= 0x40; break;
            case "extended-header": bytes[20] &= 0xfd; break;
        }
        File.WriteAllBytes(output, bytes);
        Assert.Throws<InvalidDataException>(() => ImageAnimationInfoReader.Read(output));
        var task = new MediaTask { Kind = MediaKind.Image, TargetFormatId = "webp", ConversionMode = "lossless" };
        Assert.False(await new MediaProbeService().ValidateOutputAsync(ffprobe, output, task,
            ImageAnimationInfoReader.Read(input), default, ffmpeg));
    }

    [Fact]
    public async Task AnimationValidation_RejectsUndecodableLaterFrameEvenWhenHeadersAndTimingMatch()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.gif");
        var output = Path.Combine(directory.Path, "output.webp");
        await GenerateGifAsync(ffmpeg, input, 0);
        await GenerateAnimatedWebpAsync(ffmpeg, input, output);
        var bytes = File.ReadAllBytes(output);
        var (offset, length) = FindFrameBitstream(bytes, "VP8L", 4);
        Assert.True(length > 5);
        // Retain the legal signature, version and dimensions, but destroy only
        // the fifth frame's entropy stream. Container metadata still matches.
        bytes.AsSpan(offset + 5, length - 5).Fill(0xff);
        File.WriteAllBytes(output, bytes);
        var source = ImageAnimationInfoReader.Read(input);
        Assert.True(source.Matches(ImageAnimationInfoReader.Read(output), requireLossless: true));
        var task = new MediaTask { Kind = MediaKind.Image, TargetFormatId = "webp", ConversionMode = "lossless" };
        Assert.False(await new MediaProbeService().ValidateOutputAsync(ffprobe, output, task, source, default, ffmpeg));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnimationFrameVerifier_CleansScratchFileAfterFailureOrCancellation(bool cancel)
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.gif");
        var output = Path.Combine(directory.Path, "output.webp");
        await GenerateGifAsync(ffmpeg, input, 0);
        await GenerateAnimatedWebpAsync(ffmpeg, input, output);
        using var cancellation = new CancellationTokenSource();
        string? temporary = null;
        Task<bool> Decode(string frame, CancellationToken token)
        {
            temporary = frame;
            Assert.True(File.Exists(frame));
            if (!cancel) return Task.FromResult(false);
            cancellation.Cancel();
            return Task.FromCanceled<bool>(token);
        }
        var verification = ImageAnimationInfoReader.ValidateWebpFramesAsync(output, ffmpeg, cancellation.Token, Decode);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
        else Assert.False(await verification);
        Assert.NotNull(temporary);
        Assert.False(File.Exists(temporary));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporary)));
    }

    [Fact]
    public async Task AnimationValidation_DecodesLossyVp8AndAlphaTogether()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var frame = Path.Combine(directory.Path, "transparent.webp");
        var output = Path.Combine(directory.Path, "animated.webp");
        var generation = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=red:s=32x32,format=rgba,colorchannelmixer=aa=0.5",
                "-frames:v", "1", "-c:v", "libwebp", "-lossless", "0", frame], TimeSpan.FromSeconds(20));
        Assert.True(generation.ExitCode == 0, generation.StandardError);
        var still = File.ReadAllBytes(frame);
        var chunks = ExtractStillImageChunks(still);
        Assert.Contains("ALPH", Encoding.ASCII.GetString(chunks));
        BuildAnimatedWebp(output, chunks, 32, 32, 5, 200);
        var source = new ImageAnimationInfo(32, 32, Enumerable.Repeat(200, 5).ToArray(), 0);
        Assert.False(ImageAnimationInfoReader.Read(output).IsLossless);
        var task = new MediaTask { Kind = MediaKind.Image, TargetFormatId = "webp", ConversionMode = "balanced" };
        Assert.True(await new MediaProbeService().ValidateOutputAsync(ffprobe, output, task, source, default, ffmpeg));
    }

    [Fact]
    public async Task StaticLosslessWebpValidationFailure_ReportsImageValidationAndPreservesSource()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "transparent-colour.png");
        var generation = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=red:s=16x16,format=rgba,colorchannelmixer=aa=0",
                "-frames:v", "1", "-threads", "1", "-c:v", "png", input], TimeSpan.FromSeconds(20));
        Assert.True(generation.ExitCode == 0, generation.StandardError);
        var originalBytes = await File.ReadAllBytesAsync(input);
        var originalHash = System.Security.Cryptography.SHA256.HashData(originalBytes);
        var probe = new MediaProbeService();
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "png", TargetFormatId = "webp", Kind = MediaKind.Image,
            ConversionMode = "lossless", Probe = await probe.ProbeAsync(ffprobe, input)
        };
        var diagnosticOutput = Path.Combine(directory.Path, "probe.webp");
        var encoded = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-i", input, "-map", "0:V:0", "-frames:v", "1", "-c:v", "libwebp",
                "-lossless", "1", "-threads", "1", diagnosticOutput], TimeSpan.FromSeconds(20));
        Assert.True(encoded.ExitCode == 0, encoded.StandardError);
        Assert.True(await probe.ValidateOutputAsync(ffprobe, diagnosticOutput));
        Assert.NotEqual(await PixelHashAsync(ffmpeg, input), await PixelHashAsync(ffmpeg, diagnosticOutput));
        Assert.False(await probe.ValidateOutputAsync(ffprobe, diagnosticOutput, task, null, default, ffmpeg));
        File.Delete(diagnosticOutput);

        var outputDirectory = Path.Combine(directory.Path, "converted");
        Directory.CreateDirectory(outputDirectory);
        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = outputDirectory,
                CpuThreadsPerTask = 1, HardwareAcceleration = false }, _ => { }, default);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(result.OutputPath);
        Assert.Equal(LocalizationService.T("ConversionFailed", LocalizationService.T("NativeImageValidationFailed")), result.Message);
        Assert.DoesNotContain(LocalizationService.T("GeneralServiceOutputNoMediaStream"), result.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputDirectory));
        var unchangedBytes = await File.ReadAllBytesAsync(input);
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(unchangedBytes));
        Assert.False(string.IsNullOrWhiteSpace(result.LogPath));
        Assert.Contains("static-lossless-image", await File.ReadAllTextAsync(result.LogPath!));
    }


    [Fact]
    public async Task Real16BitSource_RejectsLosslessWebpWithoutCommittingOutput()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        var ffprobe = RequireEngine("ffprobe.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "sixteen-bit.png");
        var generation = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=16x16:rate=1", "-frames:v", "1",
                "-pix_fmt", "rgb48be", "-c:v", "png", input], TimeSpan.FromSeconds(20));
        Assert.True(generation.ExitCode == 0, generation.StandardError);
        var source = await new MediaProbeService().ProbeAsync(ffprobe, input);
        Assert.Equal(16, ImagePreservationPolicy.PixelBitDepth(source.PixelFormat));
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "png", TargetFormatId = "webp", ConversionMode = "lossless", Probe = source
        };
        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("gif")]
    [InlineData("bmp")]
    [InlineData("tga")]
    public void LosslessHighBitDepth_RejectsTargetsThatReducePrecision(string target)
    {
        var task = new MediaTask
        {
            InputPath = "high-depth.png", SourceFormatId = "png", TargetFormatId = target, ConversionMode = "lossless",
            Probe = new MediaProbeResult { Kind = MediaKind.Image, PixelFormat = "rgba64be" }
        };
        Assert.Throws<ImagePrecisionUnsupportedException>(() => ImagePreservationPolicy.ValidateFfmpegInput(task));
    }

    [Fact]
    public async Task UnsupportedAnimationTarget_FailsBeforeStartingAnyEncoder()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.webp");
        File.WriteAllBytes(input, [1]);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "webp", TargetFormatId = "png", ConversionMode = "lossless",
            Probe = new MediaProbeResult { Kind = MediaKind.Image, IsAnimated = true, PageCount = 5 }
        };
        var result = await new ConversionService().ConvertAsync(task, "missing-encoder.exe",
            new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void MultipageFfmpegRoute_RejectsPageFlatteningEvenForTiffOutput()
    {
        var task = new MediaTask
        {
            InputPath = "pages.tiff", SourceFormatId = "tiff", TargetFormatId = "tiff",
            Probe = new MediaProbeResult { Kind = MediaKind.Image, PageCount = 3 }
        };
        Assert.Throws<NotSupportedException>(() => ImagePreservationPolicy.ValidateFfmpegInput(task));
    }

    [Fact]
    public void WordPdfScript_DisablesMacrosBeforeOpeningAndQuotesPaths()
    {
        var script = ExternalDocumentConverter.BuildWordPdfScript(@"C:\input's.doc", @"C:\output's.pdf");
        var disable = script.IndexOf("$word.AutomationSecurity = 3", StringComparison.Ordinal);
        var open = script.IndexOf("$word.Documents.Open", StringComparison.Ordinal);
        Assert.True(disable >= 0 && disable < open);
        Assert.Contains("input''s.doc", script);
        Assert.Contains("output''s.pdf", script);
        Assert.Contains("$document.Close($false)", script);
    }

    [Fact]
    public async Task HardwarePreflight_CancelledWaiterDoesNotCancelOrPoisonSharedProbe()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new HardwareCapabilitiesService((_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return completion.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = service.CanEncodeAsync("ffmpeg.exe", EncoderBackend.Nvidia, false, cancellation.Token);
        var activeWaiter = service.CanEncodeAsync("ffmpeg.exe", EncoderBackend.Nvidia, false);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(activeWaiter.IsCompleted);
        completion.SetResult(true);
        Assert.True(await activeWaiter);
        Assert.True(await service.CanEncodeAsync("ffmpeg.exe", EncoderBackend.Nvidia, false));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Real16BitPngToTiff_PreservesPixelsDespiteMissingProbeAndObserverFailure()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "sixteen-bit.png");
        await GenerateHighDepthPngAsync(ffmpeg, input);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "png", TargetFormatId = "tiff", ConversionMode = "lossless",
            Probe = new MediaProbeResult { Kind = MediaKind.Image, PageCount = 1 }
        };
        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "source" }, progress => { if (progress.Percent == 100) throw new InvalidOperationException("Observer failed after commit."); }, default);
        Assert.True(result.Success, result.Message);
        Assert.Equal(16, ImagePreservationPolicy.PixelBitDepth(task.Probe.PixelFormat));
        var sourceHash = await PixelHashAsync(ffmpeg, input);
        var outputHash = await PixelHashAsync(ffmpeg, result.OutputPath);
        Assert.Equal(sourceHash, outputHash);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("avif")]
    [InlineData("webp")]
    public async Task MissingNativePrecision_CannotBypassReal16BitRejection(string target)
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "sixteen-bit.png");
        await GenerateHighDepthPngAsync(ffmpeg, input);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "png", TargetFormatId = target, ConversionMode = "lossless",
            Probe = new MediaProbeResult { Kind = MediaKind.Image, PageCount = 1 }
        };
        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task LosslessAvif_CannotCommitWhenEncoderDropsSourceAlpha()
    {
        var ffmpeg = RequireEngine("ffmpeg.exe");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "transparent.png");
        var generation = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=red:s=16x16,format=rgba,colorchannelmixer=aa=0.5",
                "-frames:v", "1", "-c:v", "png", input], TimeSpan.FromSeconds(20));
        Assert.True(generation.ExitCode == 0, generation.StandardError);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "png", TargetFormatId = "avif", ConversionMode = "lossless",
            Probe = new MediaProbeResult { Kind = MediaKind.Image, PageCount = 1 }
        };
        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Theory]
    [InlineData("jxl")]
    [InlineData("ico")]
    [InlineData("frames")]
    [InlineData("mp4")]
    public void LosslessImage_RejectsUnknownDefaultEncodersAndResizingRoutes(string target)
    {
        var task = new MediaTask
        {
            InputPath = "input.png", SourceFormatId = "png", TargetFormatId = target, ConversionMode = "lossless",
            Probe = new MediaProbeResult { Kind = MediaKind.Image }
        };
        if (target == "frames")
            Assert.Throws<NotSupportedException>(() => ImagePreservationPolicy.ValidateFfmpegInput(task));
        else
            Assert.Throws<ImagePrecisionUnsupportedException>(() => ImagePreservationPolicy.ValidateFfmpegInput(task));
    }

    [Fact]
    public void ActualMultipageTiff_CannotUseStaleSinglePageProbe()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "pages.tiff");
        // Two valid zero-entry TIFF directories are enough to expose a stale page count.
        File.WriteAllBytes(input, [0x49, 0x49, 42, 0, 8, 0, 0, 0, 0, 0, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(2, TiffPageCountReader.Read(input));
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "tiff", TargetFormatId = "png",
            Probe = new MediaProbeResult { Kind = MediaKind.Image, PageCount = 1 }
        };
        Assert.Throws<NotSupportedException>(() => ImagePreservationPolicy.ValidateFfmpegInput(task));
    }

    [Fact]
    public void TiffPageReader_RejectsCyclesAndOutOfBoundsBigTiffCounts()
    {
        using var directory = new TemporaryDirectory();
        var cyclic = Path.Combine(directory.Path, "cycle.tiff");
        File.WriteAllBytes(cyclic, [0x49, 0x49, 42, 0, 8, 0, 0, 0, 0, 0, 8, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => TiffPageCountReader.Read(cyclic));
        var huge = Path.Combine(directory.Path, "huge.tiff");
        File.WriteAllBytes(huge, [0x49, 0x49, 43, 0, 8, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0,
            255, 255, 255, 255, 255, 255, 255, 255]);
        Assert.Throws<InvalidDataException>(() => TiffPageCountReader.Read(huge));
    }

    [Fact]
    public void AnimationReader_RejectsOversizedRiffChunksBeforeAllocatingPayloads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized.webp");
        File.WriteAllBytes(path, [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 12, 0, 0, 0,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'A', (byte)'N', (byte)'M', (byte)'F',
            255, 255, 255, 255]);
        Assert.Throws<InvalidDataException>(() => ImageAnimationInfoReader.Read(path));
    }

    [Fact]
    public void AnimationReader_RejectsGifWithoutTrailerAndHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "incomplete.gif");
        File.WriteAllBytes(path, [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            1, 0, 1, 0, 0, 0, 0, 0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 1, 0]);
        Assert.Throws<InvalidDataException>(() => ImageAnimationInfoReader.Read(path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageAnimationInfoReader.Read(path, cancellation.Token));
    }

    private static async Task GenerateHighDepthPngAsync(string ffmpeg, string input)
    {
        var generation = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=16x16:rate=1", "-frames:v", "1",
                "-pix_fmt", "rgba64be", "-c:v", "png", input], TimeSpan.FromSeconds(20));
        Assert.True(generation.ExitCode == 0, generation.StandardError);
    }

    private static async Task<string> PixelHashAsync(string ffmpeg, string path)
    {
        var result = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-i", path, "-frames:v", "1", "-pix_fmt", "rgba64le", "-f", "hash", "-hash", "sha256", "-"],
            TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static async Task GenerateGifAsync(string ffmpeg, string input, int repetitions)
    {
        var generation = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=32x32:rate=5:duration=1", "-loop",
                repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture), input], TimeSpan.FromSeconds(20));
        Assert.True(generation.ExitCode == 0, generation.StandardError);
    }

    private static async Task GenerateAnimatedWebpAsync(string ffmpeg, string input, string output)
    {
        var generation = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-i", input, "-map", "0:V:0", "-fps_mode", "passthrough",
                "-c:v", "libwebp_anim", "-lossless", "1", "-loop", "0", output], TimeSpan.FromSeconds(20));
        Assert.True(generation.ExitCode == 0, generation.StandardError);
    }

    private static (int Offset, int Length) FindFrameBitstream(byte[] bytes, string tag, int frameIndex)
    {
        var frame = 0;
        for (var position = 12; position + 8 <= bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4)));
            if (Encoding.ASCII.GetString(bytes, position, 4) == "ANMF")
            {
                if (frame++ == frameIndex)
                    for (var chunk = position + 24; chunk + 8 <= position + 8 + length;)
                    {
                        var chunkLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunk + 4)));
                        if (Encoding.ASCII.GetString(bytes, chunk, 4) == tag) return (chunk + 8, chunkLength);
                        chunk += 8 + chunkLength + (chunkLength & 1);
                    }
            }
            position += 8 + length + (length & 1);
        }
        throw new InvalidDataException($"Frame {frameIndex} has no {tag} bitstream.");
    }

    private static byte[] ExtractStillImageChunks(byte[] bytes)
    {
        using var extracted = new MemoryStream();
        for (var position = 12; position + 8 <= bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4)));
            var total = 8 + length + (length & 1);
            if (Encoding.ASCII.GetString(bytes, position, 4) is "ALPH" or "VP8 " or "VP8L")
                extracted.Write(bytes, position, total);
            position += total;
        }
        return extracted.ToArray();
    }

    private static void BuildAnimatedWebp(string output, byte[] imageChunks, int width, int height, int count, int duration)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        void UInt24(int value) { writer.Write((byte)value); writer.Write((byte)(value >> 8)); writer.Write((byte)(value >> 16)); }
        writer.Write("RIFF"u8); writer.Write(0u); writer.Write("WEBPVP8X"u8); writer.Write(10u);
        writer.Write((byte)0x12); UInt24(0); UInt24(width - 1); UInt24(height - 1);
        writer.Write("ANIM"u8); writer.Write(6u); writer.Write(0u); writer.Write((ushort)0);
        for (var frame = 0; frame < count; frame++)
        {
            writer.Write("ANMF"u8); writer.Write(checked((uint)(16 + imageChunks.Length)));
            UInt24(0); UInt24(0); UInt24(width - 1); UInt24(height - 1); UInt24(duration); writer.Write((byte)2);
            writer.Write(imageChunks);
        }
        stream.Position = 4;
        writer.Write(checked((uint)(stream.Length - 8)));
        File.WriteAllBytes(output, stream.ToArray());
    }

    private static string RequireEngine(string name) =>
        LocalFfmpegSmokeTests.FindExecutable(name) ?? throw SkipException.ForSkip("Local FFmpeg is unavailable.");
}
