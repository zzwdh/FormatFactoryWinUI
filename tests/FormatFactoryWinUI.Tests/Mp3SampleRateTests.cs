using System.Globalization;
using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class Mp3SampleRateTests
{
    private const int SyntheticRate = 43999;

    [Theory]
    [InlineData(SyntheticRate)]
    [InlineData(64000)]
    [InlineData(88200)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void UnsupportedExplicitMp3Rate_IsRejectedWithoutChangingTheRequest(int sampleRate)
    {
        foreach (var target in new[] { "mp3", "avi" })
        {
            var task = CreateTask(target);
            var options = new MediaConversionOptions { SampleRate = sampleRate, Channels = 2 };
            options.ApplyTo(task.Options);
            var original = task.Options.ToArray();

            Assert.Throws<InvalidDataException>(() => options.Validate(task));
            Assert.Throws<InvalidDataException>(() => new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu));
            Assert.Equal(original, task.Options.ToArray());
            Assert.Equal(sampleRate, MediaConversionOptions.FromTask(task).SampleRate);
        }
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
    public void SupportedExplicitMp3Rates_AreKeptExactly(int sampleRate)
    {
        foreach (var target in new[] { "mp3", "avi" })
        {
            var task = CreateTask(target);
            new MediaConversionOptions { SampleRate = sampleRate }.ApplyTo(task.Options);
            var arguments = new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu).ToList();
            Assert.Equal("libmp3lame", arguments[arguments.IndexOf("-c:a") + 1]);
            Assert.Equal(sampleRate.ToString(CultureInfo.InvariantCulture), arguments[arguments.IndexOf("-ar") + 1]);
        }
    }

    [Theory]
    [InlineData(EncoderBackend.Cpu)]
    [InlineData(EncoderBackend.Intel)]
    [InlineData(EncoderBackend.Nvidia)]
    [InlineData(EncoderBackend.Amd)]
    [InlineData(EncoderBackend.MediaFoundation)]
    public void AviValidation_AgreesWithItsActualMp3EncoderForEveryBackend(EncoderBackend backend)
    {
        var task = CreateTask("avi");
        var preset = new PresetService();
        var defaults = preset.BuildArguments(task, "output.avi", backend).ToList();
        Assert.Equal("libmp3lame", defaults[defaults.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-ar", defaults);
        new MediaConversionOptions { SampleRate = SyntheticRate }.ApplyTo(task.Options);
        Assert.Throws<InvalidDataException>(() => preset.BuildArguments(task, "output.avi", backend));

        // With no audio stream, this is not a request to resample MP3 audio.
        task.Probe.AudioTracks = [];
        MediaConversionOptions.FromTask(task).Validate(task);
    }

    [Fact]
    public void DefaultMp3Rate_DoesNotForceTheSourceRateIntoTheEncoder()
    {
        var task = CreateTask("mp3");
        var arguments = new PresetService().BuildArguments(task, "output.mp3", EncoderBackend.Cpu).ToList();
        Assert.Equal("libmp3lame", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-ar", arguments);
        Assert.Null(MediaConversionOptions.FromTask(task).SampleRate);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("avi")]
    public void LosslessCopy_DoesNotResampleAndKeepsItsExistingCustomOptionsRejection(string target)
    {
        var task = CreateTask(target);
        task.Probe.VideoCodec = "mpeg4";
        task.Probe.AudioTracks[0].Codec = "mp3";
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
        var task = CreateTask("mp3");
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

        var rejected = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-vn", "-c:a", "libmp3lame",
             "-ar", SyntheticRate.ToString(CultureInfo.InvariantCulture), Path.Combine(directory.Path, "rejected.mp3")],
            TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("Specified sample rate", rejected.StandardError);
        Assert.Contains("is not supported", rejected.StandardError);

        var probe = await new MediaProbeService().ProbeAsync(ffprobe, source);
        foreach (var (target, requestedRate, expectedRate) in new (string, int?, int)[]
                 { ("mp3", null, 44100), ("avi", null, 44100), ("mp3", 44100, 44100), ("avi", 48000, 48000) })
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
            Kind = target == "mp3" ? MediaKind.Audio : MediaKind.Video,
            VideoCodec = target == "mp3" ? string.Empty : "ffv1", Width = 16, Height = 16,
            AudioTracks = [new() { Codec = "pcm_s16le", Channels = 2 }]
        }
    };
}
