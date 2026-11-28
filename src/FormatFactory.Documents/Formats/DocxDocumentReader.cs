using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Reads reflowable content from WordprocessingML without requiring Word.</summary>
public sealed class DocxDocumentReader
{
    private sealed record ImageResource(string Id, string MediaType, string FileName);
    private sealed record Content(string Title, string Language, NormalizedBlock[] Blocks, ImageResource[] Resources);

    public Task<NormalizedDocument> ReadAsync(string docxPath, CancellationToken cancellationToken = default,
        string? fallbackTitle = null) => ReadCoreAsync(docxPath, fallbackTitle, null, cancellationToken);

    internal Task<NormalizedDocument> ReadForConversionAsync(string path, string workspace, string? fallbackTitle,
        CancellationToken token) => ReadCoreAsync(path, fallbackTitle, workspace, token);

    private static async Task<NormalizedDocument> ReadCoreAsync(string docxPath, string? fallbackTitle,
        string? workspace, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docxPath);
        token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(docxPath);
        if (!File.Exists(path)) throw FormatFailureDiagnostics.Attach(
            new FileNotFoundException(LocalizationService.T("ConversionUiDocxDocumentNotFound"), path),
            new(FormatFailureStage.DocxPackage, FormatFailureReason.Missing) { Source = "docx" });

        // Both readers must observe the same validated package.
        using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateOpenXmlPackage(path, token);
        var assetDirectory = Path.Combine(Path.GetFullPath(workspace ?? Path.GetTempPath()), "docx-assets-" + Guid.NewGuid().ToString("N"));
        var keepAssets = false;
        try
        {
            var data = await ConversionBackend.RequestAsync<Content>("document-read", new
            {
                Path = path, Assets = assetDirectory, FallbackTitle = fallbackTitle ?? Path.GetFileNameWithoutExtension(path)
            }, token, DocumentTextWorker.JsonOptions).ConfigureAwait(false);
            var resources = new List<NormalizedResource>(data.Resources.Length);
            for (var index = 0; index < data.Resources.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var asset = Path.Combine(assetDirectory, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var metadata = data.Resources[index];
                var resource = new NormalizedResource(metadata.Id, metadata.MediaType, metadata.FileName,
                    workspace is null ? await File.ReadAllBytesAsync(asset, token).ConfigureAwait(false) : ReadOnlyMemory<byte>.Empty)
                {
                    ContentPath = workspace is null ? null : asset
                };
                resources.Add(resource);
            }
            token.ThrowIfCancellationRequested();
            keepAssets = workspace is not null;
            return new NormalizedDocument(data.Title, data.Language, [new NormalizedSection("section-1", data.Blocks)], resources);
        }
        catch (InvalidDataException error)
        {
            var missingMain = error.Message == LocalizationService.T("ConversionUiDocxDocumentXmlMissing");
            var missingBody = missingMain || error.Message == LocalizationService.T("ConversionUiDocxBodyMissing");
            FormatFailureDiagnostics.Record(error, new(FormatFailureStage.DocxPackage,
                missingBody ? FormatFailureReason.MissingBody : FormatFailureReason.PackageRead)
                { Source = "docx", MainDocumentPart = !missingMain });
            throw;
        }
        finally
        {
            if (!keepAssets && Directory.Exists(assetDirectory)) Directory.Delete(assetDirectory, recursive: true);
        }
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
}
