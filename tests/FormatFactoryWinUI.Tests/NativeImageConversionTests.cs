using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class NativeImageConversionTests
{
    [Theory]
    [InlineData("png", "tiff")]
    [InlineData("webp", "raw")]
    [InlineData("webp", "heif")]
    public async Task LosslessPrecisionFailure_DoesNotFallbackOrUseLossyIntermediate(string target, string source)
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        task.SourceFormatId = source;
        task.TargetFormatId = target;
        task.ConversionMode = "lossless";
        var codecs = new FakeCodecs { HighBitDepth = true };
        var service = new NativeImageConversionService(codecs,
            (_, _, _, _, _) => throw new InvalidOperationException("Do not encode an 8-bit intermediate for a high-depth lossless request."));
        var result = await service.ConvertAsync(task, task.InputPath, new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.Equal("lossless", codecs.LastMode);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("avis", "mif1", true)]
    [InlineData("avif", "avis", true)]
    [InlineData("avif", "mif1", false)]
    public void AnimationDetection_RecognizesAvifSequenceBrands(string major, string compatible, bool expected)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sequence.avif");
        using var memory = new MemoryStream();
        memory.Write(new byte[] { 0, 0, 0, 20 });
        memory.Write("ftyp"u8);
        memory.Write(System.Text.Encoding.ASCII.GetBytes(major));
        memory.Write(new byte[4]);
        memory.Write(System.Text.Encoding.ASCII.GetBytes(compatible));
        File.WriteAllBytes(path, memory.ToArray());
        Assert.Equal(expected, ImageAnimationDetector.HasAnimation(path));
    }

    [Fact]
    public async Task NativeImage_ValidatesAndCommitsWithoutFfmpeg()
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        var codecs = new FakeCodecs();
        var fallback = false;
        var service = new NativeImageConversionService(codecs, (_, _, _, _, _) =>
        {
            fallback = true;
            return Task.FromResult(new ConversionResult(false, false, "", "unexpected", ""));
        });
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.True(result.Success, result.Message);
        Assert.False(fallback);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.OutputPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Theory]
    [InlineData("lossless")]
    [InlineData("Lossless")]
    public async Task LosslessCodecWithoutPixelVerifier_CannotCommitEvenWhenProbeOmitsPrecision(string mode)
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        task.ConversionMode = mode;
        var service = new NativeImageConversionService(new FakeCodecs(),
            (_, _, _, _, _) => throw new InvalidOperationException("Unproven lossless output must not fall back."));
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task LosslessPixelVerification_ControlsCommitAndPreservesCancellation(bool matches, bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        task.ConversionMode = "lossless";
        var codecs = new VerifyingCodecs(matches, cancel);
        var service = new NativeImageConversionService(codecs,
            (_, _, _, _, _) => throw new InvalidOperationException("Lossless verification must not fall back."));
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.True(codecs.Verified);
        Assert.Equal(matches, result.Success);
        Assert.Equal(cancel, result.Cancelled);
        Assert.Equal(matches ? 2 : 1, Directory.GetFiles(directory.Path).Length);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Fact]
    public async Task NativeImage_CancellationNeverFallsBackOrCommits()
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        var service = new NativeImageConversionService(new FakeCodecs { Cancel = true },
            (_, _, _, _, _) => throw new InvalidOperationException("Cancellation must not invoke FFmpeg."));
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.True(result.Cancelled);
        Assert.False(File.Exists(Path.Combine(directory.Path, "input.png")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Fact]
    public async Task NativeImage_FallbackReleasesReservationBeforeChoosingOutputName()
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        var service = new NativeImageConversionService(new FakeCodecs { Fail = true },
            async (model, _, settings, _, token) =>
            {
                using var output = new OutputPathService().Reserve(model, settings);
                await File.WriteAllBytesAsync(output.FinalPath, [9], token);
                return new(true, false, output.FinalPath, "fallback", "");
            });
        var result = await service.ConvertAsync(task, task.InputPath, new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.True(result.Success);
        Assert.Equal(Path.Combine(directory.Path, "input.png"), result.OutputPath);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Fact]
    public void AnimationDetection_RecognizesApngEvenWhenStillCodecReportsOneFrame()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "animated.png");
        File.WriteAllBytes(path, [137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 8, (byte)'a', (byte)'c', (byte)'T', (byte)'L',
            0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.True(ImageAnimationDetector.HasAnimation(path));
    }

    [Fact]
    public void AnimationDetection_ReadsWebpAnimationFlag()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "animated.webp");
        File.WriteAllBytes(path, [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 22, 0, 0, 0,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)'X',
            10, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.True(ImageAnimationDetector.HasAnimation(path));
    }

    [Fact]
    public async Task PreservePolicy_FailureCannotSilentlyFallback()
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        task.Options["imageColorPolicy"] = "preserve";
        var service = new NativeImageConversionService(new FakeCodecs(),
            (_, _, _, _, _) => throw new InvalidOperationException("ICC failure must not invoke FFmpeg."));
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Theory]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    public async Task PreservePolicy_RejectsStaleOptionAfterChangingTarget(string target)
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        task.TargetFormatId = target;
        task.Options["imageColorPolicy"] = "preserve";
        var service = new NativeImageConversionService(new FakeCodecs(),
            (_, _, _, _, _) => throw new InvalidOperationException("Unsupported ICC target must not invoke FFmpeg."));
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task NativeImage_DoesNotDropTiffPages()
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        var service = new NativeImageConversionService(new FakeCodecs { PageCount = 2 },
            (_, _, _, _, _) => throw new InvalidOperationException("Do not discard extra pages."));
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(directory.Path, "input.png")));
    }

    [Fact]
    public async Task MultipageNativeFailure_CannotFallbackToSingleFrameFfmpeg()
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path);
        task.TargetFormatId = "tiff";
        var service = new NativeImageConversionService(new FakeCodecs { PageCount = 2, Fail = true },
            (_, _, _, _, _) => throw new InvalidOperationException("A multi-page codec failure must not flatten the document."));
        var result = await service.ConvertAsync(task, task.InputPath,
            new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial*"));
    }

    [Fact]
    public async Task ImageImport_UsesInstalledDecoderWithoutFfprobe()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "phone.heic");
        await File.WriteAllBytesAsync(path, [1]);
        var probe = await new MediaProbeService(imageCodecs: new FakeCodecs()).ProbeAsync("", path);
        Assert.Equal(ContentKind.Image, probe.ContentKind);
        Assert.Equal(2, probe.Width);
    }

    [Theory]
    [InlineData("phone.HEIC", "heif")]
    [InlineData("phone.heif", "heif")]
    [InlineData("camera.cr3", "raw")]
    [InlineData("camera.nef", "raw")]
    [InlineData("camera.dng", "raw")]
    public void SystemImageImports_AreInSingleFormatCatalog(string path, string expected)
    {
        var format = Assert.IsType<FormatDescriptor>(FormatCatalog.FindByExtension(path));
        Assert.Equal(expected, format.Id);
        Assert.True(format.CanImport);
        Assert.False(format.CanExport);
        Assert.True(new ConversionEngines.DocumentConversionRoutePlanner().CanPlan(format.Id, "pdf"));
    }

    private static MediaTask ImageTask(string directory)
    {
        var path = Path.Combine(directory, "input.bmp");
        File.WriteAllBytes(path, [1]);
        return new MediaTask { InputPath = path, Kind = MediaKind.Image, SourceFormatId = "bmp", TargetFormatId = "png" };
    }

    private sealed class VerifyingCodecs(bool matches, bool cancel) : IImageCodecService
    {
        private readonly FakeCodecs _inner = new();
        public bool Verified { get; private set; }
        public bool CanEncode(string format) => _inner.CanEncode(format);
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken token = default) => _inner.ProbeAsync(path, token);
        public Task TranscodeAsync(string input, string output, string target, string mode, CancellationToken token = default) =>
            _inner.TranscodeAsync(input, output, target, mode, token);
        public Task<bool> ValidateLosslessAsync(string input, string output, CancellationToken token = default)
        {
            Verified = true;
            if (cancel) throw new OperationCanceledException();
            return Task.FromResult(matches);
        }
    }

    private sealed class FakeCodecs : IImageCodecService
    {
        public bool Cancel { get; init; }
        public bool Fail { get; init; }
        public bool HighBitDepth { get; init; }
        public string? LastMode { get; private set; }
        public int PageCount { get; init; } = 1;
        public bool CanEncode(string format) => format is "png" or "bmp" or "tiff" or "jpg";
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<MediaProbeResult?>(new MediaProbeResult { ContentKind = ContentKind.Image, Width = 2, Height = 2, PageCount = PageCount });
        public async Task TranscodeAsync(string input, string output, string target, string mode, CancellationToken cancellationToken = default)
        {
            LastMode = mode;
            if (HighBitDepth && mode == "lossless") throw new ImagePrecisionUnsupportedException();
            await File.WriteAllBytesAsync(output, [1, 2, 3], cancellationToken);
            if (Cancel) throw new OperationCanceledException();
            if (Fail) throw new NotSupportedException("Test codec failure");
        }
    }
}
