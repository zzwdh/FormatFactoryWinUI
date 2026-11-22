using System.Security.Cryptography;
using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class M4aRemuxCompatibilityTests
{
    [Theory]
    [InlineData("m4a", "aac", true)]
    [InlineData("m4a", "alac", true)]
    [InlineData("m4a", "ac3", true)]
    [InlineData("m4a", "mp3", false)]
    [InlineData("m4a", "eac3", false)]
    [InlineData("mp4", "aac", true)]
    [InlineData("mp4", "alac", true)]
    [InlineData("mp4", "ac3", true)]
    [InlineData("mp4", "mp3", true)]
    [InlineData("mp4", "eac3", true)]
    public void LosslessAudioPolicy_UsesTheSelectedContainersCodecSupport(string target, string codec, bool supported)
    {
        var task = new MediaTask
        {
            InputPath = "synthetic.mka", Kind = MediaKind.Audio, TargetFormatId = target, ConversionMode = "lossless"
        };
        RemuxStream[] streams = [new("audio", codec, SampleRate: 48000, Channels: 1)];
        if (supported) Assert.Equal(streams, RemuxCompatibilityService.Validate(task, streams));
        else Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(task, streams));
    }

    [Fact]
    public void VideoToM4a_ValidatesOnlyTheSelectedAudioWithoutChangingItsCodec()
    {
        var task = new MediaTask
        {
            InputPath = "synthetic.mkv", Kind = MediaKind.Video, TargetFormatId = "m4a", ConversionMode = "lossless"
        };
        RemuxStream[] streams = [new("video", "h264"), new("audio", "mp3"), new("audio", "aac")];
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(task, streams));
        task.SelectedAudioTrack = 1;
        Assert.Equal(streams[2], Assert.Single(RemuxCompatibilityService.Validate(task, streams)));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("eac3")]
    public async Task UnsupportedM4aCopy_FailsBeforeOutputReservationAndPreservesSource(string codec)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = await GenerateAudioAsync(ffmpeg, directory.Path, codec);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        try
        {
            var task = await AudioTaskAsync(ffprobe, input);
            var settings = new AppSettings
            {
                OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
                HardwareAcceleration = false
            };
            var progress = new List<ConversionProgress>();
            var result = await new ConversionService().ConvertAsync(task, ffmpeg, settings, progress.Add, default);

            Assert.False(result.Success);
            Assert.False(result.Cancelled);
            Assert.Equal(LocalizationService.T("RemuxUnsupportedStream", $"audio: {codec}", task.TargetFormat), result.Message);
            Assert.Equal(string.Empty, result.OutputPath);
            Assert.Equal(string.Empty, result.LogPath);
            Assert.Null(result.PreservedOutputPath);
            Assert.Null(result.RecoverableOutputId);
            Assert.Empty(progress);
            Assert.False(Directory.Exists(settings.CustomOutputDirectory));
        }
        finally
        {
            Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        }
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("alac")]
    [InlineData("ac3")]
    public async Task SupportedM4aCopy_PreservesActualPacketsAndCodec(string codec)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = await GenerateAudioAsync(ffmpeg, directory.Path, codec);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        try
        {
            var task = await AudioTaskAsync(ffprobe, input);
            var sourcePackets = await PacketHashesAsync(ffprobe, input);
            var settings = new AppSettings
            {
                OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output"),
                HardwareAcceleration = false
            };
            var result = await new ConversionService().ConvertAsync(task, ffmpeg, settings, _ => { }, default);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal(".m4a", Path.GetExtension(result.OutputPath));
            var outputProbe = await new MediaProbeService().ProbeAsync(ffprobe, result.OutputPath);
            var sourceTrack = Assert.Single(task.Probe.AudioTracks);
            var outputTrack = Assert.Single(outputProbe.AudioTracks);
            Assert.Equal(codec, outputTrack.Codec);
            Assert.Equal(sourceTrack.Channels, outputTrack.Channels);
            Assert.Equal(sourcePackets, await PacketHashesAsync(ffprobe, result.OutputPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(settings.CustomOutputDirectory, "*.partial*", SearchOption.AllDirectories));
        }
        finally
        {
            Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        }
    }

    private static async Task<string> GenerateAudioAsync(string ffmpeg, string directory, string codec)
    {
        var input = Path.Combine(directory, "synthetic.mka");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.25",
             "-c:a", codec == "mp3" ? "libmp3lame" : codec, input], TimeSpan.FromSeconds(30));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        return input;
    }

    private static async Task<MediaTask> AudioTaskAsync(string ffprobe, string input) => new()
    {
        InputPath = input, SourceFormatId = "mkv", TargetFormatId = "m4a", ConversionMode = "lossless",
        Probe = await new MediaProbeService().ProbeAsync(ffprobe, input)
    };

    private static async Task<string[]> PacketHashesAsync(string ffprobe, string path)
    {
        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "json", path],
            TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var hashes = json.RootElement.GetProperty("packets").EnumerateArray()
            .Select(packet => packet.GetProperty("data_hash").GetString()!).ToArray();
        Assert.NotEmpty(hashes);
        Assert.All(hashes, hash => Assert.StartsWith("SHA256:", hash));
        return hashes;
    }

    private static (string Ffmpeg, string Ffprobe) Engines()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        return (ffmpeg, ffprobe);
    }
}
