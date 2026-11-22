using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaProbeParserTests
{
    [Fact]
    public void AudioWithAlbumArt_RemainsAudio()
    {
        var result = MediaProbeParser.Parse("""
            {"streams":[
              {"codec_type":"video","codec_name":"mjpeg","width":3000,"height":3000,"disposition":{"attached_pic":1}},
              {"codec_type":"audio","codec_name":"mp3","channels":2,"duration":"12.5"}
            ]}
            """, "album.mp3");

        Assert.Equal(MediaKind.Audio, result.Kind);
        Assert.Empty(result.VideoCodec);
        Assert.Equal(0, result.Width);
        Assert.Equal(TimeSpan.FromSeconds(12.5), result.Duration);
        Assert.Single(result.AudioTracks);
    }

    [Fact]
    public void MultipleVideos_UseMetadataFromFirstNonArtworkStream()
    {
        var result = MediaProbeParser.Parse("""
            {"streams":[
              {"codec_type":"video","codec_name":"png","width":3000,"height":3000,"disposition":{"attached_pic":1}},
              {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"pix_fmt":"yuv420p","color_transfer":"bt709","nb_frames":"30"},
              {"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"pix_fmt":"yuv420p10le","color_transfer":"smpte2084"}
            ]}
            """, "movie.mkv");

        Assert.Equal(MediaKind.Video, result.Kind);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("yuv420p", result.PixelFormat);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal("bt709", result.ColorTransfer);
        Assert.False(result.IsHdr);
        Assert.True(result.IsAnimated);
    }

    [Theory]
    [InlineData("yuv420p10le", "bt709", false)]
    [InlineData("p010le", "", false)]
    [InlineData("yuv420p10le", "smpte2084", true)]
    [InlineData("yuv420p10le", "arib-std-b67", true)]
    public void HdrDependsOnTransferFunction(string pixelFormat, string transfer, bool isHdr)
    {
        var result = MediaProbeParser.Parse($$"""
            {"streams":[{"codec_type":"video","pix_fmt":"{{pixelFormat}}","color_transfer":"{{transfer}}"}]}
            """, "movie.mkv");

        Assert.Equal(isHdr, result.IsHdr);
    }

    [Theory]
    [InlineData("picture.tif")]
    [InlineData("picture.jpeg")]
    [InlineData("picture.tiff")]
    public void ImageAliases_UseSharedFormatCatalog(string path)
    {
        var result = MediaProbeParser.Parse("""{"streams":[{"codec_type":"video","codec_name":"tiff"}]}""", path);
        Assert.Equal(MediaKind.Image, result.Kind);
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("1e300")]
    [InlineData("-1")]
    public void InvalidDuration_DoesNotCrashImport(string duration)
    {
        var result = MediaProbeParser.Parse($$"""
            {"streams":[{"codec_type":"audio","duration":"{{duration}}"}]}
            """, "audio.mp3");
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    [Fact]
    public void ContainerBitrate_IsUsedForWholeFile()
    {
        var result = MediaProbeParser.Parse("""
            {"streams":[{"codec_type":"video","bit_rate":"1000000"},{"codec_type":"audio","bit_rate":"128000"}],
             "format":{"bit_rate":"1130000","duration":"2.5"}}
            """, "movie.mp4");
        Assert.Equal(1130000u, result.Bitrate);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.Duration);
    }
}
