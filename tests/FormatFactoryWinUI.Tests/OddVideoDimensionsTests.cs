using System.Security.Cryptography;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class OddVideoDimensionsTests
{
    private const string Padding = "pad=ceil(iw/2)*2:ceil(ih/2)*2:0:0";

    [Theory]
    [InlineData(27, 18, "balanced")]
    [InlineData(28, 19, "balanced")]
    [InlineData(27, 19, "balanced")]
    [InlineData(27, 18, "fast")]
    [InlineData(28, 19, "quality")]
    public void CpuH264_OddDimensionsAddMinimalPadding(int width, int height, string mode)
    {
        var task = CreateTask(width, height);
        task.ConversionMode = mode;
        var arguments = new PresetService().BuildArguments(task, "output.mp4", EncoderBackend.Cpu).ToList();

        Assert.Equal(Padding, arguments[arguments.IndexOf("-vf") + 1]);
        Assert.Equal(1, arguments.Count(argument => argument == "-vf"));
        Assert.Contains("libx264", arguments);
        Assert.Contains("yuv420p", arguments);
        Assert.Contains("0:V:0", arguments);
        Assert.Contains("0:a?", arguments);
    }

    [Theory]
    [InlineData(28, 18)]
    [InlineData(64, 32)]
    [InlineData(0, 19)]
    [InlineData(27, 0)]
    [InlineData(-1, 19)]
    public void CpuH264_EvenOrUnknownDimensionsKeepExistingArguments(int width, int height)
    {
        var service = new PresetService();
        var expected = service.BuildArguments(CreateTask(28, 18), "output.mp4", EncoderBackend.Cpu);
        var actual = service.BuildArguments(CreateTask(width, height), "output.mp4", EncoderBackend.Cpu);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("-vf", actual);
    }

    [Theory]
    [InlineData(EncoderBackend.Nvidia, false)]
    [InlineData(EncoderBackend.Nvidia, true)]
    [InlineData(EncoderBackend.Intel, false)]
    [InlineData(EncoderBackend.Intel, true)]
    [InlineData(EncoderBackend.Amd, false)]
    [InlineData(EncoderBackend.MediaFoundation, false)]
    [InlineData(EncoderBackend.MediaFoundation, true)]
    public void HardwareAttempts_KeepExistingArgumentsAndCpuFallbackPads(EncoderBackend backend, bool hardwareDecode)
    {
        var service = new PresetService();
        Assert.Equal(service.BuildArguments(CreateTask(28, 18), "output.mp4", backend, hardwareDecode),
            service.BuildArguments(CreateTask(27, 19), "output.mp4", backend, hardwareDecode));
        Assert.Contains(Padding, service.BuildArguments(CreateTask(27, 19), "output.mp4", EncoderBackend.Cpu));
    }

    [Theory]
    [InlineData("HEVC")]
    [InlineData("VP9")]
    [InlineData("AV1")]
    [InlineData("AVI")]
    [InlineData("PNG")]
    [InlineData("GIF")]
    [InlineData("MP3")]
    public void OtherEncodersAndImageAudioBranches_KeepExistingArguments(string target)
    {
        var service = new PresetService();
        var even = CreateTask(28, 18);
        var odd = CreateTask(27, 19);
        even.TargetFormat = odd.TargetFormat = target;

        Assert.Equal(service.BuildArguments(even, "output.tmp", EncoderBackend.Cpu),
            service.BuildArguments(odd, "output.tmp", EncoderBackend.Cpu));
    }

    [Theory]
    [InlineData("MP4")]
    [InlineData("MKV")]
    public void StrictLosslessCopy_DoesNotPadOrChangeArguments(string target)
    {
        var service = new PresetService();
        var even = CreateTask(28, 18);
        var odd = CreateTask(27, 19);
        even.TargetFormat = odd.TargetFormat = target;
        even.ConversionMode = odd.ConversionMode = "lossless";
        var arguments = service.BuildArguments(odd, "output.tmp", EncoderBackend.Cpu);

        Assert.Equal(service.BuildArguments(even, "output.tmp", EncoderBackend.Cpu), arguments);
        Assert.Contains("copy", arguments);
        Assert.DoesNotContain("-vf", arguments);
    }

    [Fact]
    public void HdrToFlv_PaddingPrecedesAndPreservesExistingToneMapping()
    {
        var service = new PresetService();
        var even = CreateTask(28, 18);
        var odd = CreateTask(27, 19);
        even.TargetFormat = odd.TargetFormat = "FLV";
        even.Probe.IsHdr = odd.Probe.IsHdr = true;
        even.Probe.ColorTransfer = odd.Probe.ColorTransfer = "smpte2084";
        var expected = service.BuildArguments(even, "output.flv", EncoderBackend.Cpu).ToList();
        var actual = service.BuildArguments(odd, "output.flv", EncoderBackend.Cpu).ToList();
        var filterIndex = expected.IndexOf("-vf") + 1;

        Assert.StartsWith(Padding + ",zscale=", actual[filterIndex]);
        Assert.Contains("tonemap=hable", actual[filterIndex]);
        Assert.EndsWith("format=yuv420p", actual[filterIndex]);
        Assert.Equal(1, actual.Count(argument => argument == "-vf"));
        actual[filterIndex] = actual[filterIndex][(Padding.Length + 1)..];
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(27, 18, 28, 18)]
    [InlineData(28, 19, 28, 20)]
    [InlineData(27, 19, 28, 20)]
    [InlineData(28, 18, 28, 18)]
    public async Task ProductionWorkflow_SyntheticVideoRetainsAllAudioAndSource(int width, int height, int expectedWidth, int expectedHeight)
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "synthetic-geometry.mkv");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=10",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
             "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
             "-t", "0.3", "-map", "0:v", "-map", "1:a", "-map", "2:a",
             "-c:v", "ffv1", "-level", "3", "-pix_fmt", "yuv444p", "-colorspace", "bt709", "-c:a", "pcm_s16le", input], TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        var probeService = new MediaProbeService();
        var probe = await probeService.ProbeAsync(ffprobe, input);
        Assert.Equal(width, probe.Width);
        Assert.Equal(height, probe.Height);
        Assert.Equal(2, probe.AudioTracks.Count);
        var task = new MediaTask { InputPath = input, SourceFormatId = "mkv", TargetFormatId = "mp4", ConversionMode = "balanced", Probe = probe };
        var settings = new AppSettings
        {
            OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
            HardwareAcceleration = false, CpuThreadsPerTask = 1
        };

        var result = await new ConversionWorkflow([new FfmpegConversionEngine(ffmpeg, settings)]).ExecuteAsync(
            ConversionRequestFactory.Capture(task, settings), null, default);

        Assert.True(result.Execution.Success, result.Execution.Message);
        var output = await probeService.ProbeAsync(ffprobe, result.OutputPath);
        Assert.Equal("h264", output.VideoCodec);
        Assert.Equal(expectedWidth, output.Width);
        Assert.Equal(expectedHeight, output.Height);
        Assert.Equal(2, output.AudioTracks.Count);
        Assert.All(output.AudioTracks, audio => Assert.Equal("aac", audio.Codec));
        Assert.True(output.Duration > TimeSpan.Zero);
        Assert.True(await probeService.ValidateOutputAsync(ffprobe, result.OutputPath));
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        Assert.Single(Directory.EnumerateFiles(settings.CustomOutputDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(input, task.InputPath);
        Assert.Equal(width, task.Probe.Width);
        Assert.Equal(height, task.Probe.Height);

        // Compare raw RGB hashes before lossy H.264 encoding. Padding followed by an
        // exact crop of the original rectangle must preserve every original pixel.
        var originalPixels = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:V:0", "-an",
             "-vf", "format=rgb24", "-f", "hash", "-hash", "sha256", "-"], TimeSpan.FromSeconds(20));
        var paddedPixels = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:V:0", "-an",
             "-vf", $"format=rgb24,{Padding},crop={width}:{height}:0:0:exact=1",
             "-f", "hash", "-hash", "sha256", "-"], TimeSpan.FromSeconds(20));
        Assert.True(originalPixels.ExitCode == 0, originalPixels.StandardError);
        Assert.True(paddedPixels.ExitCode == 0, paddedPixels.StandardError);
        Assert.StartsWith("SHA256=", originalPixels.StandardOutput.Trim());
        Assert.Equal(originalPixels.StandardOutput.Trim(), paddedPixels.StandardOutput.Trim());
    }

    private static MediaTask CreateTask(int width, int height) => new()
    {
        InputPath = "synthetic.mkv", SourceFormatId = "mkv", TargetFormat = "MP4", ConversionMode = "balanced",
        Probe = new MediaProbeResult { Kind = MediaKind.Video, Width = width, Height = height, VideoCodec = "h264", PixelFormat = "yuv444p" }
    };
}
