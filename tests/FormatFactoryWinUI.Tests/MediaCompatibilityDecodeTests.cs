using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaCompatibilityDecodeTests
{
    [Fact]
    public async Task PreparedInput_IsValidatedByPrimaryProbeAndCleansOnlyItsPrivateDirectory()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Service.PrepareAsync(fixture.Fallback, fixture.Primary, fixture.Task, fixture.Work);
        var path = prepared.Path;
        Assert.True(File.Exists(path));
        Assert.Same(fixture.IntermediateProbe, prepared.Probe);
        Assert.Equal(FfmpegLocator.GetFfprobePath(fixture.Primary), fixture.ProbedExecutable);
        Assert.Equal(fixture.Task.InputPath, fixture.Source);
        prepared.Dispose(); prepared.Dispose();
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Work));
        Assert.True(File.Exists(fixture.Source));
    }

    [Fact]
    public async Task FailedDecode_CleansPartialIntermediateAndPreservesSource()
    {
        using var fixture = new Fixture { ExitCode = 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PrepareAsync(
            fixture.Fallback, fixture.Primary, fixture.Task, fixture.Work));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Work));
        Assert.True(File.Exists(fixture.Source));
    }

    [Fact]
    public async Task CancelledDecode_PropagatesAndCleansItsPartialOutput()
    {
        using var fixture = new Fixture { CancelAfterWriting = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.PrepareAsync(
            fixture.Fallback, fixture.Primary, fixture.Task, fixture.Work));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Work));
    }

    [Fact]
    public async Task UnexpectedIntermediateStreams_AreRejectedAndCleaned()
    {
        using var fixture = new Fixture();
        fixture.IntermediateProbe.AudioTracks = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PrepareAsync(
            fixture.Fallback, fixture.Primary, fixture.Task, fixture.Work));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Work));
    }

    [Fact]
    public void AudioIntermediate_MapsOnlySelectedGlobalStreamAndNeverReencodesToFinalFormat()
    {
        var task = new MediaTask
        {
            InputPath = "synthetic.mp4", TargetFormat = "MP3", SelectedAudioTrack = 1,
            Probe = new() { Kind = MediaKind.Video, VideoStreamIndex = 0, AudioTracks =
                [new() { Codec = "aac", StreamIndex = 3 }, new() { Codec = "aac", StreamIndex = 7 }] }
        };
        var arguments = MediaCompatibilityDecodeService.BuildArguments(task, "decoded.mkv");
        Assert.Contains("0:7", arguments);
        Assert.DoesNotContain("0:3", arguments);
        Assert.DoesNotContain("0:0", arguments);
        Assert.Contains("pcm_s32le", arguments);
        Assert.DoesNotContain("libmp3lame", arguments);
        Assert.DoesNotContain("-t", arguments);
    }

    [Fact]
    public async Task RealCompatibilityDecode_ThenPrimaryH264AacEncode_SucceedsAndCleansIntermediate()
    {
        var primary = LocalFfmpegSmokeTests.FindExecutable("ffmpeg.exe");
        var configured = Environment.GetEnvironmentVariable("FORMAT_FACTORY_TEST_COMPATIBILITY_ENGINE_DIRECTORY");
        var fallback = Path.Combine(configured ?? Path.Combine(RepositoryTestContext.Root, "work", "compatibility-engine"), "ffmpeg.exe");
        if (primary is null || !File.Exists(fallback)) throw SkipException.ForSkip("Compatibility and primary FFmpeg engines are unavailable.");
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "synthetic-source.mkv");
        var output = Path.Combine(directory.Path, "final.mp4");
        var generated = await ProcessRunner.RunAsync(primary,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=0.3", "-t", "0.3", "-c:v", "ffv1", "-c:a", "pcm_s16le", source],
            TimeSpan.FromSeconds(20));
        Assert.True(generated.ExitCode == 0, generated.StandardError);
        var probeService = new MediaProbeService();
        var task = new MediaTask
        {
            InputPath = source, TargetFormat = "MP4", Probe = await probeService.ProbeAsync(FfmpegLocator.GetFfprobePath(primary), source)
        };
        string intermediatePath;
        using (var prepared = await new MediaCompatibilityDecodeService().PrepareAsync(fallback, primary, task,
            Path.Combine(directory.Path, "work")))
        {
            intermediatePath = prepared.Path;
            Assert.Equal("ffv1", prepared.Probe.VideoCodec);
            Assert.Equal("pcm_s32le", Assert.Single(prepared.Probe.AudioTracks).Codec);
            var encoded = await ProcessRunner.RunAsync(primary,
                ["-hide_banner", "-loglevel", "error", "-i", prepared.Path, "-map", "0:V:0", "-map", "0:a?",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-threads", "1", output], TimeSpan.FromSeconds(20));
            Assert.True(encoded.ExitCode == 0, encoded.StandardError);
            var result = await probeService.ProbeAsync(FfmpegLocator.GetFfprobePath(primary), output);
            Assert.Equal("h264", result.VideoCodec);
            Assert.Equal("aac", Assert.Single(result.AudioTracks).Codec);
        }
        Assert.False(File.Exists(intermediatePath));
        Assert.True(File.Exists(source));
        Assert.True(new FileInfo(output).Length > 0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public string Source { get; }
        public string Work { get; }
        public string Primary => Path.Combine(_directory.Path, "primary", "ffmpeg.exe");
        public string Fallback => Path.Combine(_directory.Path, "fallback", "ffmpeg.exe");
        public MediaTask Task { get; }
        public MediaProbeResult IntermediateProbe { get; } = new()
        {
            Kind = MediaKind.Video, VideoCodec = "ffv1", VideoStreamIndex = 0, Width = 64, Height = 64,
            AudioTracks = [new() { Codec = "pcm_s32le", Channels = 2, StreamIndex = 1 }]
        };
        public int ExitCode { get; init; }
        public bool CancelAfterWriting { get; init; }
        public string? ProbedExecutable { get; private set; }
        public Fixture()
        {
            Source = Path.Combine(_directory.Path, "source.mkv"); Work = Path.Combine(_directory.Path, "work");
            File.WriteAllText(Source, "synthetic source");
            Task = new() { InputPath = Source, TargetFormat = "MP4", Probe = new()
            {
                Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 0, Width = 64, Height = 64,
                AudioTracks = [new() { Codec = "aac", Channels = 2, StreamIndex = 1 }]
            } };
        }
        public MediaCompatibilityDecodeService Service => new(
            (_, arguments, _, _) =>
            {
                File.WriteAllText(arguments[^1], "synthetic decoded intermediate");
                if (CancelAfterWriting) throw new OperationCanceledException();
                return System.Threading.Tasks.Task.FromResult(new ProcessExecutionResult(ExitCode, string.Empty, "Synthetic decode failure"));
            },
            (executable, _, _) => { ProbedExecutable = executable; return System.Threading.Tasks.Task.FromResult(IntermediateProbe); });
        public void Dispose() => _directory.Dispose();
    }
}
