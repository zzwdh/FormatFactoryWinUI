using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class OpusChannelLayoutTests
{
    [Theory]
    [InlineData("WEBM")]
    [InlineData("VP9")]
    [InlineData("AV1")]
    public void ModernVideo_NormalizesOnlyTheAffectedOutputAudioTrack(string target)
    {
        var task = MultitrackTask(target);
        var arguments = new PresetService().BuildArguments(task, "output.webm", EncoderBackend.Cpu).ToList();

        Assert.Contains("-filter:a:1", arguments);
        Assert.Equal("channelmap=map=0|1|2|3|4|5:channel_layout=5.1",
            arguments[arguments.IndexOf("-filter:a:1") + 1]);
        Assert.DoesNotContain("-filter:a:0", arguments);
        Assert.DoesNotContain("-af", arguments);
        Assert.DoesNotContain("-ac", arguments);
        Assert.Contains("0:a?", arguments);
        Assert.Equal("128k", arguments[arguments.IndexOf("-b:a") + 1]);
    }

    [Theory]
    [InlineData(MediaKind.Video, 1, "0:a:1")]
    [InlineData(MediaKind.Audio, 0, "0:a:0")]
    public void OpusExtraction_UsesTheSelectedInputLayoutAndOutputAudioIndexZero(
        MediaKind kind, int selected, string expectedMap)
    {
        var task = MultitrackTask("OPUS");
        task.Kind = kind;
        task.SelectedAudioTrack = selected;
        if (kind == MediaKind.Audio) task.Probe.AudioTracks = [task.Probe.AudioTracks[1]];
        var arguments = new PresetService().BuildArguments(task, "output.opus", EncoderBackend.Cpu).ToList();

        Assert.Contains(expectedMap, arguments);
        Assert.Contains("-filter:a:0", arguments);
        Assert.DoesNotContain("-filter:a:1", arguments);
        Assert.DoesNotContain("-ac", arguments);
    }

    [Fact]
    public void OpusExtraction_DoesNotNormalizeAnUnselectedSurroundTrack()
    {
        var task = MultitrackTask("OPUS");
        task.SelectedAudioTrack = 0;
        var arguments = new PresetService().BuildArguments(task, "output.opus", EncoderBackend.Cpu);

        Assert.Contains("0:a:0", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-filter:a:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(6, "5.1")]
    [InlineData(6, "6.0")]
    [InlineData(6, "")]
    [InlineData(2, "stereo")]
    [InlineData(0, "5.1(side)")]
    public void OtherOrUnknownLayouts_AreNotReinterpreted(int channels, string layout)
    {
        var task = MultitrackTask("OPUS");
        task.SelectedAudioTrack = 1;
        task.Probe.AudioTracks[1].Channels = channels;
        task.Probe.AudioTracks[1].ChannelLayout = layout;
        var arguments = new PresetService().BuildArguments(task, "output.opus", EncoderBackend.Cpu);

        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-filter:a:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MP4", "balanced")]
    [InlineData("MKV", "balanced")]
    [InlineData("MP3", "balanced")]
    [InlineData("MKV", "lossless")]
    [InlineData("OPUS", "lossless")]
    public void OtherEncodersAndStreamCopy_DoNotReceiveTheOpusLayoutFilter(string target, string mode)
    {
        var task = MultitrackTask(target);
        task.ConversionMode = mode;
        var arguments = new PresetService().BuildArguments(task, "output.tmp", EncoderBackend.Cpu);

        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-filter:a:", StringComparison.Ordinal));
        if (mode == "lossless") Assert.Contains("copy", arguments);
    }

    [Fact]
    public void ActualProbeAndRequestSnapshot_PreserveChannelLayoutWithoutSharingMutableTracks()
    {
        var probe = MediaProbeParser.Parse("""
            {"streams":[
              {"index":2,"codec_type":"audio","codec_name":"pcm_s16le","channels":2},
              {"index":4,"codec_type":"audio","codec_name":"ac3","channels":6,"channel_layout":"5.1(side)"}
            ]}
            """, "synthetic.mkv");
        Assert.Empty(probe.AudioTracks[0].ChannelLayout);
        Assert.Equal("5.1(side)", probe.AudioTracks[1].ChannelLayout);
        var task = new MediaTask { InputPath = "synthetic.mkv", SourceFormatId = "mkv", TargetFormatId = "opus", Probe = probe };
        var request = ConversionRequestFactory.Capture(task, new());
        probe.AudioTracks[1].ChannelLayout = "5.1";

        var captured = ConversionRequestFactory.CreateMediaTask(request, "ffmpeg");
        Assert.Equal("5.1(side)", captured.Probe.AudioTracks[1].ChannelLayout);
        Assert.Equal(4, captured.Probe.AudioTracks[1].StreamIndex);
        Assert.NotSame(probe.AudioTracks[1], captured.Probe.AudioTracks[1]);
    }

    [Fact]
    public async Task RealMultitrackWorkflow_PreservesStereoAndSixChannels_AndExtractsSelectedOpusTrack()
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "synthetic-multitrack.mkv");
        await GenerateAsync(ffmpeg, input);
        var probeService = new MediaProbeService();
        var source = await probeService.ProbeAsync(ffprobe, input);
        Assert.Equal("5.1(side)", source.AudioTracks[1].ChannelLayout);

        // The old parameters fail on the second audio track, independently of application state.
        var before = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", input, "-map", "0:V:0", "-map", "0:a?",
             "-c:v", "libvpx-vp9", "-c:a", "libopus", "-b:a", "128k", "-threads", "1",
             Path.Combine(directory.Path, "before.webm")], TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, before.ExitCode);
        Assert.Contains("Invalid channel layout 5.1(side)", before.StandardError);

        var task = new MediaTask { InputPath = input, SourceFormatId = "mkv", TargetFormatId = "webm", Probe = source };
        var settings = new AppSettings
        {
            OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
            HardwareAcceleration = false, CpuThreadsPerTask = 1
        };
        var workflow = new ConversionWorkflow([new FfmpegConversionEngine(ffmpeg, settings)]);
        var video = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);
        Assert.True(video.Execution.Success, video.Execution.Message);
        var output = await probeService.ProbeAsync(ffprobe, video.OutputPath);
        Assert.Equal("vp9", output.VideoCodec);
        Assert.Equal(2, output.AudioTracks.Count);
        Assert.All(output.AudioTracks, track => Assert.Equal("opus", track.Codec));
        Assert.Equal(2, output.AudioTracks[0].Channels);
        Assert.Equal(6, output.AudioTracks[1].Channels);
        Assert.Equal("5.1", output.AudioTracks[1].ChannelLayout);

        task.TargetFormatId = "opus";
        task.SelectedAudioTrack = 1;
        var audio = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);
        Assert.True(audio.Execution.Success, audio.Execution.Message);
        var extracted = Assert.Single((await probeService.ProbeAsync(ffprobe, audio.OutputPath)).AudioTracks);
        Assert.Equal("opus", extracted.Codec);
        Assert.Equal(6, extracted.Channels);
        Assert.Equal("5.1", extracted.ChannelLayout);
        Assert.True(File.Exists(input));
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.CustomOutputDirectory, "*.partial*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RealLayoutNormalization_DoesNotMixOrReorderTheSixDecodedPcmChannels()
    {
        var (ffmpeg, _) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "synthetic-multitrack.mkv");
        await GenerateAsync(ffmpeg, input);
        var task = MultitrackTask("OPUS");
        task.InputPath = input;
        task.SelectedAudioTrack = 1;
        var arguments = new PresetService().BuildArguments(task, "unused.opus", EncoderBackend.Cpu).ToList();
        Assert.Contains("-filter:a:0", arguments);
        var filter = arguments[arguments.IndexOf("-filter:a:0") + 1];
        var before = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:a:1", "-c:a", "pcm_s16le",
             "-f", "hash", "-hash", "sha256", "-"], TimeSpan.FromSeconds(20));
        var after = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:a:1", "-filter:a:0", filter,
             "-c:a", "pcm_s16le", "-f", "hash", "-hash", "sha256", "-"], TimeSpan.FromSeconds(20));
        Assert.True(before.ExitCode == 0, before.StandardError);
        Assert.True(after.ExitCode == 0, after.StandardError);
        Assert.StartsWith("SHA256=", before.StandardOutput.Trim());
        Assert.Equal(before.StandardOutput.Trim(), after.StandardOutput.Trim());
    }

    private static MediaTask MultitrackTask(string target) => new()
    {
        InputPath = "synthetic.mkv", Kind = MediaKind.Video, TargetFormat = target,
        Probe = new MediaProbeResult
        {
            Kind = MediaKind.Video,
            AudioTracks =
            [
                new() { RelativeIndex = 0, StreamIndex = 2, Channels = 2, ChannelLayout = "stereo" },
                new() { RelativeIndex = 1, StreamIndex = 4, Channels = 6, ChannelLayout = "5.1(side)" }
            ]
        }
    };

    private static async Task GenerateAsync(string ffmpeg, string input)
    {
        // Different synthetic tones in every channel make accidental mixing or reordering observable.
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=blue:s=16x16:r=10:d=0.4",
             "-f", "lavfi", "-i", "aevalsrc=sin(2*PI*220*t)|sin(2*PI*330*t):s=48000:d=0.4:c=stereo",
             "-f", "lavfi", "-i", "aevalsrc=sin(2*PI*210*t)|sin(2*PI*320*t)|sin(2*PI*430*t)|sin(2*PI*540*t)|sin(2*PI*650*t)|sin(2*PI*760*t):s=48000:d=0.4:c=5.1(side)",
             "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-c:v", "ffv1",
             "-c:a:0", "pcm_s16le", "-c:a:1", "ac3", "-b:a:1", "384k", input], TimeSpan.FromSeconds(30));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
    }

    private static (string Ffmpeg, string Ffprobe) Engines()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        return (ffmpeg, ffprobe);
    }
}
