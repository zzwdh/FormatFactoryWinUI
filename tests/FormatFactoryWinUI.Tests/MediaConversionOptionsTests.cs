using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaConversionOptionsTests
{
    [Theory]
    [InlineData(MediaConversionOptions.TargetSizeKey, "-1")]
    [InlineData(MediaConversionOptions.FrameRateKey, "NaN")]
    [InlineData(MediaConversionOptions.FrameRateKey, "25,5")]
    [InlineData(MediaConversionOptions.MaxWidthKey, "0")]
    [InlineData(MediaConversionOptions.ImageQualityKey, "101")]
    [InlineData(MediaConversionOptions.VideoBitrateKey, "120k; -i extra")]
    [InlineData(MediaConversionOptions.ChannelsKey, "3")]
    public void Parse_RejectsInvalidOrUntrustedValues(string key, string value) =>
        Assert.Throws<InvalidDataException>(() => MediaConversionOptions.Parse(new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void Apply_ReplacesOnlyMediaChoicesAndUsesInvariantNumbers()
    {
        var stored = new Dictionary<string, string>
        {
            [MediaConversionOptions.VideoBitrateKey] = "1200", ["audioTrackPolicy"] = "compatible"
        };
        var options = new MediaConversionOptions { MaxWidth = 1920, FrameRate = 23.976, AudioBitrateKbps = 128 };
        options.ApplyTo(stored);
        Assert.False(stored.ContainsKey(MediaConversionOptions.VideoBitrateKey));
        Assert.Equal("23.976", stored[MediaConversionOptions.FrameRateKey]);
        Assert.Equal("compatible", stored["audioTrackPolicy"]);
        Assert.Equal(options, MediaConversionOptions.Parse(stored));
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720, 1280, 720)]
    [InlineData(1080, 1920, 1280, 720, 405, 720)]
    [InlineData(640, 360, 1920, 1080, 640, 360)]
    public void Dimensions_PreserveRatioAndNeverEnlarge(int width, int height, int maximumWidth, int maximumHeight,
        int expectedWidth, int expectedHeight)
    {
        var options = new MediaConversionOptions { MaxWidth = maximumWidth, MaxHeight = maximumHeight };
        Assert.Equal((expectedWidth, expectedHeight), options.ResolveDimensions(width, height));
    }

    [Fact]
    public void VideoDimensions_StayWithinLimitsAndMeetEvenSizeRequirement()
    {
        var result = new MediaConversionOptions { MaxHeight = 719 }.ResolveDimensions(1080, 1920, video: true);
        Assert.Equal((404, 718), result);
    }

    [Theory]
    [InlineData(MediaKind.Image, "png", "balanced", MediaConversionOptions.TargetSizeKey)]
    [InlineData(MediaKind.Image, "jpg", "lossless", MediaConversionOptions.ImageQualityKey)]
    [InlineData(MediaKind.Audio, "wav", "balanced", MediaConversionOptions.AudioBitrateKey)]
    [InlineData(MediaKind.Audio, "mp3", "balanced", MediaConversionOptions.MaxWidthKey)]
    public void Validation_RejectsUnsupportedOutputChoices(MediaKind kind, string target, string mode, string key)
    {
        var task = new MediaTask { Kind = kind, TargetFormatId = target, ConversionMode = mode };
        task.Options[key] = "100";
        Assert.Throws<NotSupportedException>(() => MediaConversionOptions.FromTask(task).Validate(task));
    }

    [Fact]
    public void SizeLimit_RejectsFixedVideoBitrate()
    {
        var task = new MediaTask { Kind = MediaKind.Video, TargetFormatId = "mp4" };
        Assert.Throws<InvalidDataException>(() => new MediaConversionOptions
        { TargetSizeBytes = 1000000, VideoBitrateKbps = 1200 }.Validate(task));
    }

    [Fact]
    public void InitialSizeBudget_IncludesEveryRetainedAudioTrack()
    {
        var task = new MediaTask
        {
            TargetFormatId = "mkv", Probe = new MediaProbeResult
            {
                Kind = MediaKind.Video, Duration = TimeSpan.FromSeconds(10),
                AudioTracks = [new() { Codec = "aac" }, new() { Codec = "aac" }]
            }
        };
        var budget = MediaSizeBudget.Initial(task, new() { TargetSizeBytes = 1000000 });
        Assert.Equal(376, budget.VideoBitrateKbps);
        Assert.Equal(192, budget.AudioBitrateKbps);
        var reduced = MediaSizeBudget.Reduce(task, budget, 1100000);
        Assert.True(reduced.VideoBitrateKbps < budget.VideoBitrateKbps);
        Assert.Equal(budget.AudioBitrateKbps, reduced.AudioBitrateKbps);
    }

    [Fact]
    public void CompleteDuration_DoesNotAcceptTruncatedLongMedia()
    {
        Assert.True(MediaSizeBudget.HasCompleteDuration(TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(24)));
        Assert.False(MediaSizeBudget.HasCompleteDuration(TimeSpan.FromHours(1), TimeSpan.FromHours(1) - TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("mp4", "libx264")]
    [InlineData("hevc", "libx265")]
    [InlineData("vp9", "libvpx-vp9")]
    [InlineData("av1", "libaom-av1")]
    public void StrictSize_UsesCpuRateControlWithoutTruncation(string target, string encoder)
    {
        var task = new MediaTask
        {
            TargetFormatId = target, InputPath = "input.mkv",
            Probe = new MediaProbeResult { Kind = MediaKind.Video, Duration = TimeSpan.FromSeconds(10), Width = 1920, Height = 1080 }
        };
        task.Options[MediaConversionOptions.TargetSizeKey] = "1000000";
        var arguments = new PresetService().BuildArguments(task, "output.mp4", EncoderBackend.Nvidia, true);
        Assert.Contains(encoder, arguments);
        Assert.Contains("-b:v", arguments);
        Assert.DoesNotContain("-crf", arguments);
        Assert.DoesNotContain("-hwaccel", arguments);
        Assert.DoesNotContain("-fs", arguments);
    }

    [Fact]
    public void CustomTransforms_UseOneFilterChainAndSoftwareDecodeWithHardwareEncoder()
    {
        var task = new MediaTask
        {
            TargetFormatId = "mp4", InputPath = "input.mkv",
            Probe = new MediaProbeResult { Kind = MediaKind.Video, Width = 1920, Height = 1080 }
        };
        new MediaConversionOptions { MaxWidth = 1280, MaxHeight = 720, FrameRate = 25, VideoBitrateKbps = 1200 }.ApplyTo(task.Options);
        var arguments = new PresetService().BuildArguments(task, "output.mp4", EncoderBackend.Nvidia, true).ToList();
        Assert.Contains("h264_nvenc", arguments);
        Assert.DoesNotContain("-hwaccel", arguments);
        Assert.DoesNotContain("-cq", arguments);
        Assert.Equal(1, arguments.Count(value => value == "-vf"));
        Assert.Equal("scale=w='min(iw,1280)':h='min(ih,720)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,fps=25",
            arguments[arguments.IndexOf("-vf") + 1]);
    }
}
