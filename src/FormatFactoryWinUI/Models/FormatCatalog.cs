namespace FormatFactoryWinUI.Models;

public static class FormatCatalog
{
    public static readonly string[] VideoFormats = ["MP4", "MKV", "AVI", "FLV", "MOV"];
    public static readonly string[] AudioFormats = ["MP3", "WMA", "M4A", "FLAC", "WAV", "AAC"];
    public static readonly string[] ImageFormats = ["JPG", "PNG", "ICO", "BMP", "WEBP", "AVIF"];
    public static readonly string[] ConversionModes = ["快速", "平衡", "高质量", "无损"];

    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".flv", ".mov", ".m4s",
        ".mp3", ".wma", ".m4a", ".flac", ".wav", ".aac",
        ".jpg", ".jpeg", ".png", ".ico", ".bmp", ".webp", ".avif"
    };

    public static IReadOnlyList<string> TargetsFor(MediaKind kind) => kind switch
    {
        MediaKind.Video => [.. VideoFormats, .. AudioFormats],
        MediaKind.Audio => AudioFormats,
        _ => ImageFormats
    };

    public static string DefaultTarget(MediaKind kind) => kind switch
    {
        MediaKind.Video => "MP4",
        MediaKind.Audio => "MP3",
        _ => "PNG"
    };

    public static string DefaultConversionMode => "平衡";

    public static string ExtensionFor(string format) => format.ToLowerInvariant() switch
    {
        "jpg" => ".jpg",
        "webp" => ".webp",
        _ => "." + format.ToLowerInvariant()
    };
}
