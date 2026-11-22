using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class CompatibleAudioTrackSmokeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SyntheticApacProbeChoice_ExecutesRealDecodeAndConversionWithOnlySelectedAacAndRequestedChannels(int? channels)
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "two-aac.mp4");
        await GenerateAsync(ffmpeg, input);
        var originalBytes = await File.ReadAllBytesAsync(input);
        var actual = await new MediaProbeService().ProbeAsync(ffprobe, input);
        Assert.Equal(2, actual.AudioTracks.Count);
        Assert.All(actual.AudioTracks, track => Assert.Equal("aac", track.Codec));
        var selectedGlobalIndex = actual.AudioTracks[1].StreamIndex;
        actual.AudioTracks[0].Codec = "apple_apac";
        actual.AudioTracks[0].CodecTag = "apac";
        // The physical source contains two AAC tracks. Only the injected probe marks
        // the unselected track as APAC, to test selection without claiming APAC decoding.
        var capabilities = await new FfmpegCapabilityService().ProbeAsync(ffmpeg);
        var decoded = false;
        var preflight = new MediaInputPreflightService(
            (_, _, _) => Task.FromResult(actual),
            (_, _) => Task.FromResult(capabilities),
            async (executable, task, probe, token) =>
            {
                var arguments = MediaInputPreflightService.BuildDecodeArguments(task, probe);
                var maps = Maps(arguments);
                Assert.Contains($"0:{selectedGlobalIndex}", maps);
                Assert.DoesNotContain($"0:{actual.AudioTracks[0].StreamIndex}", maps);
                var result = await ProcessRunner.RunAsync(executable, arguments, TimeSpan.FromSeconds(20), token);
                decoded = true;
                return new MediaDecodeCheckResult(result.ExitCode == 0, result.StandardError);
            },
            (_, _) => throw new InvalidOperationException("A compatible AAC choice must not need decoder fallback."));
        var task = new MediaTask { InputPath = input, SourceFormatId = "mp4", TargetFormatId = "mp4", Probe = actual, SelectedAudioTrack = 1 };
        task.Options[CompatibleAudioTrackPolicy.OptionKey] = CompatibleAudioTrackPolicy.CompatibleValue;
        new MediaConversionOptions { Channels = channels }.ApplyTo(task.Options);
        var outputDirectory = Path.Combine(directory.Path, "output");
        var result = await new ConversionService(preflight,
            new OutputRecoveryService(Path.Combine(directory.Path, "recovery"))).ConvertAsync(task, ffmpeg,
            new AppSettings { HardwareAcceleration = false, CpuThreadsPerTask = 1,
                OutputMode = "custom", CustomOutputDirectory = outputDirectory },
            _ => { }, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(decoded);
        Assert.True(File.Exists(result.OutputPath));
        var output = await new MediaProbeService().ProbeAsync(ffprobe, result.OutputPath);
        var audio = Assert.Single(output.AudioTracks);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal(channels ?? 2, audio.Channels);
        Assert.Equal("eng", audio.Language);
        Assert.True(output.VideoStreamIndex >= 0);
        Assert.True(MediaProbeService.ValidateCompatibleAudioOutput(task, output));
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
        Assert.Equal(input, task.InputPath);
        Assert.Same(actual, task.Probe);
        Assert.Equal(1, task.SelectedAudioTrack);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(outputDirectory));
        Assert.Empty(Directory.GetDirectories(outputDirectory));
    }

    [Fact]
    public async Task RealOutputValidation_RejectsTwoAudioTracksAndAcceptsOneStereoTrack()
    {
        var (ffmpeg, ffprobe) = Engines();
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "two-aac.mp4");
        var single = Path.Combine(directory.Path, "single-aac.mp4");
        await GenerateAsync(ffmpeg, input);
        var created = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input,
             "-map", "0:V:0", "-map", "0:a:1", "-c", "copy", single], TimeSpan.FromSeconds(20));
        Assert.True(created.ExitCode == 0, created.StandardError);
        var task = new MediaTask { InputPath = input, Kind = MediaKind.Video, TargetFormatId = "mp4" };
        var service = new MediaProbeService();
        Assert.True(await service.ValidateOutputAsync(ffprobe, input, task, null,
            CancellationToken.None, ffmpeg, validateCompatibleAudioSelection: false));
        Assert.False(await service.ValidateOutputAsync(ffprobe, input, task, null,
            CancellationToken.None, ffmpeg, validateCompatibleAudioSelection: true));
        Assert.True(await service.ValidateOutputAsync(ffprobe, single, task, null,
            CancellationToken.None, ffmpeg, validateCompatibleAudioSelection: true));
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task RealCompatibleIntermediate_RestoresExactOriginalChoiceAndSourceAfterFinalEncoding()
    {
        var (ffmpeg, ffprobe) = Engines();
        var configured = Environment.GetEnvironmentVariable("FORMAT_FACTORY_TEST_COMPATIBILITY_ENGINE_DIRECTORY");
        var fallback = Path.Combine(configured ?? Path.Combine(RepositoryTestContext.Root, "work", "compatibility-engine"), "ffmpeg.exe");
        if (!File.Exists(fallback) || !File.Exists(FfmpegLocator.GetFfprobePath(fallback)))
            throw SkipException.ForSkip("The compatibility FFmpeg and FFprobe pair is unavailable.");
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "two-aac.mp4");
        await GenerateAsync(ffmpeg, input);
        var originalBytes = await File.ReadAllBytesAsync(input);
        var actual = await new MediaProbeService().ProbeAsync(ffprobe, input);
        actual.AudioTracks[0].Codec = "apple_apac";
        actual.AudioTracks[0].CodecTag = "apac";
        var primaryCapabilities = await new FfmpegCapabilityService().ProbeAsync(ffmpeg);
        var fallbackCapabilities = await new FfmpegCapabilityService().ProbeAsync(fallback);
        var preflight = new MediaInputPreflightService(
            (_, _, _) => Task.FromResult(actual),
            (executable, _) => Task.FromResult(executable == FfmpegLocator.ResolveSymbolicLink(ffmpeg)
                ? primaryCapabilities with { Decoders = new HashSet<string> { "aac" }, DecoderCodecIds = new HashSet<string> { "aac" } }
                : fallbackCapabilities),
            async (executable, task, probe, token) =>
            {
                Assert.Equal(fallback, executable);
                var decoded = await ProcessRunner.RunAsync(executable,
                    MediaInputPreflightService.BuildDecodeArguments(task, probe), TimeSpan.FromSeconds(20), token);
                return new MediaDecodeCheckResult(decoded.ExitCode == 0, decoded.StandardError);
            },
            (_, _) => Task.FromResult(true));
        const string originalChoice = "CoMpAtIbLe";
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "mp4", TargetFormatId = "mp4", Probe = actual, SelectedAudioTrack = 1
        };
        task.Options[CompatibleAudioTrackPolicy.OptionKey] = originalChoice;
        var sawIntermediate = false;
        var outputDirectory = Path.Combine(directory.Path, "output");
        var result = await new ConversionService(preflight,
            new OutputRecoveryService(Path.Combine(directory.Path, "recovery"))).ConvertAsync(task, ffmpeg,
            new AppSettings { CompatibilityFfmpegPath = fallback, HardwareAcceleration = false,
                CpuThreadsPerTask = 1, OutputMode = "custom", CustomOutputDirectory = outputDirectory },
            _ =>
            {
                if (task.InputPath != input)
                {
                    sawIntermediate = true;
                    Assert.False(CompatibleAudioTrackPolicy.IsRequested(task));
                    Assert.Equal("ffv1", task.Probe.VideoCodec);
                    var pcm = Assert.Single(task.Probe.AudioTracks);
                    Assert.Equal("pcm_s32le", pcm.Codec);
                    Assert.Equal(2, pcm.Channels);
                }
            }, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(sawIntermediate);
        Assert.Equal(input, task.InputPath);
        Assert.Same(actual, task.Probe);
        Assert.Equal(1, task.SelectedAudioTrack);
        Assert.Equal(1, task.SelectedAudioTrack);
        Assert.Equal(originalChoice, task.Options[CompatibleAudioTrackPolicy.OptionKey]);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(input));
        var output = await new MediaProbeService().ProbeAsync(ffprobe, result.OutputPath);
        var audio = Assert.Single(output.AudioTracks);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal(2, audio.Channels);
        Assert.Equal("eng", audio.Language);
        Assert.True(output.VideoStreamIndex >= 0);
        Assert.Single(Directory.GetFiles(outputDirectory));
        Assert.Empty(Directory.GetDirectories(outputDirectory));
    }

    private static async Task GenerateAsync(string ffmpeg, string path)
    {
        var generated = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
             "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=0.4",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=0.4",
             "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=0.4",
             "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
             "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2",
             "-metadata:s:a:0", "language=qaa", "-metadata:s:a:1", "language=eng",
             "-threads", "1", "-filter_threads", "1", "-filter_complex_threads", "1", path],
            TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
    }

    private static string[] Maps(IReadOnlyList<string> arguments) => arguments
        .Select((argument, index) => (argument, index))
        .Where(item => item.argument == "-map")
        .Select(item => arguments[item.index + 1]).ToArray();

    private static (string Ffmpeg, string Ffprobe) Engines()
    {
        var ffmpeg = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        if (ffmpeg is null || !File.Exists(FfmpegLocator.GetFfprobePath(ffmpeg)))
            throw SkipException.ForSkip("Local FFmpeg and FFprobe pair is unavailable.");
        return (ffmpeg, FfmpegLocator.GetFfprobePath(ffmpeg));
    }
}
