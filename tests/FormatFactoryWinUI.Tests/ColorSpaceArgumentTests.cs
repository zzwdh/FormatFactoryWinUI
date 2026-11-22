using System.Security.Cryptography;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class ColorSpaceArgumentTests
{
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
    public void RgbInputMatrix_IsNotAppliedToYuvEncoderOutput(EncoderBackend backend, bool hardwareDecode)
    {
        var service = new PresetService();
        var unspecified = CreateTask(string.Empty);
        var expected = service.BuildArguments(unspecified, "output.mp4", backend, hardwareDecode);
        foreach (var matrix in new[] { "gbr", "rgb" })
        {
            var task = CreateTask(matrix);
            var actual = service.BuildArguments(task, "output.mp4", backend, hardwareDecode).ToList();

            Assert.DoesNotContain("-colorspace", actual);
            Assert.Equal(expected, actual);
            Assert.Equal("pc", Option(actual, "-color_range"));
            Assert.Equal("bt709", Option(actual, "-color_primaries"));
            Assert.Equal("iec61966-2-1", Option(actual, "-color_trc"));
            Assert.Equal(matrix, task.Probe.ColorSpace);
            Assert.Equal("pc", task.Probe.ColorRange);
        }
    }

    [Theory]
    [InlineData("bt709", "tv")]
    [InlineData("bt2020nc", "tv")]
    [InlineData("smpte170m", "pc")]
    public void YuvInputMatrixAndRange_ArePreserved(string matrix, string range)
    {
        var task = CreateTask(matrix);
        task.Probe.PixelFormat = "yuv420p";
        task.Probe.ColorRange = range;
        var arguments = new PresetService().BuildArguments(task, "output.mp4", EncoderBackend.Cpu).ToList();

        Assert.Equal(matrix, Option(arguments, "-colorspace"));
        Assert.Equal(range, Option(arguments, "-color_range"));
        Assert.DoesNotContain("-vf", arguments);
    }

    [Fact]
    public void StrictLosslessCopy_DoesNotChangeRgbSamplesOrMetadata()
    {
        var task = CreateTask("gbr");
        task.TargetFormatId = "mkv";
        task.ConversionMode = "lossless";
        var arguments = new PresetService().BuildArguments(task, "output.mkv", EncoderBackend.Cpu).ToList();

        Assert.Equal("copy", Option(arguments, "-c"));
        Assert.DoesNotContain("-colorspace", arguments);
        Assert.DoesNotContain("-color_range", arguments);
        Assert.DoesNotContain("-vf", arguments);
        Assert.Equal("gbr", task.Probe.ColorSpace);
        Assert.Equal("pc", task.Probe.ColorRange);
    }

    [Fact]
    public void HdrToFlv_KeepsItsActualToneMappingAndSdrMetadata()
    {
        var task = CreateTask("gbr");
        task.TargetFormatId = "flv";
        task.Probe.IsHdr = true;
        task.Probe.ColorTransfer = "smpte2084";
        task.Probe.ColorPrimaries = "bt2020";
        var arguments = new PresetService().BuildArguments(task, "output.flv", EncoderBackend.Cpu).ToList();

        Assert.Contains("tonemap=hable", Option(arguments, "-vf"));
        Assert.Equal("bt709", Option(arguments, "-colorspace"));
        Assert.Equal("bt709", Option(arguments, "-color_primaries"));
        Assert.Equal("bt709", Option(arguments, "-color_trc"));
        Assert.Equal("tv", Option(arguments, "-color_range"));
    }

    [Fact]
    public async Task RealRgbVideo_ConvertsToH264WithoutRgbMatrixTagOrColorShift()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "synthetic-rgb.mkv");
        const string patches = "color=black:s=64x32:r=5:d=0.4,format=rgb24," +
            "drawbox=x=0:y=0:w=16:h=32:color=red:t=fill," +
            "drawbox=x=16:y=0:w=16:h=32:color=lime:t=fill," +
            "drawbox=x=32:y=0:w=16:h=32:color=blue:t=fill," +
            "drawbox=x=48:y=0:w=16:h=32:color=white:t=fill";
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", patches,
             "-c:v", "ffv1", "-pix_fmt", "bgr0", "-colorspace", "rgb", "-color_range", "pc",
             "-color_primaries", "bt709", "-color_trc", "iec61966-2-1", input], TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        try
        {
            var probeService = new MediaProbeService();
            var sourceProbe = await probeService.ProbeAsync(ffprobe, input);
            Assert.Equal("gbr", sourceProbe.ColorSpace);
            Assert.Equal("pc", sourceProbe.ColorRange);
            var sourcePixels = await DecodeRgbAsync(ffmpeg, input, Path.Combine(directory.Path, "source.rgb"));

            foreach (var resize in new[] { false, true })
            {
                var output = Path.Combine(directory.Path, resize ? "resized.mp4" : "converted.mp4");
                var task = CreateTask(sourceProbe.ColorSpace);
                task.InputPath = input;
                task.Probe = sourceProbe;
                if (resize) new MediaConversionOptions { MaxWidth = 32, MaxHeight = 16 }.ApplyTo(task.Options);
                var arguments = new PresetService().BuildArguments(task, output, EncoderBackend.Cpu, cpuThreads: 1);
                var converted = await ProcessRunner.RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(20));
                Assert.True(converted.ExitCode == 0, converted.StandardError);
                var outputProbe = await probeService.ProbeAsync(ffprobe, output);
                Assert.Equal("h264", outputProbe.VideoCodec);
                Assert.Equal(resize ? 32 : 64, outputProbe.Width);
                Assert.Equal(resize ? 16 : 32, outputProbe.Height);
                Assert.DoesNotContain(outputProbe.ColorSpace, new[] { "gbr", "rgb" });
                Assert.Equal("pc", outputProbe.ColorRange);
                Assert.True(await probeService.ValidateOutputAsync(ffprobe, output));

                var outputPixels = await DecodeRgbAsync(ffmpeg, output,
                    Path.Combine(directory.Path, resize ? "resized.rgb" : "converted.rgb"));
                Assert.Equal(outputProbe.Width * outputProbe.Height * 3, outputPixels.Length);
                for (var patch = 0; patch < 4; patch++)
                {
                    // Compare the interior of each solid patch, away from lossy chroma edges.
                    var sourceIndex = (16 * 64 + patch * 16 + 8) * 3;
                    var outputIndex = (outputProbe.Height / 2 * outputProbe.Width +
                        patch * outputProbe.Width / 4 + outputProbe.Width / 8) * 3;
                    for (var channel = 0; channel < 3; channel++)
                        Assert.InRange(Math.Abs((int)sourcePixels[sourceIndex + channel] - outputPixels[outputIndex + channel]), 0, 8);
                }
            }
        }
        finally
        {
            Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        }
    }

    private static async Task<byte[]> DecodeRgbAsync(string ffmpeg, string input, string output)
    {
        var decoded = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", input, "-map", "0:V:0", "-an",
             "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", output], TimeSpan.FromSeconds(20));
        Assert.True(decoded.ExitCode == 0, decoded.StandardError);
        return await File.ReadAllBytesAsync(output);
    }

    private static string Option(List<string> arguments, string key) => arguments[arguments.IndexOf(key) + 1];

    private static MediaTask CreateTask(string matrix) => new()
    {
        InputPath = "synthetic.mkv", SourceFormatId = "mkv", TargetFormatId = "mp4", ConversionMode = "balanced",
        Probe = new MediaProbeResult
        {
            Kind = MediaKind.Video, VideoCodec = "ffv1", Width = 64, Height = 32, PixelFormat = "bgr0",
            ColorSpace = matrix, ColorRange = "pc", ColorTransfer = "iec61966-2-1", ColorPrimaries = "bt709"
        }
    };
}
