using System.Globalization;
using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class AacSampleRateTests
{
    private const int SyntheticRate = 47997;

    [Theory]
    [InlineData("aac")]
    [InlineData("m4a")]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("mov")]
    [InlineData("ts")]
    [InlineData("flv")]
    [InlineData("h264")]
    [InlineData("hevc")]
    public void UnsupportedExplicitAacRate_IsRejectedWithoutChangingTheRequest(string target)
    {
        var task = CreateTask(target);
        var options = new MediaConversionOptions { SampleRate = SyntheticRate, Channels = 2 };
        options.ApplyTo(task.Options);
        var original = task.Options.ToArray();

        Assert.Throws<InvalidDataException>(() => options.Validate(task));
        Assert.Throws<InvalidDataException>(() => new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu));
        Assert.Equal(original, task.Options.ToArray());
        Assert.Equal(SyntheticRate, MediaConversionOptions.FromTask(task).SampleRate);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(12000)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(24000)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(64000)]
    [InlineData(88200)]
    [InlineData(96000)]
    public void SupportedExplicitAacRates_AreKeptExactly(int sampleRate)
    {
        foreach (var target in new[] { "aac", "m4a", "mp4" })
        {
            var task = CreateTask(target);
            new MediaConversionOptions { SampleRate = sampleRate }.ApplyTo(task.Options);
            var arguments = new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu).ToList();
            Assert.Equal("aac", arguments[arguments.IndexOf("-c:a") + 1]);
            Assert.Equal(sampleRate.ToString(CultureInfo.InvariantCulture), arguments[arguments.IndexOf("-ar") + 1]);
        }
    }

    public static IEnumerable<object[]> VideoPresets() =>
        FormatCatalog.All.Where(format => format.ContentKind == ContentKind.Video && format.CanExport)
            .SelectMany(format => Enum.GetValues<EncoderBackend>(), (format, backend) => new object[] { format.Id, backend });

    [Theory]
    [MemberData(nameof(VideoPresets))]
    public void VideoValidation_AgreesWithTheActualAudioEncoderForEveryBackend(string target, EncoderBackend backend)
    {
        var task = CreateTask(target);
        var preset = new PresetService();
        var defaults = preset.BuildArguments(task, "output.bin", backend).ToList();
        Assert.DoesNotContain("-ar", defaults);
        var audioEncoder = defaults[defaults.IndexOf("-c:a") + 1];
        new MediaConversionOptions { SampleRate = SyntheticRate }.ApplyTo(task.Options);

        if (audioEncoder is "aac" or "libmp3lame" or "libopus")
            Assert.Throws<InvalidDataException>(() => preset.BuildArguments(task, "output.bin", backend));
        else
        {
            var arguments = preset.BuildArguments(task, "output.bin", backend).ToList();
            Assert.Equal(audioEncoder, arguments[arguments.IndexOf("-c:a") + 1]);
            Assert.Equal(SyntheticRate.ToString(CultureInfo.InvariantCulture), arguments[arguments.IndexOf("-ar") + 1]);
        }

        // An audio parameter on a video without audio is not an AAC encoder request.
        task.Probe.AudioTracks = [];
        MediaConversionOptions.FromTask(task).Validate(task);
    }

    [Theory]
    [InlineData("wma")]
    [InlineData("flac")]
    [InlineData("wav")]
    [InlineData("aiff")]
    [InlineData("vorbis")]
    [InlineData("ogg")]
    [InlineData("alac")]
    [InlineData("ac3")]
    [InlineData("eac3")]
    public void OtherAudioEncoders_KeepTheirExistingExplicitRateSemantics(string target)
    {
        var task = CreateTask(target);
        new MediaConversionOptions { SampleRate = SyntheticRate }.ApplyTo(task.Options);
        var arguments = new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu).ToList();
        Assert.NotEqual("aac", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.Equal(SyntheticRate.ToString(CultureInfo.InvariantCulture), arguments[arguments.IndexOf("-ar") + 1]);
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("m4a")]
    [InlineData("mp4")]
    public void LosslessCopy_DoesNotResampleAndKeepsItsExistingCustomOptionsRejection(string target)
    {
        var task = CreateTask(target);
        task.Probe.VideoCodec = "h264";
        task.Probe.AudioTracks[0].Codec = "aac";
        task.ConversionMode = "lossless";
        var arguments = new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu);
        Assert.Contains("copy", arguments);
        Assert.DoesNotContain("-ar", arguments);

        new MediaConversionOptions { SampleRate = SyntheticRate }.ApplyTo(task.Options);
        Assert.Throws<NotSupportedException>(() => MediaConversionOptions.FromTask(task).Validate(task));
    }

    [Fact]
    public void UnsupportedExplicitRate_UsesTheExistingLocalizedParameterErrorInEveryLanguage()
    {
        var task = CreateTask("m4a");
        var options = new MediaConversionOptions { SampleRate = SyntheticRate };
        try
        {
            foreach (var language in LocalizationService.LanguageOptions.Where(language => language.Key != "auto"))
            {
                LocalizationService.SetLanguage(language.Key);
                var error = Assert.Throws<InvalidDataException>(() => options.Validate(task));
                Assert.Equal(LocalizationService.T("MediaOptionsInvalidValue", LocalizationService.T("MediaSampleRate")), error.Message);
                Assert.DoesNotContain("MediaOptionsInvalidValue", error.Message);
                Assert.DoesNotContain("MediaSampleRate", error.Message);
            }
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task RealNonstandardSource_DefaultNegotiationWorksAndExplicitSupportedRateIsExact()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var ffprobe = LocalFfmpegSmokeTests.FindExecutable("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null) throw SkipException.ForSkip("Local FFmpeg is unavailable.");
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "synthetic-nonstandard.mkv");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=blue:s=16x16:r=10:d=0.4",
             "-f", "lavfi", "-i", $"sine=frequency=330:sample_rate={SyntheticRate}:duration=0.4",
             "-c:v", "ffv1", "-c:a", "pcm_s16le", source], TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var sourceBytes = await File.ReadAllBytesAsync(source);
        Assert.Equal(SyntheticRate, await ReadRateAsync(ffprobe, source));

        // An equivalent, independently chosen rate demonstrates the encoder's exact-rate contract.
        var rejected = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-vn", "-c:a", "aac",
             "-ar", SyntheticRate.ToString(CultureInfo.InvariantCulture), Path.Combine(directory.Path, "rejected.m4a")],
            TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("Specified sample rate", rejected.StandardError);
        Assert.Contains("is not supported", rejected.StandardError);

        var probe = await new MediaProbeService().ProbeAsync(ffprobe, source);
        foreach (var (target, requestedRate, expectedRate) in new (string, int?, int)[]
                 { ("m4a", null, 48000), ("mp4", null, 48000), ("m4a", 44100, 44100), ("mp4", 48000, 48000) })
        {
            var task = new MediaTask { InputPath = source, SourceFormatId = "mkv", TargetFormatId = target, Probe = probe };
            new MediaConversionOptions { SampleRate = requestedRate }.ApplyTo(task.Options);
            var output = Path.Combine(directory.Path, $"converted-{target}-{expectedRate}.{target}");
            var arguments = new PresetService().BuildArguments(task, output, EncoderBackend.Cpu, cpuThreads: 1);
            Assert.Equal(requestedRate.HasValue, arguments.Contains("-ar"));
            var converted = await ProcessRunner.RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(20));
            Assert.True(converted.ExitCode == 0, converted.StandardError);
            Assert.Equal(expectedRate, await ReadRateAsync(ffprobe, output));
            Assert.True(await new MediaProbeService().ValidateOutputAsync(ffprobe, output));
        }
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
    }

    private static async Task<int> ReadRateAsync(string ffprobe, string path)
    {
        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate", "-of", "json", path],
            TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        return int.Parse(parsed.RootElement.GetProperty("streams")[0].GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
    }

    private static MediaTask CreateTask(string target) => new()
    {
        InputPath = "synthetic.mkv", SourceFormatId = "mkv", TargetFormatId = target,
        Probe = new MediaProbeResult
        {
            Kind = MediaKind.Video, VideoCodec = "ffv1", Width = 16, Height = 16,
            AudioTracks = [new() { Codec = "pcm_s16le", Channels = 2 }]
        }
    };
}
