using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class RemuxCompatibilityTests
{
    [Theory]
    [InlineData("adpcm_ima_wav", 0, 44100)]
    [InlineData("adpcm_ima_wav", 3, 44100)]
    [InlineData("adpcm_ima_wav", 2, 0)]
    [InlineData("adpcm_ima_qt", 2, 44100)]
    public void WavCopy_RejectsUnverifiedAdpcmStreams(string codec, int channels, int sampleRate)
    {
        var stream = new RemuxStream("audio", codec, Channels: channels, SampleRate: sampleRate);
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("WAV"), [stream]));
    }

    [Fact]
    public void Compatibility_ChecksEveryTrackAndNeverDropsUnsupportedSubtitles()
    {
        RemuxStream[] streams = [new("video", "h264"), new("audio", "aac"), new("subtitle", "subrip")];
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("MP4"), streams));
        Assert.Equal(3, RemuxCompatibilityService.Validate(Task("MKV"), streams).Count);
    }

    [Theory]
    [InlineData("MP4", "h264", true)]
    [InlineData("MP4", "vp9", true)]
    [InlineData("MOV", "vp9", false)]
    [InlineData("AVI", "vp9", false)]
    [InlineData("TS", "vp9", false)]
    [InlineData("FLV", "vp9", false)]
    [InlineData("MOV", "h264", true)]
    [InlineData("MKV", "ffv1", true)]
    [InlineData("WEBM", "av1", true)]
    [InlineData("MP4", "ffv1", false)]
    [InlineData("WEBM", "h264", false)]
    public void ContainerTargets_RequireCompatibleStreamCopy(string target, string codec, bool compatible)
    {
        RemuxStream[] streams = [new("video", codec)];
        if (compatible) Assert.Single(RemuxCompatibilityService.Validate(Task(target), streams));
        else Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task(target), streams));
    }

    [Theory]
    [InlineData("H264")]
    [InlineData("HEVC")]
    [InlineData("VP9")]
    [InlineData("AV1")]
    [InlineData("FRAMES")]
    public void CodecAndFrameTargets_RetainTheExistingLosslessRouteGuard(string target)
    {
        var exception = Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.ValidateTarget(Task(target)));
        Assert.Equal(LocalizationService.T("ConversionUiLosslessRouteUnsupported"), exception.Message);
    }

    [Theory]
    [InlineData("GIF")]
    [InlineData("JPG")]
    [InlineData("FRAMES")]
    public void LosslessMediaMode_CannotSilentlyBecomeLossyOrDiscardFrames(string target)
    {
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.ValidateTarget(Task(target)));
        Assert.Throws<NotSupportedException>(() => new PresetService().BuildArguments(Task(target), "output.tmp", EncoderBackend.Cpu));
    }

    [Fact]
    public void AudioExtraction_OnlyChecksAndCopiesTheSelectedTrack()
    {
        var task = Task("M4A");
        task.SelectedAudioTrack = 1;
        RemuxStream[] streams =
        [new("video", "ffv1"), new("audio", "vorbis"), new("audio", "aac"), new("subtitle", "ass")];

        Assert.Equal(streams[2], Assert.Single(RemuxCompatibilityService.Validate(task, streams)));
        task.SelectedAudioTrack = 0;
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(task, streams));
        task.SelectedAudioTrack = 3;
        Assert.Throws<InvalidDataException>(() => RemuxCompatibilityService.Validate(task, streams));
    }

    [Fact]
    public void UnknownOrUnsupportedStreams_AreRejectedBeforeConversion()
    {
        Assert.Throws<InvalidDataException>(() => RemuxCompatibilityService.Validate(Task("MKV"), []));
        Assert.Throws<InvalidDataException>(() => RemuxCompatibilityService.Validate(Task("MKV"), [new("video", "")]));
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("MKV"),
            [new("video", "h264"), new("data", "bin_data")]));
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("FLV"),
            [new("video", "h264"), new("audio", "aac"), new("audio", "aac")]));
    }

    [Fact]
    public void OutputVerification_RejectsLostAddedOrAlteredStreams()
    {
        RemuxStream video = new("video", "h264", 1920, 1080);
        RemuxStream audio = new("audio", "aac", SampleRate: 48000, Channels: 2);
        RemuxStream[] source = [video, audio];

        Assert.True(RemuxCompatibilityService.MatchesOutput(source, [audio, video]));
        Assert.False(RemuxCompatibilityService.MatchesOutput(source, [video]));
        Assert.False(RemuxCompatibilityService.MatchesOutput(source, [video, audio, audio]));
        Assert.False(RemuxCompatibilityService.MatchesOutput(source, [video with { Width = 1280 }, audio]));
        Assert.False(RemuxCompatibilityService.MatchesOutput(source, [video, audio with { Codec = "mp3" }]));
        Assert.False(RemuxCompatibilityService.MatchesOutput(source, [video, audio with { SampleRate = 44100 }]));
        Assert.False(RemuxCompatibilityService.MatchesOutput(source, [video, audio with { Channels = 1 }]));
    }

    [Fact]
    public void Parser_RetainsAttachmentsAndDataBeyondTheUiProbeModel()
    {
        var streams = RemuxCompatibilityService.ParseStreams("""
            {"streams":[
                {"codec_type":"video","codec_name":"h264","width":640,"height":480},
                {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2},
                {"codec_type":"attachment"},
                {"codec_type":"data","codec_name":"bin_data"}
            ]}
            """);
        Assert.Equal(4, streams.Count);
        Assert.Equal(48000, streams[1].SampleRate);
        Assert.Equal("attachment", streams[2].Type);
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("MKV"), streams));
        Assert.Equal(3, RemuxCompatibilityService.Validate(Task("MKV"), streams.Take(3).ToArray()).Count);
    }

    [Fact]
    public void Vp9ToMp4_PreservesAllCompatibleTracksAndStillRejectsUnsupportedTracks()
    {
        RemuxStream[] streams = [new("video", "vp9", 64, 64),
            new("audio", "aac", SampleRate: 48000, Channels: 2),
            new("audio", "aac", SampleRate: 48000, Channels: 2)];
        Assert.Equal(streams, RemuxCompatibilityService.Validate(Task("MP4"), streams));
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("MP4"),
            [streams[0], new("audio", "opus", SampleRate: 48000, Channels: 2)]));
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("MP4"),
            [.. streams, new("subtitle", "subrip")]));
        Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(Task("MOV"), streams));
    }
    private static MediaTask Task(string target) => new()
    {
        InputPath = "input.mkv", Kind = MediaKind.Video, TargetFormat = target, ConversionMode = "lossless"
    };
}
