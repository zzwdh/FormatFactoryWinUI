using System.Globalization;
using System.Text.Json;
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

            cancellationToken.ThrowIfCancellationRequested();
            OutputRecoveryService.Publish(temporaryPath, fullOutputPath, isDirectory: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task CreateArchiveAsync(EpubPublication publication, string temporaryPath, CancellationToken token)
    {
        using var lifetime = ConversionBackend.Start("epub-write", token);
        var process = lifetime.Process;
        var errors = process.StandardError.ReadToEndAsync();
        var response = process.StandardOutput.ReadToEndAsync();
        try
        {
            await lifetime.WaitAndDrainAsync(Timeout.InfiniteTimeSpan, token, SendAsync(), errors, response);
        }
        catch (IOException) when (!token.IsCancellationRequested && errors.IsCompletedSuccessfully &&
            errors.Result.StartsWith("error ", StringComparison.Ordinal))
        {
            throw ConversionBackend.DecodeFailure(errors.Result);
        }
        if (process.ExitCode != 0 || await response != "true")
            throw ConversionBackend.DecodeFailure(await errors);

        async Task SendAsync()
        {
            var stream = process.StandardInput.BaseStream;
            try
            {
                var resources = publication.Resources ?? [];
                var modified = (publication.Modified ?? _timeProvider.GetUtcNow()).UtcDateTime;
                await LineAsync(new
                {
                    path = temporaryPath, identifier = publication.Identifier, title = publication.Title,
                    language = publication.Language, creator = publication.Creator,
                    modified = modified.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    @fixed = publication.FixedLayout, chapters = publication.Chapters.Count, resources = resources.Count
                });
                foreach (var chapter in publication.Chapters)
                {
                    token.ThrowIfCancellationRequested();
                    var body = new XElement(XhtmlNamespace + "body",
                        publication.FixedLayout ? new XAttribute("class", "fixed-layout") : null,
                        chapter.Body.Count == 0 ? [new XElement(XhtmlNamespace + "p")] :
                            chapter.Body.Select(CloneAndApplyXhtmlNamespace));
                    await LineAsync(new
                    {
                        id = chapter.Id, title = XmlConvert.VerifyXmlChars(chapter.Title),
                        width = chapter.ViewportWidth, height = chapter.ViewportHeight,
                        body = body.ToString(SaveOptions.DisableFormatting)
                    });
                }
                foreach (var resource in resources)
                {
                    await using var content = resource.OpenRead();
                    var path = resource.ContentPath is null ? null : Path.GetFullPath(resource.ContentPath);
                    await LineAsync(new
                    {
                        id = resource.Id, name = NormalizeResourceName(resource.FileName), media = resource.MediaType,
                        length = content.Length, path
                    });
                    if (path is null) await content.CopyToAsync(stream, token);
                }
            }
            finally { process.StandardInput.Close(); }

            async Task LineAsync<T>(T record)
            {
                await JsonSerializer.SerializeAsync(stream, record, cancellationToken: token);
                await stream.WriteAsync(NewLine, token);
            }
        }
    }

    private static readonly byte[] NewLine = [(byte)'\n'];

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
}
