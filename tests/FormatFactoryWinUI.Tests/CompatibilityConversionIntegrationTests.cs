using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class CompatibilityConversionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackDecode_UsesPrimaryEncoderOriginalNameAndValidatedRecovery(bool preventCommit)
    {
        var primary = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var fallback = Path.Combine(RepositoryTestContext.Root, "work/compatibility-engine/ffmpeg.exe");
        if (primary is null || !File.Exists(fallback)) throw SkipException.ForSkip("Primary and compatibility engines are unavailable.");
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.mkv");
        var output = Path.Combine(directory.Path, "output");
        var generated = await ProcessRunner.RunAsync(primary,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=0.3", "-t", "0.3", "-c:v", "ffv1", "-c:a", "pcm_s16le", source],
            TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var originalBytes = await File.ReadAllBytesAsync(source);
        var probing = new MediaProbeService();
        var sourceProbe = await probing.ProbeAsync(FfmpegLocator.GetFfprobePath(primary), source);
        var capabilities = await new FfmpegCapabilityService().ProbeAsync(fallback);
        // Deliberately remove the primary's decoder in this fixture to exercise
        // the production fallback branch with real decoding and final encoding.
        var preflight = new MediaInputPreflightService(
            (_, _, _) => Task.FromResult(sourceProbe),
            (path, _) => Task.FromResult(path == primary ? capabilities with
                { Decoders = new HashSet<string>(), DecoderCodecIds = new HashSet<string>() } : capabilities),
            (_, _, _, _) => Task.FromResult(new MediaDecodeCheckResult(true, string.Empty)),
            (_, _) => Task.FromResult(true));
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var task = new MediaTask { InputPath = source, Probe = sourceProbe, TargetFormatId = "mp4", SourceFormatId = "mkv" };
        var blocker = Path.Combine(output, "source.mp4");
        var result = await new ConversionService(preflight, recovery).ConvertAsync(task, primary,
            new AppSettings { CompatibilityFfmpegPath = fallback, CustomOutputDirectory = output, HardwareAcceleration = false },
            value =>
            {
                if (preventCommit && value.Message.StartsWith("CPU", StringComparison.Ordinal) && !File.Exists(blocker))
                    File.WriteAllText(blocker, "unrelated existing output");
            }, CancellationToken.None);
        Assert.Equal(source, task.InputPath);
        Assert.Same(sourceProbe, task.Probe);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.EnumerateDirectories(output));
        var completedPath = result.OutputPath;
        if (preventCommit)
        {
            Assert.False(result.Success);
            Assert.Empty(result.OutputPath);
            Assert.Equal("unrelated existing output", await File.ReadAllTextAsync(blocker));
            var retained = recovery.GetById(result.RecoverableOutputId);
            Assert.NotNull(retained);
            Assert.Equal("mp4", retained.TargetFormatId);
            completedPath = await recovery.SaveAsAsync(retained.Id, Path.Combine(directory.Path, "saved"));
            Assert.Empty(recovery.Load());
        }
        else
        {
            Assert.True(result.Success, result.Message);
            Assert.Equal(blocker, result.OutputPath);
            Assert.Null(result.RecoverableOutputId);
        }
        var encoded = await probing.ProbeAsync(FfmpegLocator.GetFfprobePath(primary), completedPath);
        Assert.Equal("h264", encoded.VideoCodec);
        Assert.Equal("aac", Assert.Single(encoded.AudioTracks).Codec);
    }
}
