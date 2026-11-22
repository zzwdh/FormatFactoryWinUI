using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class PresetServiceTests
{
    [Theory]
    [InlineData("balanced", -1, "0:a:0")]
    [InlineData("lossless", -1, "0:a:0")]
    [InlineData("balanced", int.MinValue, "0:a:0")]
    [InlineData("balanced", 1, "0:a:1")]
    [InlineData("lossless", 1, "0:a:1")]
    public void VideoAudioExtraction_UsesNonnegativeStreamSpecifier(string mode, int selection, string expected)
    {
        var task = new MediaTask
        {
            InputPath = "input.mp4", Kind = MediaKind.Video, TargetFormat = "MP3",
            SelectedAudioTrack = selection, ConversionMode = mode
        };

        var arguments = new PresetService().BuildArguments(task, "output.mp3", EncoderBackend.Cpu);

        Assert.Contains(expected, arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("0:a:-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MP4")]
    [InlineData("GIF")]
    [InlineData("PNG")]
    public void VideoConversion_ExcludesAlbumArt(string target)
    {
        var task = new MediaTask { InputPath = "input.mkv", Kind = MediaKind.Video, TargetFormat = target };
        var arguments = new PresetService().BuildArguments(task, "output.tmp", EncoderBackend.Cpu);
        Assert.Contains("0:V:0", arguments);
        Assert.DoesNotContain("0:v:0", arguments);
    }

    [Fact]
    public void HdrToFlv_OutputUsesSdrColorTags()
    {
        var task = new MediaTask
        {
            InputPath = "input.mkv", Kind = MediaKind.Video, TargetFormat = "FLV",
            Probe = new MediaProbeResult
            {
                Kind = MediaKind.Video, IsHdr = true, ColorTransfer = "smpte2084",
                ColorPrimaries = "bt2020", ColorSpace = "bt2020nc", ColorRange = "tv"
            }
        };
        var arguments = new PresetService().BuildArguments(task, "output.flv", EncoderBackend.Cpu).ToList();
        Assert.Contains(arguments, value => value.Contains("tonemap=hable", StringComparison.Ordinal));
        Assert.Equal("bt709", arguments[arguments.IndexOf("-color_trc") + 1]);
        Assert.Equal("bt709", arguments[arguments.IndexOf("-color_primaries") + 1]);
        Assert.Equal("bt709", arguments[arguments.IndexOf("-colorspace") + 1]);
        Assert.DoesNotContain("smpte2084", arguments);
    }

    [Theory]
    [InlineData("H264", true)]
    [InlineData("HEVC", true)]
    [InlineData("MP4", true)]
    [InlineData("VP9", false)]
    [InlineData("AV1", false)]
    public void ExplicitVideoCodecTargets_UseEligibleHardware(string target, bool hardware)
    {
        var task = new MediaTask { InputPath = "input.mkv", Kind = MediaKind.Video, TargetFormat = target };
        Assert.Equal(hardware, new PresetService().WillUseHardware(task));
    }
}
