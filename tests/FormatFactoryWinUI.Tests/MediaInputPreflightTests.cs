using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaInputPreflightTests
{
    [Fact]
    public async Task HealthyPrimary_UsesActualProbeAndDoesNotInspectConfiguredFallback()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.True(result.Success);
        Assert.Equal(fixture.Primary, result.ExecutablePath);
        Assert.Same(fixture.PrimaryProbe, result.Probe);
        Assert.False(result.UsedFallback);
        Assert.False(fixture.FallbackValidated);
        Assert.Equal([fixture.Primary], fixture.DecodedExecutables);
    }

    [Fact]
    public async Task UnknownCodecTag_IsPreservedAndCompatibilityEngineReprobesSameFile()
    {
        var fixture = new Fixture();
        fixture.PrimaryProbe.VideoCodec = string.Empty;
        fixture.PrimaryProbe.VideoCodecTag = "synthetic_fourcc";
        fixture.FallbackProbe.VideoCodec = "compatible_codec";
        fixture.FallbackCapabilities = Capabilities("compatible_codec", "mp3");
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(fixture.Fallback, result.ExecutablePath);
        Assert.Same(fixture.FallbackProbe, result.Probe);
        Assert.Equal([fixture.Fallback], fixture.DecodedExecutables);
        Assert.Equal([fixture.Task.InputPath, fixture.Task.InputPath], fixture.ProbedInputs);
        Assert.Equal([FfmpegLocator.GetFfprobePath(fixture.Primary), FfmpegLocator.GetFfprobePath(fixture.Fallback)], fixture.ProbedExecutables);
    }

    [Fact]
    public async Task UnknownCodecTag_WithoutFallbackReturnsTypedUnsupportedDecoder()
    {
        var fixture = new Fixture();
        fixture.PrimaryProbe.VideoCodec = string.Empty;
        fixture.PrimaryProbe.VideoCodecTag = "synthetic_fourcc";
        var result = await fixture.Service.SelectAsync(fixture.Primary, null, fixture.Task);
        Assert.False(result.Success);
        Assert.Equal(MediaInputFailureKind.UnsupportedDecoder, result.FailureKind);
        Assert.Contains("synthetic_fourcc", result.Detail);
        Assert.Empty(fixture.DecodedExecutables);
    }

    [Fact]
    public async Task DecoderFailureForDamagedInput_DoesNotSwitchEngines()
    {
        var fixture = new Fixture { DecodeResult = new(false, "Error submitting packet to decoder: invalid synthetic data") };
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.False(result.Success);
        Assert.Equal(MediaInputFailureKind.InvalidMedia, result.FailureKind);
        Assert.False(fixture.FallbackValidated);
        Assert.Equal([fixture.Primary], fixture.DecodedExecutables);
    }

    [Fact]
    public async Task DecoderMissingAtRuntime_IsEligibleForCompatibilityEngine()
    {
        var fixture = new Fixture();
        fixture.DecodeHandler = (path, _, _, _) => Task.FromResult(path == fixture.Primary
            ? new MediaDecodeCheckResult(false, "Decoding requested, but no decoder found for synthetic codec")
            : new MediaDecodeCheckResult(true, string.Empty));
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal([fixture.Primary, fixture.Fallback], fixture.DecodedExecutables);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackRequiresIntermediateMuxerAndEncoder(bool missingEncoder)
    {
        var fixture = new Fixture();
        fixture.PrimaryProbe.VideoCodec = "unsupported_codec";
        fixture.FallbackCapabilities = Capabilities("h264", "mp3") with
        {
            Muxers = new HashSet<string>(missingEncoder ? ["matroska"] : [], StringComparer.OrdinalIgnoreCase),
            Encoders = new HashSet<string>(missingEncoder ? ["pcm_s32le"] : ["ffv1", "pcm_s32le"], StringComparer.OrdinalIgnoreCase)
        };
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.False(result.Success);
        Assert.Equal(MediaInputFailureKind.MissingOutputComponent, result.FailureKind);
        Assert.Contains(missingEncoder ? "ffv1" : "matroska", result.Detail);
        Assert.Empty(fixture.DecodedExecutables);
    }

    [Fact]
    public async Task FallbackThatStillCannotDecode_IsRejectedBeforeDecodeProcess()
    {
        var fixture = new Fixture();
        fixture.PrimaryProbe.VideoCodec = "unsupported_codec";
        fixture.FallbackProbe.VideoCodec = "unsupported_codec";
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.False(result.Success);
        Assert.Equal(MediaInputFailureKind.UnsupportedDecoder, result.FailureKind);
        Assert.Empty(fixture.DecodedExecutables);
    }

    [Fact]
    public async Task FallbackWithoutMatchingRunnablePair_IsRejected()
    {
        var fixture = new Fixture { ValidFallbackPair = false };
        fixture.PrimaryProbe.VideoCodec = "unsupported_codec";
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.Equal(MediaInputFailureKind.EngineUnavailable, result.FailureKind);
        Assert.Single(fixture.ProbedInputs);
        Assert.Empty(fixture.DecodedExecutables);
    }

    [Fact]
    public async Task MissingPrimaryOutputEncoder_DoesNotTriggerInputDecoderFallback()
    {
        var fixture = new Fixture();
        fixture.PrimaryCapabilities = Capabilities("h264", "mp3") with { Encoders = new HashSet<string>() };
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.Equal(MediaInputFailureKind.MissingOutputComponent, result.FailureKind);
        Assert.False(fixture.FallbackValidated);
    }

    [Fact]
    public async Task UnreadableInput_DoesNotTriggerDecoderFallback()
    {
        var fixture = new Fixture { ProbeError = new InvalidDataException("Synthetic container is invalid") };
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.Equal(MediaInputFailureKind.UnreadableInput, result.FailureKind);
        Assert.False(fixture.FallbackValidated);
    }

    [Fact]
    public async Task DecodeTimeout_IsExplicitAndDoesNotTriggerFallback()
    {
        var fixture = new Fixture();
        fixture.DecodeHandler = (_, _, _, _) => throw new TimeoutException("Synthetic decoder timed out");
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.Equal(MediaInputFailureKind.DecodeTimedOut, result.FailureKind);
        Assert.False(fixture.FallbackValidated);
    }

    [Fact]
    public async Task Cancellation_PropagatesFromShortDecode()
    {
        var fixture = new Fixture();
        fixture.DecodeHandler = (_, _, _, _) => throw new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task));
        Assert.False(fixture.FallbackValidated);
    }

    [Fact]
    public async Task StreamCopy_DoesNotRequireInputDecoderEncoderOrShortDecode()
    {
        var fixture = new Fixture();
        fixture.Task.ConversionMode = "lossless";
        fixture.Task.TargetFormat = "MKV";
        fixture.PrimaryProbe.VideoCodec = "unsupported_codec";
        fixture.PrimaryCapabilities = new(new HashSet<string> { "matroska" });
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.True(result.Success);
        Assert.False(fixture.FallbackValidated);
        Assert.Empty(fixture.DecodedExecutables);
    }

    [Fact]
    public async Task StaticImages_DoNotLaunchAnAdditionalDecodeProcess()
    {
        var fixture = new Fixture();
        fixture.Task.TargetFormat = "PNG";
        fixture.PrimaryProbe = new() { Kind = MediaKind.Image, VideoCodec = "png", VideoStreamIndex = 0 };
        fixture.PrimaryCapabilities = new(new HashSet<string> { "image2" })
        { Decoders = new HashSet<string> { "png" }, Encoders = new HashSet<string> { "png" } };
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.True(result.Success);
        Assert.Empty(fixture.DecodedExecutables);
    }

    [Fact]
    public async Task AudioExtraction_IgnoresUnsupportedUnusedVideoStream()
    {
        var fixture = new Fixture();
        fixture.Task.TargetFormat = "MP3";
        fixture.PrimaryProbe.VideoCodec = "unsupported_codec";
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.True(result.Success);
        Assert.False(fixture.FallbackValidated);
    }

    [Fact]
    public void DecodeMapping_UsesActualGlobalStreamIndexAndSelectedAudioTrack()
    {
        var fixture = new Fixture();
        fixture.Task.TargetFormat = "MP3";
        fixture.PrimaryProbe.AudioTracks =
        [new() { RelativeIndex = 0, StreamIndex = 3, Codec = "mp3" }, new() { RelativeIndex = 1, StreamIndex = 7, Codec = "aac" }];
        fixture.Task.SelectedAudioTrack = 1;
        var arguments = MediaInputPreflightService.BuildDecodeArguments(fixture.Task, fixture.PrimaryProbe);
        Assert.Contains("0:7", arguments);
        Assert.DoesNotContain("0:3", arguments);
        Assert.Contains("-xerror", arguments);
        Assert.DoesNotContain("discardcorrupt", arguments);
    }

    [Fact]
    public async Task InvalidSelectedTrack_IsRejectedBeforeDecode()
    {
        var fixture = new Fixture();
        fixture.Task.TargetFormat = "MP3";
        fixture.Task.SelectedAudioTrack = 10;
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, fixture.Task);
        Assert.Equal(MediaInputFailureKind.InvalidMedia, result.FailureKind);
        Assert.Empty(fixture.DecodedExecutables);
        Assert.False(fixture.FallbackValidated);
    }

    private static FfmpegCapabilitySnapshot Capabilities(params string[] decoders) => new(
        new HashSet<string> { "mp4", "mp3", "matroska" })
    {
        Decoders = new HashSet<string>(decoders), Encoders = new HashSet<string> { "libx264", "libmp3lame", "aac", "ffv1", "pcm_s32le" }
    };

    private sealed class Fixture
    {
        public string Primary { get; } = Path.GetFullPath("test-engines/primary/ffmpeg.exe");
        public string Fallback { get; } = Path.GetFullPath("test-engines/fallback/ffmpeg.exe");
        public MediaTask Task { get; } = new() { InputPath = "synthetic.mp4", Kind = MediaKind.Video, TargetFormat = "MP4" };
        public MediaProbeResult PrimaryProbe { get; set; } = CreateProbe();
        public MediaProbeResult FallbackProbe { get; set; } = CreateProbe();
        public FfmpegCapabilitySnapshot PrimaryCapabilities { get; set; } = Capabilities("h264", "mp3");
        public FfmpegCapabilitySnapshot FallbackCapabilities { get; set; } = Capabilities("h264", "mp3");
        public MediaDecodeCheckResult DecodeResult { get; set; } = new(true, string.Empty);
        public Func<string, MediaTask, MediaProbeResult, CancellationToken, Task<MediaDecodeCheckResult>>? DecodeHandler { get; set; }
        public Exception? ProbeError { get; set; }
        public bool ValidFallbackPair { get; set; } = true;
        public bool FallbackValidated { get; private set; }
        public List<string> DecodedExecutables { get; } = [];
        public List<string> ProbedInputs { get; } = [];
        public List<string> ProbedExecutables { get; } = [];
        public MediaInputPreflightService Service => new(
            (executable, input, _) =>
            {
                ProbedInputs.Add(input); ProbedExecutables.Add(executable);
                if (ProbeError is not null) throw ProbeError;
                return System.Threading.Tasks.Task.FromResult(executable == FfmpegLocator.GetFfprobePath(Primary) ? PrimaryProbe : FallbackProbe);
            },
            (executable, _) => System.Threading.Tasks.Task.FromResult(executable == Primary ? PrimaryCapabilities : FallbackCapabilities),
            (executable, task, probe, token) =>
            {
                DecodedExecutables.Add(executable);
                return DecodeHandler?.Invoke(executable, task, probe, token) ?? System.Threading.Tasks.Task.FromResult(DecodeResult);
            },
            (_, _) => { FallbackValidated = true; return System.Threading.Tasks.Task.FromResult(ValidFallbackPair); });
        private static MediaProbeResult CreateProbe() => new()
        {
            Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 2,
            AudioTracks = [new() { Codec = "mp3", Channels = 2, RelativeIndex = 0, StreamIndex = 4 }]
        };
    }
}
