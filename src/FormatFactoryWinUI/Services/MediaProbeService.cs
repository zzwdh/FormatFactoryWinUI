using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class MediaProbeService
{
    public async Task<MediaProbeResult> ProbeAsync(string ffprobePath, string inputPath, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", inputPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var jsonTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var json = await jsonTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "FFprobe 无法读取该文件。" : error.Trim());
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            throw new InvalidDataException("文件中没有可识别的媒体流。M4S 文件可能缺少初始化信息。" );
        }

        var extension = Path.GetExtension(inputPath);
        var isImageExtension = FormatCatalog.ImageFormats
            .Select(FormatCatalog.ExtensionFor)
            .Append(".jpeg")
            .Contains(extension, StringComparer.OrdinalIgnoreCase);

        var hasVideo = false;
        var hasAudio = false;
        var width = 0;
        var height = 0;
        var videoCodec = string.Empty;
        var pixelFormat = string.Empty;
        var colorTransfer = string.Empty;
        var colorPrimaries = string.Empty;
        var colorSpace = string.Empty;
        var colorRange = string.Empty;
        var isHdr = false;
        var isAnimated = false;
        var audioTracks = new List<AudioTrackInfo>();
        var subtitleCodecs = new List<string>();
        var audioRelativeIndex = 0;
        var bestDuration = TimeSpan.Zero;

        foreach (var stream in streams.EnumerateArray())
        {
            var codecType = GetString(stream, "codec_type");
            if (codecType == "video")
            {
                hasVideo = true;
                width = Math.Max(width, GetInt(stream, "width"));
                height = Math.Max(height, GetInt(stream, "height"));
                videoCodec = GetString(stream, "codec_name");
                pixelFormat = GetString(stream, "pix_fmt");
                var transfer = GetString(stream, "color_transfer");
                colorTransfer = FirstKnown(colorTransfer, transfer);
                colorPrimaries = FirstKnown(colorPrimaries, GetString(stream, "color_primaries"));
                colorSpace = FirstKnown(colorSpace, GetString(stream, "color_space"));
                colorRange = FirstKnown(colorRange, GetString(stream, "color_range"));
                isHdr |= transfer is "smpte2084" or "arib-std-b67" || pixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) || pixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase);
                var frameCount = GetLong(stream, "nb_frames");
                isAnimated |= frameCount > 1;
                bestDuration = Max(bestDuration, ParseDuration(GetString(stream, "duration")));
            }
            else if (codecType == "audio")
            {
                hasAudio = true;
                var tags = stream.TryGetProperty("tags", out var tagsElement) ? tagsElement : default;
                audioTracks.Add(new AudioTrackInfo
                {
                    StreamIndex = GetInt(stream, "index"),
                    RelativeIndex = audioRelativeIndex++,
                    Codec = GetString(stream, "codec_name"),
                    Channels = GetInt(stream, "channels"),
                    Language = tags.ValueKind == JsonValueKind.Object ? GetString(tags, "language", "未知") : "未知",
                    Title = tags.ValueKind == JsonValueKind.Object ? GetString(tags, "title") : string.Empty
                });
                bestDuration = Max(bestDuration, ParseDuration(GetString(stream, "duration")));
            }
            else if (codecType == "subtitle")
            {
                subtitleCodecs.Add(GetString(stream, "codec_name"));
            }
        }

        if (root.TryGetProperty("format", out var format))
        {
            bestDuration = Max(bestDuration, ParseDuration(GetString(format, "duration")));
        }

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) && bestDuration > TimeSpan.FromMilliseconds(100))
        {
            isAnimated = true;
        }

        var kind = isImageExtension ? MediaKind.Image : hasVideo ? MediaKind.Video : hasAudio ? MediaKind.Audio : throw new InvalidDataException("无法确定媒体类型。" );
        return new MediaProbeResult
        {
            Kind = kind,
            Duration = bestDuration,
            Width = width,
            Height = height,
            IsHdr = isHdr,
            IsAnimated = isAnimated,
            VideoCodec = videoCodec,
            PixelFormat = pixelFormat,
            ColorTransfer = colorTransfer,
            ColorPrimaries = colorPrimaries,
            ColorSpace = colorSpace,
            ColorRange = colorRange,
            AudioTracks = audioTracks,
            SubtitleCodecs = subtitleCodecs
        };
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "") =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static long GetLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && long.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static TimeSpan ParseDuration(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static string FirstKnown(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) && candidate is not ("" or "unknown" or "unspecified" or "reserved") ? candidate : current;

}
