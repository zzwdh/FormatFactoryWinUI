using System.Globalization;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Strict DOCX import, with explicit opt-in normalization of a temporary copy.</summary>
public sealed class DocxImportService(DocxDocumentReader reader, ExternalDocumentConverter converter)
{
    public async Task<NormalizedDocument> ReadAsync(
        string inputPath,
        bool repairOnValidationFailure,
        IReadOnlyList<ExternalToolInfo> tools,
        string workspacePath,
        IProgress<ConversionExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await reader.ReadForConversionAsync(inputPath, workspacePath, null, cancellationToken);
        }
        catch (InvalidDataException originalError) when (repairOnValidationFailure && !cancellationToken.IsCancellationRequested)
        {
            var libreOffice = tools.FirstOrDefault(tool => tool.Kind == ExternalToolKind.LibreOffice && tool.IsAvailable)
                ?? throw new DocumentEngineUnavailableException(LocalizationService.T("DocumentCompatibilityRepairUnavailable"));
            var workspace = Path.GetFullPath(workspacePath);
            var temporaryDirectory = Path.Combine(workspace, "docx-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                progress?.Report(new ConversionExecutionProgress(25, "docx-repair",
                    LocalizationService.T("DocumentCompatibilityRepairing")));
                // Never give the repair process a user's original file to modify.
                var copyPath = Path.Combine(temporaryDirectory, "source.docx");
                await using (var source = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var copy = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous))
                    await source.CopyToAsync(copy, cancellationToken);

                var normalizedPath = Path.Combine(temporaryDirectory, "normalized.docx");
                await converter.ConvertWithLibreOfficeAsync(libreOffice, copyPath, normalizedPath, cancellationToken);
                NormalizeRegeneratedGridSpacing(normalizedPath, cancellationToken);
                // A successful external process is insufficient: the regenerated
                // package must pass the same full schema and structure checks.
                return await reader.ReadForConversionAsync(normalizedPath, workspace,
                    Path.GetFileNameWithoutExtension(inputPath), cancellationToken);
            }
            catch (Exception repairError) when (repairError is IOException or InvalidDataException or
                InvalidOperationException or XmlException or FormatException)
            {
                throw new InvalidDataException(LocalizationService.T("DocumentCompatibilityRepairFailed",
                    originalError.Message, repairError.Message), repairError);
            }
            finally
            {
                // This path is created by this method under the supplied pipeline workspace.
                var prefix = workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(temporaryDirectory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(temporaryDirectory, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static void NormalizeRegeneratedGridSpacing(string path, CancellationToken cancellationToken)
    {
        // LibreOffice can serialize a negative 32-bit grid offset as an unsigned
        // decimal (e.g. -4097 as 4294963199). Correct only its temporary output;
        // preserve the bits and keep full validation for every other attribute.
        using var package = WordprocessingDocument.Open(path, true, new OpenSettings { AutoSave = false });
        var document = package.MainDocumentPart?.Document;
        if (document is null) return;
        var changed = false;
        foreach (var grid in document.Descendants<DocGrid>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spacing = grid.GetAttributes().FirstOrDefault(attribute => attribute.LocalName == "charSpace" &&
                attribute.NamespaceUri == "http://schemas.openxmlformats.org/wordprocessingml/2006/main").Value;
            if (!uint.TryParse(spacing, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned) || unsigned <= int.MaxValue)
                continue;
            grid.CharacterSpace = unchecked((int)unsigned);
            changed = true;
        }
        if (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            document.Save();
        }
    }
}
