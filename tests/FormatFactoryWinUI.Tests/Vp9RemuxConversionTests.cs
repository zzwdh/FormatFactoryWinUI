using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class Vp9RemuxConversionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Vp9ToMp4LosslessWorkflow_PreservesEveryPacketWithoutReencoding(int audioTracks)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = await GenerateAsync(ffmpeg, directory.Path, audioTracks == 0 ? null : "aac", audioTracks);
        var inputBytes = await File.ReadAllBytesAsync(input);
        var sourcePackets = await PacketHashesAsync(ffprobe, input);
        var sourceStreams = await ReadStreamsAsync(ffprobe, input);
        Assert.Equal(1 + audioTracks, sourceStreams.Count);
        Assert.Equal("vp9", sourceStreams[0].Codec);
        Assert.All(sourceStreams.Skip(1), stream => Assert.Equal("aac", stream.Codec));

        // Prove the bundled muxer supports this combination before the admission policy is changed.
        var directCopy = Path.Combine(directory.Path, "muxer-proof.mp4");
        var direct = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-i", input, "-map", "0", "-c", "copy", directCopy],
            TimeSpan.FromSeconds(30));
        Assert.True(direct.ExitCode == 0, direct.StandardError);
        AssertPacketsEqual(sourcePackets, await PacketHashesAsync(ffprobe, directCopy));
        Assert.True(await RemuxCompatibilityService.ValidateOutputAsync(ffprobe, directCopy, sourceStreams, default));

        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "mkv", TargetFormatId = "mp4",
            Probe = await new MediaProbeService().ProbeAsync(ffprobe, input), ConversionMode = "lossless"
        };
        var settings = new AppSettings
        {
            OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
            HardwareAcceleration = true, CpuThreadsPerTask = 1
        };
        var arguments = new PresetService().BuildArguments(task, "output.mp4", EncoderBackend.Cpu).ToList();
        Assert.Equal("copy", arguments[arguments.IndexOf("-c") + 1]);
        Assert.Equal("0", arguments[arguments.IndexOf("-map") + 1]);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("lib", StringComparison.Ordinal));
        var messages = new List<string>();
        var workflow = new ConversionWorkflow([new FfmpegConversionEngine(ffmpeg, settings)]);

        var result = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings),
            new ImmediateProgress(value => messages.Add(value.Message)), default);

        Assert.True(result.Execution.Success, result.Execution.Message);
        Assert.False(result.Execution.Cancelled);
        Assert.Equal(".mp4", Path.GetExtension(result.OutputPath));
        Assert.Contains(LocalizationService.T("RemuxCopying"), messages);
        AssertPacketsEqual(sourcePackets, await PacketHashesAsync(ffprobe, result.OutputPath));
        Assert.True(await RemuxCompatibilityService.ValidateOutputAsync(ffprobe, result.OutputPath, sourceStreams, default));
        var preservedInput = await File.ReadAllBytesAsync(input);
        Assert.True(inputBytes.AsSpan().SequenceEqual(preservedInput));
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.CustomOutputDirectory, "*.partial*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("opus", false, "mp4")]
    [InlineData(null, true, "mp4")]
    [InlineData(null, false, "mov")]
    [InlineData(null, false, "avi")]
    public async Task UnsupportedVp9Combinations_RemainRejectedBeforeWriting(string? audioCodec, bool subtitles, string target)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = await GenerateAsync(ffmpeg, directory.Path, audioCodec, audioCodec is null ? 0 : 1, subtitles);
        var inputBytes = await File.ReadAllBytesAsync(input);
        var streams = await ReadStreamsAsync(ffprobe, input);
        Assert.Equal("vp9", streams[0].Codec);
        if (audioCodec is not null) Assert.Contains(streams, stream => stream.Type == "audio" && stream.Codec == audioCodec);
        if (subtitles) Assert.Contains(streams, stream => stream.Type == "subtitle" && stream.Codec == "subrip");
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "mkv", TargetFormatId = target, ConversionMode = "lossless",
            // Admission must inspect the real streams, even if imported metadata says H.264.
            Probe = new MediaProbeResult { Kind = MediaKind.Video, VideoCodec = "h264" }
        };
        var settings = new AppSettings { OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output") };
        var workflow = new ConversionWorkflow([new FfmpegConversionEngine(ffmpeg, settings)]);

        var result = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);

        Assert.False(result.Execution.Success);
        Assert.False(result.Execution.Cancelled);
        Assert.Empty(result.Execution.Artifacts);
        Assert.False(string.IsNullOrWhiteSpace(result.Execution.Message));
        Assert.False(Directory.Exists(settings.CustomOutputDirectory));
        var preservedInput = await File.ReadAllBytesAsync(input);
        Assert.True(inputBytes.AsSpan().SequenceEqual(preservedInput));
    }

    private static async Task<string> GenerateAsync(string ffmpeg, string directory, string? audioCodec, int audioTracks,
        bool subtitles = false)
    {
        var input = Path.Combine(directory, "source.mkv");
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10"
        };
        for (var index = 0; index < audioTracks; index++)
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency={440 * (index + 1)}:sample_rate=48000"]);
        if (subtitles)
        {
            var subtitle = Path.Combine(directory, "synthetic.srt");
            await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:00,300\nSynthetic fixture\n");
            arguments.AddRange(["-i", subtitle]);
        }
        arguments.AddRange(["-map", "0:v:0"]);
        for (var index = 0; index < audioTracks; index++) arguments.AddRange(["-map", $"{index + 1}:a:0"]);
        if (subtitles) arguments.AddRange(["-map", $"{audioTracks + 1}:s:0", "-c:s", "srt"]);
        arguments.AddRange(["-t", "0.4", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8",
            "-row-mt", "0", "-threads", "1", "-pix_fmt", "yuv420p"]);
        if (audioTracks > 0) arguments.AddRange(["-c:a", audioCodec == "opus" ? "libopus" : audioCodec!, "-ac", "2"]);
        arguments.Add(input);
        var generated = await ProcessRunner.RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(30));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        return input;
    }

    private static async Task<IReadOnlyList<RemuxStream>> ReadStreamsAsync(string ffprobe, string path)
    {
        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_streams", "-of", "json", path], TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return RemuxCompatibilityService.ParseStreams(result.StandardOutput);
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

    private static void AssertPacketsEqual(IReadOnlyDictionary<int, string[]> expected, IReadOnlyDictionary<int, string[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var stream in expected) Assert.Equal(stream.Value, actual[stream.Key]);
    }

    private static (string Ffmpeg, string Ffprobe) Engines()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        return (ffmpeg, ffprobe);
    }

    private sealed class ImmediateProgress(Action<ConversionExecutionProgress> report) : IProgress<ConversionExecutionProgress>
    {
        public void Report(ConversionExecutionProgress value) => report(value);
    }
}