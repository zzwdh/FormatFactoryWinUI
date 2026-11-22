using System.Security.Cryptography;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class YcgcoConversionTests
{
    private const string MatrixFilter = "zscale=matrixin=ycgco:matrix=bt709,format=yuv444p16le";

    [Theory]
    [InlineData(EncoderBackend.Cpu, false)]
    [InlineData(EncoderBackend.Cpu, true)]
    [InlineData(EncoderBackend.Intel, false)]
    [InlineData(EncoderBackend.Intel, true)]
    [InlineData(EncoderBackend.Nvidia, false)]
    [InlineData(EncoderBackend.Nvidia, true)]
    [InlineData(EncoderBackend.Amd, false)]
    [InlineData(EncoderBackend.Amd, true)]
    [InlineData(EncoderBackend.MediaFoundation, false)]
    [InlineData(EncoderBackend.MediaFoundation, true)]
    public void YcgcoInput_ConvertsMatrixBeforeHardwareEncoding(EncoderBackend backend, bool hardwareDecode)
    {
        var task = CreateTask();
        var arguments = new PresetService().BuildArguments(task, "output.mp4", backend, hardwareDecode).ToList();

        Assert.Equal(MatrixFilter, Option(arguments, "-vf"));
        Assert.Equal("bt709", Option(arguments, "-colorspace"));
        Assert.DoesNotContain("-hwaccel", arguments);
        Assert.Equal(1, arguments.Count(value => value == "-vf"));
        Assert.Equal(1, arguments.Count(value => value == "-colorspace"));
        Assert.Equal("pc", Option(arguments, "-color_range"));
        Assert.Equal("smpte2084", Option(arguments, "-color_trc"));
        Assert.Equal("bt2020", Option(arguments, "-color_primaries"));
        Assert.Equal("ycgco", task.Probe.ColorSpace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OddInput_ConvertsBeforeExistingPaddingOrResize(bool resize)
    {
        var task = CreateTask();
        task.Probe.Width = 65;
        task.Probe.Height = 33;
        if (resize) new MediaConversionOptions { MaxWidth = 32, MaxHeight = 16, FrameRate = 5 }.ApplyTo(task.Options);
        var arguments = new PresetService().BuildArguments(task, "output.mp4", EncoderBackend.Cpu).ToList();
        var filter = Option(arguments, "-vf");

        Assert.StartsWith(MatrixFilter + ",", filter);
        Assert.Equal(1, arguments.Count(value => value == "-vf"));
        if (resize)
        {
            Assert.Contains(",scale=", filter);
            Assert.Contains(",fps=5", filter);
            Assert.DoesNotContain("pad=", filter);
        }
        else Assert.EndsWith("pad=ceil(iw/2)*2:ceil(ih/2)*2:0:0", filter);
    }

    [Theory]
    [InlineData("avi")]
    [InlineData("webm")]
    [InlineData("av1")]
    public void OtherVideoEncoders_ReceiveTheSameExplicitMatrixConversion(string target)
    {
        var task = CreateTask();
        task.TargetFormatId = target;
        var arguments = new PresetService().BuildArguments(task, "output." + target, EncoderBackend.Cpu).ToList();

        Assert.StartsWith(MatrixFilter, Option(arguments, "-vf"));
        Assert.Equal("bt709", Option(arguments, "-colorspace"));
    }

    [Fact]
    public void ToneMapping_ConvertsYcgcoBeforeItsExistingHdrFilter()
    {
        var task = CreateTask();
        task.TargetFormatId = "flv";
        task.Probe.IsHdr = true;
        var arguments = new PresetService().BuildArguments(task, "output.flv", EncoderBackend.Cpu).ToList();

        Assert.StartsWith(MatrixFilter + ",zscale=t=linear", Option(arguments, "-vf"));
        Assert.Contains("tonemap=hable", Option(arguments, "-vf"));
        Assert.Equal(1, arguments.Count(value => value == "-vf"));
        Assert.Equal("bt709", Option(arguments, "-colorspace"));
        Assert.Equal("bt709", Option(arguments, "-color_trc"));
        Assert.Equal("bt709", Option(arguments, "-color_primaries"));
        Assert.Equal("tv", Option(arguments, "-color_range"));
    }

    [Fact]
    public void LosslessCopy_KeepsTheOriginalSamplesAndMatrix()
    {
        var task = CreateTask();
        task.TargetFormatId = "mkv";
        task.ConversionMode = "lossless";
        var arguments = new PresetService().BuildArguments(task, "output.mkv", EncoderBackend.Cpu).ToList();

        Assert.Equal("copy", Option(arguments, "-c"));
        Assert.DoesNotContain("-vf", arguments);
        Assert.DoesNotContain("-colorspace", arguments);
        Assert.Equal("ycgco", task.Probe.ColorSpace);
        Assert.Equal("pc", task.Probe.ColorRange);
    }

    [Fact]
    public async Task HardwareFallback_DoesNotRepeatTheSameAttemptAsHardwareDecode()
    {
        using var directory = new TemporaryDirectory();
        var temporary = Path.Combine(directory.Path, "output.tmp.mp4");
        var codecs = new List<string>();
        var hardware = new HardwareCapabilitiesService((_, backend, _, _) =>
            Task.FromResult(backend == EncoderBackend.Nvidia));
        var encoder = new MediaEncodingService((_, _, arguments, _, _, _) =>
        {
            Assert.DoesNotContain("-hwaccel", arguments);
            var codec = arguments.Contains("h264_nvenc") ? "h264_nvenc" : "libx264";
            codecs.Add(codec);
            File.WriteAllText(temporary, codec);
            return Task.FromResult(new FfmpegProcessResult(codec == "libx264" ? 0 : 1,
                ["Synthetic encoder failure"]));
        }, hardware);

        var failure = await encoder.EncodeAsync(CreateTask(), "synthetic-ffmpeg.exe", temporary,
            Path.Combine(directory.Path, "task.log"),
            new(new MediaConversionOptions(), new AppSettings { HardwareAcceleration = true }, null, null, null),
            _ => { }, default);

        Assert.Null(failure);
        Assert.Equal(new[] { "h264_nvenc", "libx264" }, codecs);
    }

    [Theory]
    [InlineData(false, false, "tv")]
    [InlineData(false, true, "tv")]
    [InlineData(true, false, "tv")]
    [InlineData(false, false, "pc")]
    public async Task RealYcgcoVideo_ConvertsWithoutRelabelingOrChangingTheSource(bool odd, bool unknownMetadata, string range)
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        using var directory = new TemporaryDirectory();
        var reference = Path.Combine(directory.Path, "reference.mkv");
        var input = Path.Combine(directory.Path, "synthetic-ycgco.mkv");
        var patchWidth = odd ? 68 : 64;
        var patchHeight = odd ? 34 : 32;
        var patches = $"color=black:s={patchWidth}x{patchHeight}:r=5:d=0.4,format=rgb24," +
            $"drawbox=x=0:y=0:w=16:h={patchHeight}:color=red:t=fill," +
            $"drawbox=x=16:y=0:w=16:h={patchHeight}:color=lime:t=fill," +
            $"drawbox=x=32:y=0:w=16:h={patchHeight}:color=blue:t=fill," +
            $"drawbox=x=48:y=0:w=16:h={patchHeight}:color=white:t=fill";
        await RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", patches,
             "-vf", $"scale=out_color_matrix=bt709:out_range={range},format=yuv444p", "-c:v", "ffv1",
             "-colorspace", "bt709", "-color_trc", "bt709", "-color_primaries", "bt709", "-color_range", range, reference]);
        // Use the separate colorspace implementation to produce actual YCoCg samples,
        // rather than only assigning that matrix tag to ordinary YUV pixels.
        var sourceFilter = $"colorspace=iall=bt709:all=bt709:space=ycgco:range={range}:format=yuv444p" +
            (odd ? ",crop=65:33:0:0" : string.Empty);
        await RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", reference, "-vf", sourceFilter,
             "-c:v", "ffv1", "-colorspace", "ycgco", "-color_range", range,
             "-color_trc", unknownMetadata ? "unknown" : "bt709",
             "-color_primaries", unknownMetadata ? "unknown" : "bt709", input]);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        var probeService = new MediaProbeService();
        var probe = await probeService.ProbeAsync(ffprobe, input);
        Assert.Equal("ycgco", probe.ColorSpace);
        var referencePixels = await ReadPixelsAsync(ffmpeg, reference, Path.Combine(directory.Path, "reference.rgb"));

        try
        {
            foreach (var resize in new[] { false, true })
            {
                var task = CreateTask();
                task.InputPath = input;
                task.Probe = probe;
                if (resize) new MediaConversionOptions { MaxWidth = 32, MaxHeight = 16 }.ApplyTo(task.Options);
                var output = Path.Combine(directory.Path, resize ? "resized.mp4" : "output.mp4");
                var arguments = new PresetService().BuildArguments(task, output, EncoderBackend.Cpu, cpuThreads: 1);
                await RunAsync(ffmpeg, arguments);
                var result = await probeService.ProbeAsync(ffprobe, output);
                Assert.Equal("h264", result.VideoCodec);
                Assert.Equal("bt709", result.ColorSpace);
                Assert.Equal(range, result.ColorRange);
                Assert.Equal(probe.ColorTransfer, result.ColorTransfer);
                Assert.Equal(probe.ColorPrimaries, result.ColorPrimaries);
                Assert.Equal(resize ? 32 : odd ? 66 : 64, result.Width);
                Assert.Equal(resize ? 16 : odd ? 34 : 32, result.Height);
                Assert.True(await probeService.ValidateOutputAsync(ffprobe, output));
                var actualPixels = await ReadPixelsAsync(ffmpeg, output, Path.Combine(directory.Path, resize ? "resized.rgb" : "output.rgb"));
                for (var patch = 0; patch < 4; patch++)
                {
                    var sourceX = patch * 16 + 8;
                    var outputX = resize ? sourceX * result.Width / probe.Width : sourceX;
                    var expectedOffset = (patchHeight / 2 * patchWidth + sourceX) * 3;
                    var actualOffset = (result.Height / 2 * result.Width + outputX) * 3;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var difference = Math.Abs((int)referencePixels[expectedOffset + channel] - actualPixels[actualOffset + channel]);
                        Assert.True(difference <= 12,
                            $"Color difference {difference}: range={range}, resize={resize}, patch={patch}, channel={channel}, " +
                            $"reference={referencePixels[expectedOffset + channel]}, actual={actualPixels[actualOffset + channel]}.");
                    }
                }
            }
        }
        finally
        {
            Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        }
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var result = await ProcessRunner.RunAsync(executable, arguments, TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private static async Task<byte[]> ReadPixelsAsync(string ffmpeg, string input, string output)
    {
        await RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input, "-map", "0:V:0", "-an",
             "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", output]);
        return await File.ReadAllBytesAsync(output);
    }

    private static string Option(List<string> arguments, string option)
    {
        var index = arguments.IndexOf(option);
        Assert.True(index >= 0, "Missing option " + option);
        return arguments[index + 1];
    }

    private static MediaTask CreateTask() => new()
    {
        InputPath = "synthetic.mkv", SourceFormatId = "mkv", TargetFormatId = "mp4", ConversionMode = "balanced",
        Probe = new MediaProbeResult
        {
            Kind = MediaKind.Video, VideoCodec = "h264", Width = 64, Height = 32, PixelFormat = "yuv444p10le",
            ColorSpace = "ycgco", ColorRange = "pc", ColorTransfer = "smpte2084", ColorPrimaries = "bt2020"
        }
    };
}
