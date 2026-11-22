using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Reads reflowable content from WordprocessingML without requiring Word.</summary>
public sealed class DocxDocumentReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public async Task<NormalizedDocument> ReadAsync(string docxPath, CancellationToken cancellationToken = default,
        string? fallbackTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docxPath);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(docxPath);
        if (!File.Exists(path)) throw FormatFailureDiagnostics.Attach(
            new FileNotFoundException(LocalizationService.T("ConversionUiDocxDocumentNotFound"), path),
            new(FormatFailureStage.DocxPackage, FormatFailureReason.Missing) { Source = "docx" });

        ValidateOpenXmlPackage(path, cancellationToken);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var main = archive.GetEntry("word/document.xml")
            ?? throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiDocxDocumentXmlMissing")),
                new(FormatFailureStage.DocxPackage, FormatFailureReason.MissingBody) { Source = "docx", MainDocumentPart = false });
        var xml = await LoadXmlAsync(main, cancellationToken);
        var body = xml.Root?.Element(W + "body")
            ?? throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiDocxBodyMissing")),
                new(FormatFailureStage.DocxPackage, FormatFailureReason.MissingBody) { Source = "docx", MainDocumentPart = true });

        var relationships = await ReadImageRelationshipsAsync(archive, cancellationToken);
        var (resources, resourceIds) = ReadResources(archive, relationships);
        var headings = await ReadHeadingStylesAsync(archive, cancellationToken);
        var numbering = await ReadNumberingAsync(archive, cancellationToken);
        var footnotes = await ReadFootnotesAsync(archive, cancellationToken);
        var blocks = ParseElements(body.Elements(), headings, numbering, footnotes, resourceIds);

        var core = archive.GetEntry("docProps/core.xml");
        var coreXml = core is null ? null : await LoadXmlAsync(core, cancellationToken);
        var title = coreXml?.Descendants(Dc + "title").FirstOrDefault()?.Value.Trim();
        title = string.IsNullOrWhiteSpace(title)
            ? blocks.OfType<NormalizedHeading>().FirstOrDefault()?.Text ?? fallbackTitle ?? Path.GetFileNameWithoutExtension(path)
            : title;
        var language = coreXml?.Descendants(Dc + "language").FirstOrDefault()?.Value.Trim();
        language = string.IsNullOrWhiteSpace(language)
            ? xml.Descendants(W + "lang").Select(item => (string?)item.Attribute(W + "val"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "zh-CN"
            : language;

        return new NormalizedDocument(
            title,
            language,
            [new NormalizedSection("section-1", blocks)],
            resources);
    }

    private static void ValidateOpenXmlPackage(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var package = WordprocessingDocument.Open(path, false, new OpenSettings { AutoSave = false });
            if (package.MainDocumentPart?.Document?.Body is null)
                throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiDocxMainBodyMissing")),
                    new(FormatFailureStage.DocxPackage, FormatFailureReason.MissingBody) { Source = "docx", MainDocumentPart = package.MainDocumentPart is not null });
            // Import supports documents written by modern Word. The SDK's
            // parameterless validator targets Office 2007 and rejects later
            // schema attributes such as w14:paraId even on readable content.
            var validationErrors = new OpenXmlValidator(FileFormatVersions.Microsoft365)
                .Validate(package, cancellationToken).Take(8).ToList();
            if (validationErrors.Count > 0)
            {
                var details = string.Join("；", validationErrors.Select(error => error.Description));
                throw FormatFailureDiagnostics.Attach(new InvalidDataException(LocalizationService.T("ConversionUiDocxOpenXmlInvalid", details)),
                    new(FormatFailureStage.DocxSchema, FormatFailureReason.SchemaValidation)
                    { Source = "docx", MainDocumentPart = true, Count = validationErrors.Count, SchemaId = validationErrors[0].Id });
            }
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or IOException or FormatException)
        {
            FormatFailureDiagnostics.Record(ex, new(FormatFailureStage.DocxPackage, FormatFailureReason.PackageRead) { Source = "docx" });
            throw FormatFailureDiagnostics.Transfer(ex, new InvalidDataException(LocalizationService.T("ConversionUiDocxDamagedOrEncrypted"), ex));
        }
    }

    private static IReadOnlyList<NormalizedBlock> ParseElements(
        IEnumerable<XElement> elements,
        IReadOnlyDictionary<string, int> headings,
        IReadOnlyDictionary<string, bool> numbering,
        IReadOnlyDictionary<string, IReadOnlyList<NormalizedBlock>> footnotes,
        IReadOnlyDictionary<string, string> resourceIds)
    {
        var result = new List<NormalizedBlock>();
        List<NormalizedListItem>? listItems = null;
        bool listOrdered = false;

        void FlushList()
        {
            if (listItems is { Count: > 0 }) result.Add(new NormalizedList(listOrdered, listItems));
            listItems = null;
        }

        foreach (var element in UnwrapContentControls(elements))
        {
            if (element.Name == W + "p")
            {
                var blocks = ParseParagraph(element, headings, footnotes, resourceIds, out var numberingId);
                if (numberingId is not null)
                {
                    var ordered = numbering.GetValueOrDefault(numberingId, true);
                    if (listItems is null || ordered != listOrdered)
                    {
                        FlushList();
                        listItems = [];
                        listOrdered = ordered;
                    }
                    listItems.Add(new NormalizedListItem(blocks));
                }
                else
                {
                    FlushList();
                    result.AddRange(blocks);
                }
            }
            else if (element.Name == W + "tbl")
            {
                FlushList();
                var rows = element.Elements(W + "tr").Select(row =>
                    new NormalizedTableRow(row.Elements(W + "tc").Select(cell =>
                    {
                        var cellBlocks = ParseElements(cell.Elements(), headings, numbering, footnotes, resourceIds);
                        var spanText = (string?)cell.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val");
                        var span = int.TryParse(spanText, out var parsed) ? Math.Max(1, parsed) : 1;
                        return new NormalizedTableCell(cellBlocks, span);
                    }).ToList())).ToList();
                result.Add(new NormalizedTable(rows));
            }
        }
        FlushList();
        return result;
    }

    private static IEnumerable<XElement> UnwrapContentControls(IEnumerable<XElement> elements)
    {
        foreach (var element in elements)
        {
            if (element.Name == W + "sdt")
            {
                foreach (var child in UnwrapContentControls(element.Element(W + "sdtContent")?.Elements() ?? []))
                    yield return child;
            }
            else
            {
                yield return element;
            }
        }
    }

    private static IReadOnlyList<NormalizedBlock> ParseParagraph(
        XElement paragraph,
        IReadOnlyDictionary<string, int> headings,
        IReadOnlyDictionary<string, IReadOnlyList<NormalizedBlock>> footnotes,
        IReadOnlyDictionary<string, string> resourceIds,
        out string? numberingId)
    {
        var result = new List<NormalizedBlock>();
        var properties = paragraph.Element(W + "pPr");
        numberingId = (string?)properties?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val");
        var text = ReadText(paragraph).Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var level = HeadingLevel(properties, headings);
            result.Add(level is null ? new NormalizedParagraph(text) : new NormalizedHeading(level.Value, text));
        }

        foreach (var image in paragraph.Descendants(A + "blip"))
        {
            var relationId = (string?)image.Attribute(R + "embed");
            if (relationId is null || !resourceIds.TryGetValue(relationId, out var resourceId)) continue;
            var container = image.Ancestors(Wp + "inline").FirstOrDefault() ?? image.Ancestors(Wp + "anchor").FirstOrDefault();
            var docProperties = container?.Element(Wp + "docPr");
            var alternative = (string?)docProperties?.Attribute("descr") ?? (string?)docProperties?.Attribute("title");
            result.Add(new NormalizedImage(resourceId, alternative));
        }

        foreach (var reference in paragraph.Descendants(W + "footnoteReference"))
        {
            var id = (string?)reference.Attribute(W + "id");
            if (id is not null && footnotes.TryGetValue(id, out var value))
                result.Add(new NormalizedFootnote($"footnote-{id}", value));
        }
        if (paragraph.Descendants(W + "br").Any(br => (string?)br.Attribute(W + "type") == "page"))
            result.Add(new NormalizedPageBreak());
        if (result.Count == 0) result.Add(new NormalizedParagraph(string.Empty));
        return result;
    }

    private static string ReadText(XElement element)
    {
        var text = new System.Text.StringBuilder();
        foreach (var node in element.Descendants())
        {
            if (node.Name == W + "t") text.Append(node.Value);
            else if (node.Name == W + "tab") text.Append('\t');
            else if (node.Name == W + "br") text.AppendLine();
        }
        return text.ToString();
    }

    private static int? HeadingLevel(XElement? properties, IReadOnlyDictionary<string, int> headings)
    {
        var outline = (string?)properties?.Element(W + "outlineLvl")?.Attribute(W + "val");
        if (int.TryParse(outline, out var level)) return Math.Clamp(level + 1, 1, 6);
        var style = (string?)properties?.Element(W + "pStyle")?.Attribute(W + "val");
        return style is not null && headings.TryGetValue(style, out level) ? level : null;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadImageRelationshipsAsync(ZipArchive archive, CancellationToken token)
    {
        var entry = archive.GetEntry("word/_rels/document.xml.rels");
        if (entry is null) return new Dictionary<string, string>();
        var xml = await LoadXmlAsync(entry, token);
        return xml.Descendants(Rel + "Relationship")
            .Where(item => (string?)item.Attribute("TargetMode") != "External")
            .Where(item => (string?)item.Attribute("Type") == R.NamespaceName + "/image")
            .Where(item => item.Attribute("Id") is not null && item.Attribute("Target") is not null)
            .ToDictionary(
                item => (string)item.Attribute("Id")!,
                item => ResolveWordPath((string)item.Attribute("Target")!),
                StringComparer.Ordinal);
    }

    private static (IReadOnlyList<NormalizedResource>, IReadOnlyDictionary<string, string>) ReadResources(
        ZipArchive archive,
        IReadOnlyDictionary<string, string> relationships)
    {
        var resources = new List<NormalizedResource>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relation in relationships)
        {
            var entry = archive.GetEntry(relation.Value);
            if (entry is null) continue;
            using var source = entry.Open();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var id = $"image-{relation.Key}";
            var name = Path.GetFileName(relation.Value);
            resources.Add(new NormalizedResource(id, MediaType(Path.GetExtension(name)), name, memory.ToArray()));
            ids[relation.Key] = id;
        }
        return (resources, ids);
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadHeadingStylesAsync(ZipArchive archive, CancellationToken token)
    {
        var entry = archive.GetEntry("word/styles.xml");
        if (entry is null) return new Dictionary<string, int>();
        var xml = await LoadXmlAsync(entry, token);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var style in xml.Descendants(W + "style"))
        {
            var id = (string?)style.Attribute(W + "styleId");
            if (id is null) continue;
            var outline = (string?)style.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val");
            if (int.TryParse(outline, out var value)) result[id] = Math.Clamp(value + 1, 1, 6);
            else
            {
                var name = (string?)style.Element(W + "name")?.Attribute(W + "val");
                var digits = name is null ? string.Empty : new string(name.Where(char.IsDigit).ToArray());
                if (name?.StartsWith("heading", StringComparison.OrdinalIgnoreCase) == true && int.TryParse(digits, out value))
                    result[id] = Math.Clamp(value, 1, 6);
            }
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, bool>> ReadNumberingAsync(ZipArchive archive, CancellationToken token)
    {
        var entry = archive.GetEntry("word/numbering.xml");
        if (entry is null) return new Dictionary<string, bool>();
        var xml = await LoadXmlAsync(entry, token);
        var abstractFormats = xml.Descendants(W + "abstractNum").Where(item => item.Attribute(W + "abstractNumId") is not null)
            .ToDictionary(
                item => (string)item.Attribute(W + "abstractNumId")!,
                item => !item.Descendants(W + "numFmt").Any(format => (string?)format.Attribute(W + "val") == "bullet"));
        return xml.Descendants(W + "num").Where(item => item.Attribute(W + "numId") is not null).ToDictionary(
            item => (string)item.Attribute(W + "numId")!,
            item =>
            {
                var id = (string?)item.Element(W + "abstractNumId")?.Attribute(W + "val");
                return id is null || abstractFormats.GetValueOrDefault(id, true);
            });
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<NormalizedBlock>>> ReadFootnotesAsync(ZipArchive archive, CancellationToken token)
    {
        var entry = archive.GetEntry("word/footnotes.xml");
        if (entry is null) return new Dictionary<string, IReadOnlyList<NormalizedBlock>>();
        var xml = await LoadXmlAsync(entry, token);
        return xml.Descendants(W + "footnote")
            .Where(item => int.TryParse((string?)item.Attribute(W + "id"), out var id) && id >= 0)
            .ToDictionary(
                item => (string)item.Attribute(W + "id")!,
                item => (IReadOnlyList<NormalizedBlock>)item.Elements(W + "p")
                    .Select(paragraph => (NormalizedBlock)new NormalizedParagraph(ReadText(paragraph).Trim()))
                    .ToList());
    }

    private static async Task<XDocument> LoadXmlAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        await using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return await XDocument.LoadAsync(reader, LoadOptions.None, token);
    }

    private static string ResolveWordPath(string target)
    {
        var parts = target.StartsWith('/') ? new List<string>() : ["word"];
        foreach (var part in target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != ".") parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static string MediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream"
    };
}
