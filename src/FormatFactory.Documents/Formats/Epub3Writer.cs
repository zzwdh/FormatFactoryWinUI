using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>
/// Writes a compact EPUB 3 publication without an external runtime dependency.
/// EPUBCheck remains the release conformance authority; the built-in validator
/// prevents malformed artifacts from entering the conversion history.
/// </summary>
public sealed class Epub3Writer
{
    private static readonly XNamespace XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace EpubNamespace = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace OpfNamespace = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
    private readonly EpubStructureValidator _validator;
    private readonly TimeProvider _timeProvider;

    public Epub3Writer(EpubStructureValidator? validator = null, TimeProvider? timeProvider = null)
    {
        _validator = validator ?? new EpubStructureValidator();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task WriteAsync(
        EpubPublication publication,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ValidatePublication(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(fullOutputPath).Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiEpubOutputMustUseExtension"), nameof(outputPath));
        }

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiEpubOutputNeedsDirectory"), nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await CreateArchiveAsync(publication, temporaryPath, cancellationToken);
            var validation = await _validator.ValidateAsync(temporaryPath, cancellationToken);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(LocalizationService.T("ConversionUiGeneratedEpubStructureInvalid", string.Join("; ", validation.Errors)));
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task CreateArchiveAsync(
        EpubPublication publication,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);

        await WriteTextEntryAsync(
            archive,
            "mimetype",
            "application/epub+zip",
            CompressionLevel.NoCompression,
            cancellationToken);
        await WriteXmlEntryAsync(archive, "META-INF/container.xml", CreateContainer(), cancellationToken);
        await WriteTextEntryAsync(archive, "EPUB/styles.css", DefaultStyles, CompressionLevel.Optimal, cancellationToken);

        var chapterFiles = new List<(EpubChapter Chapter, string FileName)>();
        for (var index = 0; index < publication.Chapters.Count; index++)
        {
            var chapter = publication.Chapters[index];
            var fileName = $"chapter-{index + 1:D4}.xhtml";
            chapterFiles.Add((chapter, fileName));
            await WriteXmlEntryAsync(
                archive,
                $"EPUB/{fileName}",
                CreateChapterDocument(chapter, publication.Language, publication.FixedLayout),
                cancellationToken);
        }

        await WriteXmlEntryAsync(
            archive,
            "EPUB/nav.xhtml",
            CreateNavigation(publication, chapterFiles),
            cancellationToken);

        foreach (var resource in publication.Resources ?? [])
        {
            var entry = archive.CreateEntry($"EPUB/assets/{NormalizeResourceName(resource.FileName)}", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var source = resource.OpenRead();
            await source.CopyToAsync(entryStream, cancellationToken);
        }

        var modified = publication.Modified ?? _timeProvider.GetUtcNow();
        await WriteXmlEntryAsync(
            archive,
            "EPUB/package.opf",
            CreatePackageDocument(publication, chapterFiles, modified),
            cancellationToken);
    }

    private static XDocument CreateContainer() =>
        new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:container") + "container",
                new XAttribute("version", "1.0"),
                new XElement(
                    XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:container") + "rootfiles",
                    new XElement(
                        XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:container") + "rootfile",
                        new XAttribute("full-path", "EPUB/package.opf"),
                        new XAttribute("media-type", "application/oebps-package+xml")))));

    private static XDocument CreateChapterDocument(EpubChapter chapter, string language, bool fixedLayout)
    {
        var bodyNodes = chapter.Body.Select(CloneAndApplyXhtmlNamespace).ToList();
        if (bodyNodes.Count == 0)
        {
            bodyNodes.Add(new XElement(XhtmlNamespace + "p"));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                XhtmlNamespace + "html",
                new XAttribute(XNamespace.Xml + "lang", language),
                new XElement(
                    XhtmlNamespace + "head",
                    new XElement(XhtmlNamespace + "title", chapter.Title),
                    fixedLayout
                        ? new XElement(
                            XhtmlNamespace + "meta",
                            new XAttribute("name", "viewport"),
                            new XAttribute(
                                "content",
                                $"width={Math.Max(1, chapter.ViewportWidth ?? 1200)},height={Math.Max(1, chapter.ViewportHeight ?? 1600)}"))
                        : null,
                    new XElement(
                        XhtmlNamespace + "link",
                        new XAttribute("rel", "stylesheet"),
                        new XAttribute("type", "text/css"),
                        new XAttribute("href", "styles.css"))),
                new XElement(
                    XhtmlNamespace + "body",
                    fixedLayout ? new XAttribute("class", "fixed-layout") : null,
                    bodyNodes)));
    }

    private static XDocument CreateNavigation(
        EpubPublication publication,
        IReadOnlyList<(EpubChapter Chapter, string FileName)> chapterFiles) =>
        new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                XhtmlNamespace + "html",
                new XAttribute(XNamespace.Xmlns + "epub", EpubNamespace),
                new XAttribute(XNamespace.Xml + "lang", publication.Language),
                new XElement(
                    XhtmlNamespace + "head",
                    new XElement(XhtmlNamespace + "title", publication.Title)),
                new XElement(
                    XhtmlNamespace + "body",
                    new XElement(
                        XhtmlNamespace + "nav",
                        new XAttribute(EpubNamespace + "type", "toc"),
                        new XElement(XhtmlNamespace + "h1", publication.Title),
                        new XElement(
                            XhtmlNamespace + "ol",
                            chapterFiles.Select(item =>
                                new XElement(
                                    XhtmlNamespace + "li",
                                    new XElement(
                                        XhtmlNamespace + "a",
                                        new XAttribute("href", item.FileName),
                                        item.Chapter.Title))))))));

    private static XDocument CreatePackageDocument(
        EpubPublication publication,
        IReadOnlyList<(EpubChapter Chapter, string FileName)> chapterFiles,
        DateTimeOffset modified)
    {
        var resources = publication.Resources ?? [];
        var metadata = new XElement(
            OpfNamespace + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", DublinCoreNamespace),
            new XElement(DublinCoreNamespace + "identifier", new XAttribute("id", "pub-id"), publication.Identifier),
            new XElement(DublinCoreNamespace + "title", publication.Title),
            new XElement(DublinCoreNamespace + "language", publication.Language),
            new XElement(
                OpfNamespace + "meta",
                new XAttribute("property", "dcterms:modified"),
                modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(publication.Creator))
        {
            metadata.Add(new XElement(DublinCoreNamespace + "creator", publication.Creator));
        }
        if (publication.FixedLayout)
        {
            metadata.Add(
                new XElement(OpfNamespace + "meta", new XAttribute("property", "rendition:layout"), "pre-paginated"),
                new XElement(OpfNamespace + "meta", new XAttribute("property", "rendition:spread"), "none"),
                new XElement(OpfNamespace + "meta", new XAttribute("property", "rendition:orientation"), "auto"));
        }

        var manifest = new XElement(
            OpfNamespace + "manifest",
            new XElement(
                OpfNamespace + "item",
                new XAttribute("id", "nav"),
                new XAttribute("href", "nav.xhtml"),
                new XAttribute("media-type", "application/xhtml+xml"),
                new XAttribute("properties", "nav")),
            new XElement(
                OpfNamespace + "item",
                new XAttribute("id", "styles"),
                new XAttribute("href", "styles.css"),
                new XAttribute("media-type", "text/css")),
            chapterFiles.Select(item =>
                new XElement(
                    OpfNamespace + "item",
                    new XAttribute("id", item.Chapter.Id),
                    new XAttribute("href", item.FileName),
                    new XAttribute("media-type", "application/xhtml+xml"))),
            resources.Select(resource =>
                new XElement(
                    OpfNamespace + "item",
                    new XAttribute("id", resource.Id),
                    new XAttribute("href", $"assets/{NormalizeResourceName(resource.FileName)}"),
                    new XAttribute("media-type", resource.MediaType))));

        var spine = new XElement(
            OpfNamespace + "spine",
            chapterFiles.Select(item =>
                new XElement(OpfNamespace + "itemref", new XAttribute("idref", item.Chapter.Id))));

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                OpfNamespace + "package",
                new XAttribute("version", "3.0"),
                new XAttribute("unique-identifier", "pub-id"),
                new XAttribute(XNamespace.Xml + "lang", publication.Language),
                metadata,
                manifest,
                spine));
    }

    private static XNode CloneAndApplyXhtmlNamespace(XNode node) => node switch
    {
        XElement element => CloneElement(element),
        XCData cdata => new XCData(cdata.Value),
        XText text => new XText(text.Value),
        _ => new XText(node.ToString(SaveOptions.DisableFormatting))
    };

    private static XElement CloneElement(XElement source)
    {
        var name = source.Name.Namespace == XNamespace.None
            ? XhtmlNamespace + source.Name.LocalName
            : source.Name;
        return new XElement(
            name,
            source.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .Select(attribute => new XAttribute(attribute.Name, attribute.Value)),
            source.Nodes().Select(CloneAndApplyXhtmlNamespace));
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string path,
        string text,
        CompressionLevel compressionLevel,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static async Task WriteXmlEntryAsync(
        ZipArchive archive,
        string path,
        XDocument document,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false
        };
        using var writer = XmlWriter.Create(stream, settings);
        await document.SaveAsync(writer, cancellationToken);
        await writer.FlushAsync();
    }

    private static void ValidatePublication(EpubPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (string.IsNullOrWhiteSpace(publication.Identifier) ||
            string.IsNullOrWhiteSpace(publication.Title) ||
            string.IsNullOrWhiteSpace(publication.Language))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiEpubMetadataRequired"), nameof(publication));
        }

        if (publication.Chapters.Count == 0)
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiEpubChapterRequired"), nameof(publication));
        }

        var ids = publication.Chapters.Select(chapter => chapter.Id)
            .Concat((publication.Resources ?? []).Select(resource => resource.Id))
            .ToList();
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || !XmlConvert.VerifyNCName(id).Equals(id, StringComparison.Ordinal)) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiEpubIdsMustBeUniqueNcNames"), nameof(publication));
        }

        foreach (var resource in publication.Resources ?? [])
        {
            _ = NormalizeResourceName(resource.FileName);
            using var content = resource.OpenRead();
            if (string.IsNullOrWhiteSpace(resource.MediaType) || content.Length == 0)
            {
                throw new ArgumentException(LocalizationService.T("ConversionUiEpubResourceRequiresMediaAndContent"), nameof(publication));
            }
        }
    }

    private static string NormalizeResourceName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var normalized = fileName.Replace('\\', '/');
        if (normalized.StartsWith('/', StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiEpubResourceNameSafeRelative"), nameof(fileName));
        }

        return normalized;
    }

    private const string DefaultStyles = """
        html { writing-mode: horizontal-tb; }
        body { font-family: serif; line-height: 1.6; margin: 5%; }
        img { max-width: 100%; height: auto; }
        body.fixed-layout { box-sizing: border-box; width: 100vw; height: 100vh; margin: 0; overflow: hidden; }
        body.fixed-layout figure { box-sizing: border-box; width: 100%; height: 100%; margin: 0; }
        body.fixed-layout img { width: 100%; height: 100%; object-fit: contain; }
        table { border-collapse: collapse; max-width: 100%; }
        th, td { border: 1px solid currentColor; padding: 0.25em; }
        .page-break { break-before: page; }
        """;
}
