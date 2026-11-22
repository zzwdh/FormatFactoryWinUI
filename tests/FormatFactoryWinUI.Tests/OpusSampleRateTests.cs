using System.Globalization;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class OpusSampleRateTests
{
    private const int SyntheticRate = 33001;

    [Theory]
    [InlineData(SyntheticRate)]
    [InlineData(11025)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void UnsupportedExplicitOpusRates_AreRejectedWithoutChangingTheRequest(int sampleRate)
    {
        foreach (var target in new[] { "opus", "webm", "vp9", "av1" })
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
    [InlineData(12000)]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(48000)]
    public void SupportedExplicitOpusRates_AreKeptExactly(int sampleRate)
    {
        foreach (var target in new[] { "opus", "webm", "vp9", "av1" })
        {
            var task = CreateTask(target);
            new MediaConversionOptions { SampleRate = sampleRate }.ApplyTo(task.Options);
            var arguments = new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu).ToList();
            Assert.Equal("libopus", arguments[arguments.IndexOf("-c:a") + 1]);
            Assert.Equal(sampleRate.ToString(CultureInfo.InvariantCulture), arguments[arguments.IndexOf("-ar") + 1]);
        }
    }

    [Theory]
    [InlineData(8000, true)]
    [InlineData(12000, true)]
    [InlineData(16000, true)]
    [InlineData(24000, true)]
    [InlineData(48000, true)]
    [InlineData(SyntheticRate, false)]
    [InlineData(44100, false)]
    public void AudioSource_UsesTheSameOpusRateValidation(int sampleRate, bool supported)
    {
        var task = CreateTask("opus");
        task.Kind = MediaKind.Audio;
        task.SourceFormatId = "wav";
        new MediaConversionOptions { SampleRate = sampleRate }.ApplyTo(task.Options);
        if (supported)
        {
            var arguments = new PresetService().BuildArguments(task, "output.opus", EncoderBackend.Cpu).ToList();
            Assert.Equal("libopus", arguments[arguments.IndexOf("-c:a") + 1]);
            Assert.Equal(sampleRate.ToString(CultureInfo.InvariantCulture), arguments[arguments.IndexOf("-ar") + 1]);
        }
        else
            Assert.Throws<InvalidDataException>(() => new PresetService().BuildArguments(task, "output.opus", EncoderBackend.Cpu));
    }

    [Theory]
    [InlineData(EncoderBackend.Cpu)]
    [InlineData(EncoderBackend.Intel)]
    [InlineData(EncoderBackend.Nvidia)]
    [InlineData(EncoderBackend.Amd)]
    [InlineData(EncoderBackend.MediaFoundation)]
    public void VideoValidation_AgreesWithItsActualOpusEncoderForEveryBackend(EncoderBackend backend)
    {
        foreach (var target in new[] { "webm", "vp9", "av1" })
        {
            var task = CreateTask(target);
            var preset = new PresetService();
            var defaults = preset.BuildArguments(task, "output.webm", backend).ToList();
            Assert.Equal("libopus", defaults[defaults.IndexOf("-c:a") + 1]);
            Assert.DoesNotContain("-ar", defaults);
            new MediaConversionOptions { SampleRate = SyntheticRate }.ApplyTo(task.Options);
            Assert.Throws<InvalidDataException>(() => preset.BuildArguments(task, "output.webm", backend));

            task.Probe.AudioTracks = [];
            MediaConversionOptions.FromTask(task).Validate(task);
        }
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("webm")]
    [InlineData("vp9")]
    [InlineData("av1")]
    public void AbsentRate_LeavesOpusNegotiationToTheEncoder(string target)
    {
        var task = CreateTask(target);
        var arguments = new PresetService().BuildArguments(task, "output.bin", EncoderBackend.Cpu).ToList();
        Assert.Equal("libopus", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-ar", arguments);
        Assert.Null(MediaConversionOptions.FromTask(task).SampleRate);
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("webm")]
    public void LosslessCopy_KeepsItsExistingCustomOptionsRejection(string target)
    {
        var task = CreateTask(target);
        task.Probe.VideoCodec = "vp9";
        task.Probe.AudioTracks[0].Codec = "opus";
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
        var task = CreateTask("opus");
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
    public async Task RealNonstandardSource_DefaultAndSupportedRateSucceed_UnsupportedRateNeverReservesOutput()
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
        var probeService = new MediaProbeService();
        var probe = await probeService.ProbeAsync(ffprobe, source);

        var rejected = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-vn", "-c:a", "libopus",
             "-ar", SyntheticRate.ToString(CultureInfo.InvariantCulture), Path.Combine(directory.Path, "rejected.opus")],
            TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("Specified sample rate", rejected.StandardError);
        Assert.Contains("is not supported", rejected.StandardError);

        foreach (var target in new[] { "opus", "webm", "vp9", "av1" })
        {
            var task = CreateTask(target);
            task.InputPath = source;
            task.Probe = probe;
            new MediaConversionOptions { SampleRate = SyntheticRate }.ApplyTo(task.Options);
            var settings = new AppSettings
            {
                OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, $"blocked-{target}"),
                HardwareAcceleration = false, CpuThreadsPerTask = 1
            };
            var result = await new ConversionService().ConvertAsync(task, ffmpeg, settings, _ => { }, default);
            Assert.False(result.Success);
            Assert.Equal(LocalizationService.T("MediaOptionsInvalidValue", LocalizationService.T("MediaSampleRate")), result.Message);
            Assert.Empty(result.OutputPath);
            Assert.False(Directory.Exists(settings.CustomOutputDirectory));
            Assert.Equal(SyntheticRate, MediaConversionOptions.FromTask(task).SampleRate);
        }

        foreach (var (target, requestedRate) in new (string, int?)[]
                 { ("opus", null), ("webm", null), ("vp9", 24000), ("av1", 48000) })
        {
            var task = CreateTask(target);
            task.InputPath = source;
            task.Probe = probe;
            new MediaConversionOptions { SampleRate = requestedRate }.ApplyTo(task.Options);
            var output = Path.Combine(directory.Path, $"converted-{target}.{(target == "opus" ? "opus" : "webm")}");
            var arguments = new PresetService().BuildArguments(task, output, EncoderBackend.Cpu, cpuThreads: 1);
            Assert.Equal(requestedRate.HasValue, arguments.Contains("-ar"));
            var converted = await ProcessRunner.RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(30));
            Assert.True(converted.ExitCode == 0, converted.StandardError);
            Assert.Equal("opus", Assert.Single((await probeService.ProbeAsync(ffprobe, output)).AudioTracks).Codec);
            Assert.True(await probeService.ValidateOutputAsync(ffprobe, output));
        }
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
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
