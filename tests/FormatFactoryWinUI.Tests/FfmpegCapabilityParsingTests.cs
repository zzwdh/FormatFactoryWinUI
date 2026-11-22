using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class FfmpegCapabilityParsingTests
{
    [Fact]
    public void DecoderList_RetainsNamesAndUnderlyingCodecAliases()
    {
        var parsed = FfmpegCapabilityService.ParseCodecs("""
            Decoders:
             V..... = Video
             A....D mp3float Synthetic floating point MPEG decoder (codec mp3)
             VFS..D h264 Synthetic AVC decoder
             V..... h264_custom Synthetic custom AVC decoder (codec h264)
             S..... subrip Synthetic text decoder
            """);
        var snapshot = new FfmpegCapabilitySnapshot(new HashSet<string>())
        { Decoders = parsed.Names, DecoderCodecIds = parsed.CodecIds };
        Assert.Equal(4, snapshot.Decoders.Count);
        Assert.True(snapshot.HasDecoder("mp3"));
        Assert.True(snapshot.HasDecoder("mp3float"));
        Assert.True(snapshot.HasDecoder("H264"));
        Assert.False(snapshot.HasDecoder("mp4"));
        Assert.DoesNotContain("=", snapshot.Decoders);
    }

    [Fact]
    public void FormatList_RetainsCommaSeparatedMuxerAliasesAndIgnoresDemuxOnlyRows()
    {
        var muxers = FfmpegCapabilityService.ParseMuxers(" E mov,mp4 Synthetic containers\n D input_only Synthetic reader\n DE matroska Synthetic container");
        Assert.Contains("mov", muxers);
        Assert.Contains("mp4", muxers);
        Assert.Contains("matroska", muxers);
        Assert.DoesNotContain("input_only", muxers);
    }

    [Fact]
    public void ActualProbe_PreservesUnknownCodecTagsAndGlobalStreamIndices()
    {
        var probe = MediaProbeParser.Parse("""
            {"streams":[
              {"index":1,"codec_type":"video","codec_name":"png","disposition":{"attached_pic":1}},
              {"index":5,"codec_type":"video","codec_tag_string":"synthetic_fourcc","width":16,"height":16},
              {"index":8,"codec_type":"audio","codec_name":"mp3","codec_tag_string":"synthetic_audio","channels":2}
            ]}
            """, "synthetic.mp4");
        Assert.Empty(probe.VideoCodec);
        Assert.Equal("synthetic_fourcc", probe.VideoCodecTag);
        Assert.Equal(5, probe.VideoStreamIndex);
        Assert.Equal("synthetic_audio", Assert.Single(probe.AudioTracks).CodecTag);
        Assert.Equal(8, Assert.Single(probe.AudioTracks).StreamIndex);
    }

    [Theory]
    [InlineData("MP3", "libmp3lame")]
    [InlineData("MP4", "libx264")]
    [InlineData("AV1", "libaom-av1")]
    [InlineData("PNG", "png")]
    public void SoftwareOutputEncoder_IsVerifiedAlongsideMuxer(string target, string expectedEncoder)
    {
        var snapshot = new FfmpegCapabilitySnapshot(new HashSet<string> { "mp3", "mp4", "webm", "image2" });
        var missing = FfmpegCapabilityService.GetMissingOutputComponents(snapshot,
            new MediaTask { TargetFormat = target, Kind = MediaKind.Video }, new MediaProbeResult { Kind = MediaKind.Video });
        Assert.Contains("encoder: " + expectedEncoder, missing);
        Assert.DoesNotContain(missing, value => value.StartsWith("muxer:", StringComparison.Ordinal));
    }
}
