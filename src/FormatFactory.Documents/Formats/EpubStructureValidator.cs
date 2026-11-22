using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>
/// Performs the deterministic structural checks needed before handing an EPUB to
/// the external EPUBCheck conformance test used by release builds.
/// </summary>
public sealed class EpubStructureValidator
{
    private static readonly XNamespace ContainerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace OpfNamespace = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";

    public async Task<EpubValidationResult> ValidateAsync(
        string epubPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epubPath);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(epubPath))
        {
            errors.Add(LocalizationService.T("ConversionUiEpubFileMissing"));
            return new EpubValidationResult(false, errors, warnings, null);
        }

        try
        {
            await ValidateMimetypeHeaderAsync(epubPath, errors, cancellationToken);
            await using var stream = new FileStream(
                epubPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .ToDictionary(entry => NormalizeArchivePath(entry.FullName), StringComparer.Ordinal);

            if (!entries.TryGetValue("mimetype", out var mimetypeEntry))
            {
                errors.Add(LocalizationService.T("ConversionUiEpubRootMimetypeMissing"));
            }
            else
            {
                var mimetype = await ReadEntryTextAsync(mimetypeEntry, cancellationToken);
                if (!string.Equals(mimetype, "application/epub+zip", StringComparison.Ordinal))
                {
                    errors.Add(LocalizationService.T("ConversionUiEpubMimetypeContentInvalid"));
                }
            }

            if (!entries.TryGetValue("META-INF/container.xml", out var containerEntry))
            {
                errors.Add(LocalizationService.T("ConversionUiEpubContainerMissing"));
                return new EpubValidationResult(false, errors, warnings, null);
            }

            var container = await LoadXmlAsync(containerEntry, cancellationToken);
            var rootfile = container
                .Descendants(ContainerNamespace + "rootfile")
                .FirstOrDefault();
            var packagePath = NormalizeArchivePath((string?)rootfile?.Attribute("full-path") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                errors.Add(LocalizationService.T("ConversionUiEpubContainerMissingOpf"));
                return new EpubValidationResult(false, errors, warnings, null);
            }

            if (!entries.TryGetValue(packagePath, out var packageEntry))
            {
                errors.Add(LocalizationService.T("ConversionUiEpubOpfNotFound", packagePath));
                return new EpubValidationResult(false, errors, warnings, packagePath);
            }

            var package = await LoadXmlAsync(packageEntry, cancellationToken);
            await ValidatePackageAsync(package, packagePath, entries, errors, warnings, cancellationToken);
            return new EpubValidationResult(errors.Count == 0, errors, warnings, packagePath);
        }
        catch (InvalidDataException ex)
        {
            errors.Add(LocalizationService.T("ConversionUiEpubZipInvalid", ex.Message));
            return new EpubValidationResult(false, errors, warnings, null);
        }
        catch (System.Xml.XmlException ex)
        {
            errors.Add(LocalizationService.T("ConversionUiEpubXmlInvalid", ex.Message));
            return new EpubValidationResult(false, errors, warnings, null);
        }
    }

    private static async Task ValidatePackageAsync(
        XDocument package,
        string packagePath,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        List<string> errors,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var root = package.Root;
        if (root?.Name != OpfNamespace + "package")
        {
            errors.Add(LocalizationService.T("ConversionUiOpfRootMustBePackage"));
            return;
        }

        var metadata = root.Element(OpfNamespace + "metadata");
        RequireMetadata(metadata, DublinCoreNamespace + "identifier", "identifier", errors);
        RequireMetadata(metadata, DublinCoreNamespace + "title", "title", errors);
        RequireMetadata(metadata, DublinCoreNamespace + "language", "language", errors);
        var fixedLayout = metadata?
            .Elements(OpfNamespace + "meta")
            .Any(element =>
                (string?)element.Attribute("property") == "rendition:layout" &&
                element.Value.Trim().Equals("pre-paginated", StringComparison.Ordinal)) == true;

        var manifestItems = root
            .Element(OpfNamespace + "manifest")?
            .Elements(OpfNamespace + "item")
            .ToList() ?? [];
        if (manifestItems.Count == 0)
        {
            errors.Add(LocalizationService.T("ConversionUiOpfManifestNotEmpty"));
            return;
        }

        var duplicateIds = manifestItems
            .Select(item => (string?)item.Attribute("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicateId in duplicateIds)
        {
            errors.Add(LocalizationService.T("ConversionUiOpfManifestDuplicateId", duplicateId));
        }

        var manifestById = manifestItems
            .Where(item => !string.IsNullOrWhiteSpace((string?)item.Attribute("id")))
            .GroupBy(item => (string)item.Attribute("id")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var packageDirectory = ArchiveDirectory(packagePath);
        var hasNavigation = false;

        foreach (var item in manifestItems)
        {
            var id = (string?)item.Attribute("id");
            var href = (string?)item.Attribute("href");
            var mediaType = (string?)item.Attribute("media-type");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(mediaType))
            {
                errors.Add(LocalizationService.T("ConversionUiOpfManifestItemFieldsRequired"));
                continue;
            }

            var properties = ((string?)item.Attribute("properties") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            hasNavigation |= properties.Contains("nav", StringComparer.Ordinal);

            if (!Uri.TryCreate(href, UriKind.Absolute, out _))
            {
                var archivePath = ResolveArchivePath(packageDirectory, href);
                if (!entries.ContainsKey(archivePath))
                {
                    errors.Add(LocalizationService.T("ConversionUiOpfManifestResourceMissing", archivePath));
                }
            }
        }

        if (!hasNavigation)
        {
            errors.Add(LocalizationService.T("ConversionUiEpubNavigationRequired"));
        }

        var spineItems = root
            .Element(OpfNamespace + "spine")?
            .Elements(OpfNamespace + "itemref")
            .ToList() ?? [];
        if (spineItems.Count == 0)
        {
            errors.Add(LocalizationService.T("ConversionUiOpfSpineNotEmpty"));
        }

        foreach (var itemref in spineItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idref = (string?)itemref.Attribute("idref");
            if (string.IsNullOrWhiteSpace(idref) || !manifestById.ContainsKey(idref))
            {
                errors.Add(LocalizationService.T(
                    "ConversionUiOpfSpineMissingManifestId",
                    idref ?? LocalizationService.T("ConversionUiEmptyValue")));
                continue;
            }

            if (fixedLayout)
            {
                var manifestItem = manifestById[idref];
                var href = (string?)manifestItem.Attribute("href");
                var mediaType = (string?)manifestItem.Attribute("media-type");
                if (string.IsNullOrWhiteSpace(href) || mediaType != "application/xhtml+xml") continue;
                var contentPath = ResolveArchivePath(packageDirectory, href);
                if (!entries.TryGetValue(contentPath, out var contentEntry)) continue;
                var content = await LoadXmlAsync(contentEntry, cancellationToken);
                var viewport = content.Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "meta" &&
                        (string?)element.Attribute("name") == "viewport");
                if (string.IsNullOrWhiteSpace((string?)viewport?.Attribute("content")))
                {
                    errors.Add(LocalizationService.T("ConversionUiFixedLayoutViewportMissing", contentPath));
                }
            }
        }

        if ((string?)root.Attribute("version") != "3.0")
        {
            warnings.Add(LocalizationService.T("ConversionUiOpfVersionRecommendation"));
        }
    }

    private static void RequireMetadata(XElement? metadata, XName name, string displayName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(metadata?.Elements(name).FirstOrDefault()?.Value))
        {
            errors.Add(LocalizationService.T("ConversionUiOpfMetadataMissing", displayName));
        }
    }

    private static async Task ValidateMimetypeHeaderAsync(
        string epubPath,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var header = new byte[30];
        await using var stream = new FileStream(
            epubPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (await stream.ReadAsync(header, cancellationToken) != header.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034B50)
        {
            errors.Add(LocalizationService.T("ConversionUiEpubNotValidZip"));
            return;
        }

        var compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26, 2));
        var fileName = new byte[fileNameLength];
        if (await stream.ReadAsync(fileName, cancellationToken) != fileName.Length ||
            !fileName.AsSpan().SequenceEqual("mimetype"u8))
        {
            errors.Add(LocalizationService.T("ConversionUiEpubMimetypeMustBeFirst"));
        }

        if (compressionMethod != 0)
        {
            errors.Add(LocalizationService.T("ConversionUiEpubMimetypeUncompressed"));
        }
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<XDocument> LoadXmlAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
    }

    private static string NormalizeArchivePath(string value) =>
        value.Replace('\\', '/').TrimStart('/');

    private static string ArchiveDirectory(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string ResolveArchivePath(string baseDirectory, string href)
    {
        var decoded = Uri.UnescapeDataString(href.Split('#', 2)[0]);
        var combined = string.IsNullOrEmpty(baseDirectory) ? decoded : $"{baseDirectory}/{decoded}";
        var parts = new List<string>();
        foreach (var part in NormalizeArchivePath(combined).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }
}
