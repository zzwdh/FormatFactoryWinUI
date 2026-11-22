using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class RemuxConversionTests
{
    [Theory]
    [InlineData(1, 22050)]
    [InlineData(2, 44100)]
    public async Task ImaAdpcmExtraction_PreservesAudioPayloadAndRetainsSameWavGuard(int channels, int sampleRate)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "input.avi");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", $"sine=frequency=750:sample_rate={sampleRate}:duration=0.5",
             "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "adpcm_ima_wav", input],
            TimeSpan.FromSeconds(30));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var sourceHash = await AudioPayloadHashAsync(ffmpeg, input);
        var settings = new AppSettings { OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output") };
        var workflow = new ConversionWorkflow([new FfmpegConversionEngine(ffmpeg, settings)]);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "avi", TargetFormatId = "wav", ConversionMode = "lossless",
            Probe = await new MediaProbeService().ProbeAsync(ffprobe, input)
        };
        var result = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);
        Assert.True(result.Execution.Success, result.Execution.Message);
        Assert.Equal(sourceHash, await AudioPayloadHashAsync(ffmpeg, result.OutputPath));
        var outputProbe = await new MediaProbeService().ProbeAsync(ffprobe, result.OutputPath);
        Assert.True(await RemuxCompatibilityService.ValidateOutputAsync(ffprobe, result.OutputPath,
            [new RemuxStream("audio", "adpcm_ima_wav", Channels: channels, SampleRate: sampleRate)], default));

        var sameFormat = new MediaTask
        {
            InputPath = result.OutputPath, SourceFormatId = "wav", TargetFormatId = "wav",
            ConversionMode = "lossless", Probe = outputProbe
        };
        var repeated = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(sameFormat, settings), null, default);
        Assert.False(repeated.Execution.Success);
        Assert.Equal(LocalizationService.T("SameLossless"), repeated.Execution.Message);
        Assert.Empty(repeated.Execution.Artifacts);
    }

    [Fact]
    public async Task LosslessWorkflow_PreservesActualPacketsAndSelectedAudioWithNoPartialOutputs()
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var inputDirectory = Directory.CreateDirectory(Path.Combine(sourceRoot, "nested")).FullName;
        var input = Path.Combine(inputDirectory, "sample.mp4");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
             "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
             "-map", "0:v", "-map", "1:a", "-map", "2:a", "-t", "0.4",
             "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", input],
            TimeSpan.FromSeconds(30));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var sourcePackets = await PacketHashesAsync(ffprobe, input);
        var task = new MediaTask
        {
            InputPath = input, SourceRoot = sourceRoot, SourceFormatId = "mp4", TargetFormatId = "mkv",
            Probe = await new MediaProbeService().ProbeAsync(ffprobe, input), ConversionMode = "lossless"
        };
        var settings = new AppSettings
        {
            OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
            OutputFileNameTemplate = "{name}_{format}", PreserveFolderStructure = true, HardwareAcceleration = true
        };
        var workflow = new ConversionWorkflow([new FfmpegConversionEngine(ffmpeg, settings)]);
        var messages = new List<string>();
        var result = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings),
            new ImmediateProgress(value => messages.Add(value.Message)), default);

        Assert.True(result.Execution.Success, result.Execution.Message);
        Assert.Equal(Path.Combine(settings.CustomOutputDirectory, "nested", "sample_MKV.mkv"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        var outputPackets = await PacketHashesAsync(ffprobe, result.OutputPath);
        Assert.Equal(3, outputPackets.Count);
        foreach (var stream in sourcePackets) Assert.Equal(stream.Value, outputPackets[stream.Key]);
        Assert.Contains(LocalizationService.T("RemuxCopying"), messages);

        task.TargetFormatId = "m4a";
        task.SelectedAudioTrack = 1;
        var audio = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);
        Assert.True(audio.Execution.Success, audio.Execution.Message);
        var audioPackets = await PacketHashesAsync(ffprobe, audio.OutputPath);
        Assert.Equal(sourcePackets[2], Assert.Single(audioPackets).Value);
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.CustomOutputDirectory, "*.partial*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("ffv1", "mp4")]
    [InlineData("h264", "hevc")]
    [InlineData("h264", "gif")]
    public async Task IncompatibleLosslessWorkflow_FailsBeforeWritingAndCannotUseStaleProbe(string codec, string target)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.mkv");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10",
             "-t", "0.3", "-an", "-c:v", codec == "h264" ? "libx264" : codec, "-threads", "1", input],
            TimeSpan.FromSeconds(30));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "mkv", TargetFormatId = target, ConversionMode = "lossless",
            Probe = new MediaProbeResult { Kind = MediaKind.Video, VideoCodec = "hevc" }
        };
        var settings = new AppSettings { OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output") };
        var workflow = new ConversionWorkflow([new FfmpegConversionEngine(ffmpeg, settings)]);

        var result = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);

        Assert.False(result.Execution.Success);
        Assert.False(result.Execution.Cancelled);
        Assert.Empty(result.Execution.Artifacts);
        Assert.False(string.IsNullOrWhiteSpace(result.Execution.Message));
        Assert.False(Directory.Exists(settings.CustomOutputDirectory));
        Assert.True(File.Exists(input));
    }

    private static (string Ffmpeg, string Ffprobe) Engines()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        return (ffmpeg, ffprobe);
    }

    private static async Task<string> AudioPayloadHashAsync(string ffmpeg, string path)
    {
        // The WAV demuxer can group several ADPCM blocks into a single packet.
        var result = await ProcessRunner.RunAsync(ffmpeg,
            ["-v", "error", "-i", path, "-map", "0:a:0", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-"],
            TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.StandardError);
        var hash = result.StandardOutput.Trim();
        Assert.Matches("^SHA256=[0-9a-f]{64}$", hash);
        return hash;
    }

    private static async Task<IReadOnlyDictionary<int, string[]>> PacketHashesAsync(string ffprobe, string path)
    {
        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=stream_index,data_hash", "-of", "json", path],
            TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var packets = json.RootElement.GetProperty("packets").EnumerateArray()
            .Select(packet => (Index: packet.GetProperty("stream_index").GetInt32(), Hash: packet.GetProperty("data_hash").GetString()!)).ToArray();
        Assert.NotEmpty(packets);
        Assert.All(packets, packet => Assert.StartsWith("SHA256:", packet.Hash));
        return packets.GroupBy(packet => packet.Index).ToDictionary(group => group.Key, group => group.Select(packet => packet.Hash).ToArray());
    }

    private sealed class ImmediateProgress(Action<ConversionExecutionProgress> report) : IProgress<ConversionExecutionProgress>
    {
        public void Report(ConversionExecutionProgress value) => report(value);
    }
}
