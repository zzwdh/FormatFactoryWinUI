using System.Text.Json;

namespace FormatFactoryWinUI.Models;

/// <summary>
/// Single source of truth for import extensions and conversion targets.
/// Stable lower-case ids are persisted; display names preserve the existing UI API.
/// </summary>
public static class FormatCatalog
{
    private const string ResourceName = "FormatFactoryWinUI.Models.formats.json";
    private static readonly IReadOnlyList<FormatDescriptor> Descriptors = LoadEmbedded();
    private static readonly IReadOnlyDictionary<string, FormatDescriptor> ById = Descriptors
        .ToDictionary(format => format.Id, StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, FormatDescriptor> ByExtension = Descriptors
        .Where(format => format.CanImport)
        .SelectMany(format => format.Extensions.Select(extension => (extension, format)))
        .GroupBy(item => item.extension, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().format, StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, FormatDescriptor> ByDisplayName = Descriptors
        .GroupBy(format => format.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    public static readonly string[] VideoFormats = ExportNames(ContentKind.Video);
    public static readonly string[] AudioFormats = ExportNames(ContentKind.Audio);
    public static readonly string[] ImageFormats = ExportNames(ContentKind.Image, includeVirtual: false);
    public static readonly string[] DocumentFormats = ExportNames(ContentKind.Document);
    // DOCX export currently belongs to the PDF text/OCR route. A missing source
    // still returns the full document target list for automation rule editors.
    private static readonly string[] NonPdfDocumentTargets = DocumentFormats.Where(format => format != "DOCX").ToArray();
    public static readonly string[] EbookFormats = ExportNames(ContentKind.Ebook);
    public static readonly string[] DiscFormats = ExportNames(ContentKind.Disc);
    public static readonly string[] ConversionModes = ["fast", "balanced", "quality", "lossless"];

    public static readonly HashSet<string> SupportedExtensions = new(
        Descriptors.Where(format => format.CanImport).SelectMany(format => format.Extensions),
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<FormatDescriptor> All => Descriptors;

    public static FormatDescriptor? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return ById.GetValueOrDefault(FormatDescriptor.NormalizeId(id)) ??
               ByDisplayName.GetValueOrDefault(id.Trim());
    }

    public static FormatDescriptor? FindByExtension(string? extensionOrPath)
    {
        if (string.IsNullOrWhiteSpace(extensionOrPath))
        {
            return null;
        }

        var value = extensionOrPath.Trim();
        var extension = value.StartsWith('.', StringComparison.Ordinal)
            ? value
            : Path.GetExtension(value);
        return string.IsNullOrWhiteSpace(extension)
            ? null
            : ByExtension.GetValueOrDefault(FormatDescriptor.NormalizeExtension(extension));
    }

    public static IReadOnlyList<string> TargetsFor(MediaKind kind, bool hasAudio = true) => kind switch
    {
        MediaKind.Video => hasAudio ? [.. VideoFormats, .. AudioFormats] : VideoFormats,
        MediaKind.Audio => AudioFormats,
        MediaKind.Image => [.. ImageFormats, .. NonPdfDocumentTargets, .. EbookFormats],
        MediaKind.Document => [.. DocumentFormats, .. EbookFormats],
        MediaKind.Ebook => EbookFormats,
        MediaKind.Disc => DiscFormats,
        _ => []
    };

    public static IReadOnlyList<string> TargetsFor(ContentKind kind, string? sourceFormatId = null) => kind switch
    {
        ContentKind.Video => [.. VideoFormats, .. AudioFormats],
        ContentKind.Audio => AudioFormats,
        ContentKind.Image => [.. ImageFormats, .. NonPdfDocumentTargets, .. EbookFormats],
        ContentKind.Document when !string.IsNullOrWhiteSpace(sourceFormatId) && NormalizeFormatId(sourceFormatId) != "pdf"
            => [.. NonPdfDocumentTargets, .. EbookFormats],
        ContentKind.Document => [.. DocumentFormats, .. EbookFormats],
        ContentKind.Ebook when NormalizeFormatId(sourceFormatId) == "epub" => ["MOBI", "AZW3"],
        ContentKind.Ebook => EbookFormats,
        ContentKind.Disc => DiscTargets(sourceFormatId),
        _ => []
    };

    public static string DefaultTarget(MediaKind kind) => kind switch
    {
        MediaKind.Video => "MP4",
        MediaKind.Audio => "MP3",
        MediaKind.Image => "PNG",
        MediaKind.Document => "PDF",
        MediaKind.Ebook => "EPUB",
        MediaKind.Disc => "ISO",
        _ => "MP4"
    };

    public static string DefaultTarget(ContentKind kind) => DefaultTarget(kind.ToMediaKind());
    public static string DefaultConversionMode => "balanced";

    public static string NormalizeFormatId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var descriptor = FindById(value) ?? FindByExtension(value);
        return descriptor?.Id ?? FormatDescriptor.NormalizeId(value.TrimStart('.'));
    }

    public static string NormalizeEngineId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "ffmpeg" : FormatDescriptor.NormalizeId(value);

    public static string ExtensionFor(string format) =>
        FindById(format)?.PrimaryExtension ?? FormatDescriptor.NormalizeExtension(format);

    public static bool IsAudioFormat(string format) => IsExportOfKind(format, ContentKind.Audio);
    public static bool IsImageFormat(string format) => IsExportOfKind(format, ContentKind.Image);
    public static bool IsFrameSequence(string format) => NormalizeFormatId(format) == "frames";
    public static bool IsVideoCodec(string format) =>
        NormalizeFormatId(format) is "h264" or "hevc" or "vp9" or "av1";

    public static string ContainerForVideoTarget(string format) => NormalizeFormatId(format) switch
    {
        "h264" or "hevc" => "MP4",
        "vp9" or "av1" => "WEBM",
        var id => FindById(id)?.DisplayName ?? format.Trim().ToUpperInvariant()
    };

    public static string? VideoCodecForTarget(string format) => NormalizeFormatId(format) switch
    {
        "h264" => "h264",
        "hevc" => "hevc",
        "vp9" => "vp9",
        "av1" => "av1",
        _ => null
    };

    public static bool IsFfmpegFormat(string format)
    {
        var descriptor = FindById(format);
        return descriptor is not null &&
               descriptor.ContentKind is ContentKind.Video or ContentKind.Audio or ContentKind.Image &&
               string.Equals(descriptor.DefaultEngineId, "ffmpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExportOfKind(string format, ContentKind kind)
    {
        var descriptor = FindById(format);
        return descriptor is { CanExport: true } && descriptor.ContentKind == kind;
    }

    private static IReadOnlyList<string> DiscTargets(string? sourceFormatId) =>
        NormalizeFormatId(sourceFormatId) switch
        {
            "bluray-disc" => ["MKV"],
            "audio-cd" => AudioFormats,
            "dvd-disc" or "data-disc" or "optical-disc" => ["ISO", "CSO"],
            "iso" => ["CSO"],
            "cso" => ["ISO"],
            _ => DiscFormats
        };

    private static string[] ExportNames(ContentKind kind, bool includeVirtual = true) => Descriptors
        .Where(format => format.ContentKind == kind &&
                         format.CanExport &&
                         (includeVirtual || !format.IsVirtual))
        .Select(format => format.DisplayName)
        .ToArray();

    private static IReadOnlyList<FormatDescriptor> LoadEmbedded()
    {
        using var stream = typeof(FormatCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded format catalog '{ResourceName}'.");
        return Load(stream);
    }

    internal static IReadOnlyList<FormatDescriptor> Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var entries = JsonSerializer.Deserialize<FormatEntry[]>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
        if (entries.Length == 0)
        {
            throw new InvalidDataException("The format catalog is empty.");
        }

        var result = entries.Select(ToDescriptor).ToArray();
        var duplicateId = result.GroupBy(format => format.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidDataException($"Duplicate format id '{duplicateId.Key}'.");
        }

        var duplicateExtension = result
            .Where(format => format.CanImport)
            .SelectMany(format => format.Extensions.Select(extension => (extension, format.Id)))
            .GroupBy(item => item.extension, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        if (duplicateExtension is not null)
        {
            throw new InvalidDataException($"Duplicate import extension '{duplicateExtension.Key}'.");
        }

        return Array.AsReadOnly(result);
    }

    private static FormatDescriptor ToDescriptor(FormatEntry entry)
    {
        if (!Enum.TryParse<ContentKind>(entry.ContentKind, true, out var kind))
        {
            throw new InvalidDataException($"Unknown content kind '{entry.ContentKind}' for '{entry.Id}'.");
        }

        var capabilities = FormatCapabilities.None;
        foreach (var value in entry.Capabilities ?? [])
        {
            if (!Enum.TryParse<FormatCapabilities>(value, true, out var capability))
            {
                throw new InvalidDataException($"Unknown capability '{value}' for '{entry.Id}'.");
            }
            capabilities |= capability;
        }

        return new FormatDescriptor(
            entry.Id ?? string.Empty,
            entry.DisplayName ?? string.Empty,
            kind,
            entry.PrimaryExtension ?? string.Empty,
            entry.Extensions ?? [],
            entry.MimeTypes ?? [],
            capabilities,
            entry.DefaultEngineId,
            entry.IsVirtual);
    }

    private sealed class FormatEntry
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? ContentKind { get; set; }
        public string? PrimaryExtension { get; set; }
        public string[]? Extensions { get; set; }
        public string[]? MimeTypes { get; set; }
        public string[]? Capabilities { get; set; }
        public string? DefaultEngineId { get; set; }
        public bool IsVirtual { get; set; }
    }
}
