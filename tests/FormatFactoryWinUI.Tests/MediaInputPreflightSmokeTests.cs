using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaInputPreflightSmokeTests
{
    [Fact]
    public async Task HealthySyntheticAudio_IsProbedAndDecodedWithoutWritingAnOutput()
    {
        var ffmpeg = Engine();
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "synthetic.wav");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.2", source],
            TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var result = await new MediaInputPreflightService().SelectAsync(ffmpeg, null,
            new MediaTask { InputPath = source, Kind = MediaKind.Audio, TargetFormat = "MP3" });
        Assert.True(result.Success, result.Detail);
        Assert.Equal(MediaInputFailureKind.None, result.FailureKind);
        Assert.Equal("pcm_s16le", Assert.Single(result.Probe!.AudioTracks).Codec);
        Assert.Equal([source], Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task DamagedSyntheticMp3_IsRejectedBeforeOutputAndIsNotSentToFallback()
    {
        var ffmpeg = Engine();
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "damaged.mp3");
        var bytes = Enumerable.Repeat((byte)0xff, 417 * 20).ToArray();
        for (var frame = 0; frame < 20; frame++)
        {
            var offset = frame * 417;
            bytes[offset] = 0xff; bytes[offset + 1] = 0xfb; bytes[offset + 2] = 0x90; bytes[offset + 3] = 0x00;
        }
        await File.WriteAllBytesAsync(source, bytes);
        var result = await new MediaInputPreflightService().SelectAsync(ffmpeg,
            Path.Combine(directory.Path, "unused-fallback", "ffmpeg.exe"),
            new MediaTask { InputPath = source, Kind = MediaKind.Audio, TargetFormat = "MP3" });
        Assert.False(result.Success);
        Assert.Equal(MediaInputFailureKind.InvalidMedia, result.FailureKind);
        Assert.False(result.UsedFallback);
        Assert.Contains("Error submitting packet to decoder", result.Detail);
        Assert.True(result.Detail.Length <= 2048);
        Assert.Equal([source], Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task ProductionCapabilityProbe_RetainsCompleteListsAndMp3CodecAlias()
    {
        var snapshot = await new FfmpegCapabilityService().ProbeAsync(Engine());
        Assert.True(snapshot.Decoders.Count > 20);
        Assert.True(snapshot.Encoders.Count > 20);
        Assert.True(snapshot.HasDecoder("mp3"));
        Assert.True(snapshot.HasEncoder("libmp3lame"));
        Assert.True(snapshot.HasMuxer("mp3"));
    }

    private static string Engine()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        if (ffmpeg is null || !File.Exists(FfmpegLocator.GetFfprobePath(ffmpeg)))
            throw SkipException.ForSkip("Local FFmpeg/FFprobe pair is unavailable.");
        return ffmpeg;
    }
}
