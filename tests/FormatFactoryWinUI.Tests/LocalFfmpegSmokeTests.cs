using System.Text.Json;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class LocalFfmpegSmokeTests
{
    [Fact]
    public async Task VideoAudioExtraction_WithClearedAudioSelection_ConvertsFirstTrack()
    {
        var ffmpeg = FindExecutable("ffmpeg.exe");
        var ffprobe = FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "video-with-audio.mkv");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=blue:s=16x16:r=10",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=0.3",
             "-t", "0.3", "-c:v", "ffv1", "-c:a", "pcm_s16le", input], TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var probe = new MediaProbeService();
        var task = new FormatFactoryWinUI.Models.MediaTask
        {
            InputPath = input, SourceFormatId = "mkv", TargetFormatId = "mp3",
            Probe = await probe.ProbeAsync(ffprobe, input), SelectedAudioTrack = -1
        };

        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new FormatFactoryWinUI.Models.AppSettings
            {
                OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
                HardwareAcceleration = false
            }, _ => { }, default);

        Assert.True(result.Success, result.Message);
        Assert.True(await probe.ValidateOutputAsync(ffprobe, result.OutputPath));
        var output = await probe.ProbeAsync(ffprobe, result.OutputPath);
        Assert.Equal("mp3", Assert.Single(output.AudioTracks).Codec);
        Assert.True(output.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task CorruptMp3_FailsWithDecoderCauseAndLeavesNoCompletedOrPartialOutput()
    {
        var ffmpeg = FindExecutable("ffmpeg.exe");
        if (ffmpeg is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "corrupt.mp3");
        // Synthetic MPEG-1 layer-III headers with invalid side information.
        // No content from an error report or a user's source file is used.
        var bytes = Enumerable.Repeat((byte)0xff, 417 * 20).ToArray();
        for (var frame = 0; frame < 20; frame++)
        {
            var offset = frame * 417;
            bytes[offset] = 0xff;
            bytes[offset + 1] = 0xfb;
            bytes[offset + 2] = 0x90;
            bytes[offset + 3] = 0x00;
        }
        await File.WriteAllBytesAsync(input, bytes);
        var outputDirectory = Path.Combine(directory.Path, "output");
        var task = new FormatFactoryWinUI.Models.MediaTask
        {
            InputPath = input, Kind = FormatFactoryWinUI.Models.MediaKind.Audio,
            SourceFormatId = "mp3", TargetFormatId = "mp3", ConversionMode = "balanced"
        };

        var result = await new ConversionService().ConvertAsync(task, ffmpeg,
            new FormatFactoryWinUI.Models.AppSettings
            {
                OutputMode = "custom", CustomOutputDirectory = outputDirectory, HardwareAcceleration = false
            }, _ => { }, default);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(result.OutputPath);
        Assert.Contains("Error submitting packet to decoder", result.Message);
        Assert.Contains("Invalid data found when processing input", result.Message);
        Assert.True(!Directory.Exists(outputDirectory) || !Directory.EnumerateFileSystemEntries(outputDirectory).Any());
    }

    [Fact]
    public async Task UnifiedWorkflow_UsesProductionFfmpegAndPreservesNestedOutputDirectory()
    {
        var ffmpeg = FindExecutable("ffmpeg.exe");
        var ffprobe = FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        using var directory = new TemporaryDirectory();
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "输入 子目录")).FullName;
        var input = Path.Combine(sourceDirectory, "测试.wav");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.2", input],
            TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var probe = new MediaProbeService();
        var task = new FormatFactoryWinUI.Models.MediaTask
        {
            InputPath = input, SourceRoot = directory.Path, SourceFormatId = "wav", TargetFormatId = "flac",
            Probe = await probe.ProbeAsync(ffprobe, input)
        };
        var settings = new FormatFactoryWinUI.Models.AppSettings
        {
            CustomOutputDirectory = Path.Combine(directory.Path, "输出"), HardwareAcceleration = false,
            ParallelCount = 4, CpuThreadsPerTask = 1
        };
        var result = await new FormatFactoryWinUI.ApplicationServices.ConversionWorkflow(
            [new FfmpegConversionEngine(ffmpeg, settings)]).ExecuteAsync(
                FormatFactoryWinUI.ApplicationServices.ConversionRequestFactory.Capture(task, settings), null, default);
        Assert.True(result.Execution.Success, result.Execution.Message);
        Assert.Equal(Path.Combine(settings.CustomOutputDirectory, "输入 子目录", "测试.flac"), result.OutputPath);
        Assert.True(await probe.ValidateOutputAsync(ffprobe, result.OutputPath));
    }

    [Fact]
    public async Task LocalFfmpeg_SynthesizesProbesConvertsAndValidatesShortVideo()
    {
        var ffmpeg = FindExecutable("ffmpeg.exe");
        var ffprobe = FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null)
        {
            throw SkipException.ForSkip("Local ffmpeg/ffprobe executables are not installed.");
        }

        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "synthetic.mkv");
        var converted = Path.Combine(directory.Path, "converted.mp4");

        var generate = await ProcessRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi",
                "-i", "testsrc2=size=64x64:rate=10",
                "-t", "0.4",
                "-an",
                "-c:v", "ffv1",
                source
            ],
            TimeSpan.FromSeconds(20));
        Assert.True(generate.ExitCode == 0, generate.StandardError);
        Assert.True(new FileInfo(source).Length > 0);

        var sourceProbe = await ProbeVideoAsync(ffprobe, source);
        Assert.Equal("ffv1", sourceProbe.Codec);
        Assert.Equal(64, sourceProbe.Width);
        Assert.Equal(64, sourceProbe.Height);

        var convert = await ProcessRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", source,
                "-an",
                "-c:v", "mpeg4",
                "-q:v", "5",
                "-pix_fmt", "yuv420p",
                converted
            ],
            TimeSpan.FromSeconds(20));
        Assert.True(convert.ExitCode == 0, convert.StandardError);
        Assert.True(new FileInfo(converted).Length > 0);

        var convertedProbe = await ProbeVideoAsync(ffprobe, converted);
        Assert.Equal("mpeg4", convertedProbe.Codec);
        Assert.Equal(64, convertedProbe.Width);
        Assert.Equal(64, convertedProbe.Height);
    }

    [Fact]
    public async Task AlbumArtAndTenBitSdr_UseProductionProbeAndPresets()
    {
        var ffmpeg = FindExecutable("ffmpeg.exe");
        var ffprobe = FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null)
        {
            throw SkipException.ForSkip("Local ffmpeg/ffprobe executables are not installed.");
        }

        using var directory = new TemporaryDirectory();
        var audioPath = Path.Combine(directory.Path, "带封面.mp3");
        var generateAudio = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=0.3",
             "-f", "lavfi", "-i", "color=red:s=16x16",
             "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "png",
             "-frames:v", "1", "-disposition:v", "attached_pic", "-t", "0.3", audioPath],
            TimeSpan.FromSeconds(20));
        Assert.True(generateAudio.ExitCode == 0, generateAudio.StandardError);
        var service = new MediaProbeService();
        var audioProbe = await service.ProbeAsync(ffprobe, audioPath);
        Assert.Equal(FormatFactoryWinUI.Models.MediaKind.Audio, audioProbe.Kind);

        var outputAudio = Path.Combine(directory.Path, "audio.flac");
        var audioTask = new FormatFactoryWinUI.Models.MediaTask
        {
            InputPath = audioPath, Kind = audioProbe.Kind, Probe = audioProbe, TargetFormat = "FLAC"
        };
        var convertAudio = await ProcessRunner.RunAsync(ffmpeg,
            new PresetService().BuildArguments(audioTask, outputAudio, EncoderBackend.Cpu, cpuThreads: 1),
            TimeSpan.FromSeconds(20));
        Assert.True(convertAudio.ExitCode == 0, convertAudio.StandardError);
        Assert.True(await service.ValidateOutputAsync(ffprobe, outputAudio));

        var videoPath = Path.Combine(directory.Path, "10位SDR.mkv");
        var generateVideo = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10",
             "-t", "0.3", "-c:v", "ffv1", "-pix_fmt", "yuv420p10le",
             "-color_trc", "bt709", "-color_primaries", "bt709", "-colorspace", "bt709", videoPath],
            TimeSpan.FromSeconds(20));
        Assert.True(generateVideo.ExitCode == 0, generateVideo.StandardError);
        var videoProbe = await service.ProbeAsync(ffprobe, videoPath);
        Assert.Equal("yuv420p10le", videoProbe.PixelFormat);
        Assert.False(videoProbe.IsHdr);

        var outputVideo = Path.Combine(directory.Path, "video.flv");
        var videoTask = new FormatFactoryWinUI.Models.MediaTask
        {
            InputPath = videoPath, Kind = videoProbe.Kind, Probe = videoProbe, TargetFormat = "FLV"
        };
        var convertVideo = await ProcessRunner.RunAsync(ffmpeg,
            new PresetService().BuildArguments(videoTask, outputVideo, EncoderBackend.Cpu, cpuThreads: 1),
            TimeSpan.FromSeconds(20));
        Assert.True(convertVideo.ExitCode == 0, convertVideo.StandardError);
        var outputProbe = await service.ProbeAsync(ffprobe, outputVideo);
        Assert.Equal(64, outputProbe.Width);
        Assert.Equal(64, outputProbe.Height);
        Assert.False(outputProbe.IsHdr);
    }

    [Fact]
    public async Task VideoArtworkAndHdrToneMapping_UseProductionPresets()
    {
        var ffmpeg = FindExecutable("ffmpeg.exe");
        var ffprobe = FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null)
        {
            throw SkipException.ForSkip("Local ffmpeg/ffprobe executables are not installed.");
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "movie-with-cover.mp4");
        var moviePath = Path.Combine(directory.Path, "movie.mp4");
        var coverPath = Path.Combine(directory.Path, "cover.png");
        var generate = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=blue:s=64x64:r=10",
             "-t", "0.3", "-c:v", "libx264", moviePath],
            TimeSpan.FromSeconds(20));
        Assert.True(generate.ExitCode == 0, generate.StandardError);
        generate = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=red:s=16x16", "-frames:v", "1", coverPath],
            TimeSpan.FromSeconds(20));
        Assert.True(generate.ExitCode == 0, generate.StandardError);
        generate = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", moviePath, "-i", coverPath,
             "-map", "1:v", "-map", "0:v", "-c", "copy", "-disposition:v:0", "attached_pic", sourcePath],
            TimeSpan.FromSeconds(20));
        Assert.True(generate.ExitCode == 0, generate.StandardError);
        var service = new MediaProbeService();
        var sourceProbe = await service.ProbeAsync(ffprobe, sourcePath);
        Assert.Equal(64, sourceProbe.Width);
        Assert.Equal("h264", sourceProbe.VideoCodec);

        foreach (var target in new[] { "MP4", "JPG" })
        {
            var outputPath = Path.Combine(directory.Path, "converted." + target.ToLowerInvariant());
            var task = new FormatFactoryWinUI.Models.MediaTask
            {
                InputPath = sourcePath, Kind = sourceProbe.Kind, Probe = sourceProbe, TargetFormat = target
            };
            var conversion = await ProcessRunner.RunAsync(ffmpeg,
                new PresetService().BuildArguments(task, outputPath, EncoderBackend.Cpu, cpuThreads: 1),
                TimeSpan.FromSeconds(20));
            Assert.True(conversion.ExitCode == 0, conversion.StandardError);
            var outputProbe = await service.ProbeAsync(ffprobe, outputPath);
            Assert.Equal(64, outputProbe.Width);
            Assert.Equal(64, outputProbe.Height);
        }

        var hdrPath = Path.Combine(directory.Path, "hdr.mkv");
        generate = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10",
             "-t", "0.3", "-vf", "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
             "-color_trc", "smpte2084", "-color_primaries", "bt2020", "-colorspace", "bt2020nc", hdrPath],
            TimeSpan.FromSeconds(20));
        Assert.True(generate.ExitCode == 0, generate.StandardError);
        var hdrProbe = await service.ProbeAsync(ffprobe, hdrPath);
        Assert.True(hdrProbe.IsHdr, $"Expected PQ input, got transfer={hdrProbe.ColorTransfer}, pixelFormat={hdrProbe.PixelFormat}.");
        var sdrPath = Path.Combine(directory.Path, "tonemapped.flv");
        var hdrTask = new FormatFactoryWinUI.Models.MediaTask
        {
            InputPath = hdrPath, Kind = hdrProbe.Kind, Probe = hdrProbe, TargetFormat = "FLV"
        };
        var toneMap = await ProcessRunner.RunAsync(ffmpeg,
            new PresetService().BuildArguments(hdrTask, sdrPath, EncoderBackend.Cpu, cpuThreads: 1),
            TimeSpan.FromSeconds(20));
        Assert.True(toneMap.ExitCode == 0, toneMap.StandardError);
        var sdrProbe = await service.ProbeAsync(ffprobe, sdrPath);
        Assert.False(sdrProbe.IsHdr);
        Assert.Equal("bt709", sdrProbe.ColorTransfer);
        Assert.Equal("bt709", sdrProbe.ColorPrimaries);
        Assert.Equal("bt709", sdrProbe.ColorSpace);
    }

    private static async Task<(string Codec, int Width, int Height)> ProbeVideoAsync(
        string ffprobe,
        string path)
    {
        var result = await ProcessRunner.RunAsync(
            ffprobe,
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=codec_name,width,height",
                "-of", "json",
                path
            ],
            TimeSpan.FromSeconds(10));
        Assert.True(result.ExitCode == 0, result.StandardError);

        using var json = JsonDocument.Parse(result.StandardOutput);
        var stream = json.RootElement.GetProperty("streams")[0];
        return (
            stream.GetProperty("codec_name").GetString() ?? string.Empty,
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32());
    }

    internal static string? FindExecutable(string fileName)
    {
        var configured = Environment.GetEnvironmentVariable("FORMAT_FACTORY_TEST_ENGINE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var bundled = Path.Combine(configured, fileName);
            if (!File.Exists(bundled)) throw new FileNotFoundException("Required release-test engine is missing.", bundled);
            return bundled;
        }
        var winGetLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Links",
            fileName);
        if (File.Exists(winGetLink))
        {
            return winGetLink;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
