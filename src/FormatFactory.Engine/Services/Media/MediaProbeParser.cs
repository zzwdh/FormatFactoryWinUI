using System.Globalization;
using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class MediaProbeParser
{
    internal static MediaProbeResult Parse(string json, string inputPath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
        {
            throw new InvalidDataException(LocalizationService.T("GeneralServiceNoRecognizableMediaStreams"));
        }

        var isImage = FormatCatalog.FindByExtension(Path.GetExtension(inputPath))?.ContentKind == ContentKind.Image;
        var probe = new MediaProbeResult();
        var hasVideo = false;
        var audioTracks = new List<AudioTrackInfo>();
        var subtitleCodecs = new List<string>();
        var streamOrdinal = -1;
        foreach (var stream in streams.EnumerateArray())
        {
            streamOrdinal++;
            switch (GetString(stream, "codec_type"))
            {
                case "video":
                    // Keep all video metadata on the same first real stream used by -map 0:V:0.
                    if (hasVideo || IsAttachedPicture(stream))
                    {
                        break;
                    }
                    hasVideo = true;
                    probe.Width = GetInt(stream, "width");
                    probe.Height = GetInt(stream, "height");
                    probe.VideoCodec = GetString(stream, "codec_name");
                    probe.VideoCodecTag = GetString(stream, "codec_tag_string");
                    probe.VideoStreamIndex = GetInt(stream, "index", streamOrdinal);
                    probe.PixelFormat = GetString(stream, "pix_fmt");
                    probe.ColorTransfer = GetString(stream, "color_transfer");
                    probe.ColorPrimaries = GetString(stream, "color_primaries");
                    probe.ColorSpace = GetString(stream, "color_space");
                    probe.ColorRange = GetString(stream, "color_range");
                    // Bit depth alone does not imply HDR: 10-bit SDR is valid.
                    probe.IsHdr = probe.ColorTransfer is "smpte2084" or "arib-std-b67";
                    probe.IsAnimated = GetLong(stream, "nb_frames") > 1;
                    probe.Duration = Max(probe.Duration, ParseDuration(GetString(stream, "duration")));
                    probe.Bitrate = Math.Max(probe.Bitrate, GetUInt(stream, "bit_rate"));
                    break;
                case "audio":
                    var tags = stream.TryGetProperty("tags", out var tagsElement) ? tagsElement : default;
                    audioTracks.Add(new AudioTrackInfo
                    {
                        RelativeIndex = audioTracks.Count,
                        Codec = GetString(stream, "codec_name"),
                        CodecTag = GetString(stream, "codec_tag_string"),
                        StreamIndex = GetInt(stream, "index", streamOrdinal),
                        Channels = GetInt(stream, "channels"),
                        ChannelLayout = GetString(stream, "channel_layout"),
                        Language = GetString(tags, "language", LocalizationService.T("Unknown")),
                        Title = GetString(tags, "title")
                    });
                    probe.Duration = Max(probe.Duration, ParseDuration(GetString(stream, "duration")));
                    probe.Bitrate = Math.Max(probe.Bitrate, GetUInt(stream, "bit_rate"));
                    break;
                case "subtitle":
                    subtitleCodecs.Add(GetString(stream, "codec_name"));
                    break;
            }
        }

        if (root.TryGetProperty("format", out var format))
        {
            probe.FormatName = GetString(format, "format_name");
            probe.Duration = Max(probe.Duration, ParseDuration(GetString(format, "duration")));
            var bitrate = GetUInt(format, "bit_rate");
            if (bitrate > 0) probe.Bitrate = bitrate;
        }
        probe.Kind = isImage && hasVideo ? MediaKind.Image
            : hasVideo ? MediaKind.Video
            : audioTracks.Count > 0 ? MediaKind.Audio
            : throw new InvalidDataException(LocalizationService.T("GeneralServiceCannotDetermineMediaType"));
        probe.AudioTracks = audioTracks;
        probe.SubtitleCodecs = subtitleCodecs;
        return probe;
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        (GetInt(disposition, "attached_pic") != 0 || GetInt(disposition, "timed_thumbnails") != 0);

    private static string GetString(JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int GetInt(JsonElement element, string name, int fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            int.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static uint GetUInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        uint.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static long GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        long.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static TimeSpan ParseDuration(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}
