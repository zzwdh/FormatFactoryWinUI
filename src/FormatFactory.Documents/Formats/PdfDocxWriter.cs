using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Writes the editable text and page breaks extracted from a PDF without requiring Office.</summary>
public sealed class PdfDocxWriter
{
    public Task WriteAsync(NormalizedDocument document, string outputPath, CancellationToken cancellationToken = default) =>
        WritePackageAsync(document, outputPath, includeProperties: true, cancellationToken);

    internal async Task WritePackageAsync(NormalizedDocument document, string outputPath, bool includeProperties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await ConversionBackend.RequestStreamAsync<bool>("docx-write",
                stream => SendAsync(stream, temporary, document, includeProperties, cancellationToken), cancellationToken).ConfigureAwait(false);
            await ValidateAsync(temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            OutputRecoveryService.Publish(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly byte[] Paragraph = [(byte)'P'];
    private static readonly byte[] PageBreak = [(byte)'B'];
    private static readonly byte[] End = [(byte)'E'];

    private static async Task SendAsync(Stream stream, string path, NormalizedDocument document, bool properties, CancellationToken token)
    {
        await JsonSerializer.SerializeAsync(stream, new { output = path, properties }, cancellationToken: token).ConfigureAwait(false);
        await stream.WriteAsync(NewLine, token).ConfigureAwait(false);
        var buffer = new byte[32 * 1024];
        await WriteTextAsync(document.Title).ConfigureAwait(false);
        await WriteTextAsync(document.Language).ConfigureAwait(false);
        foreach (var block in document.Blocks)
        {
            token.ThrowIfCancellationRequested();
            switch (block)
            {
                case NormalizedParagraph paragraph:
                    await stream.WriteAsync(Paragraph, token).ConfigureAwait(false);
                    await WriteTextAsync(paragraph.Text).ConfigureAwait(false);
                    break;
                case NormalizedPageBreak:
                    await stream.WriteAsync(PageBreak, token).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"PDF DOCX text output does not support {block.GetType().Name}.");
            }
        }
        await stream.WriteAsync(End, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        async Task WriteTextAsync(string text)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, text.Length);
            await stream.WriteAsync(buffer.AsMemory(0, sizeof(int)), token).ConfigureAwait(false);
            for (var index = 0; index < text.Length;)
            {
                var size = Math.Min(buffer.Length / sizeof(char), text.Length - index);
                // Both supported Windows architectures use little-endian UTF-16. Sending code units
                // preserves unpaired surrogates until the Rust XML writer can discard them.
                MemoryMarshal.AsBytes(text.AsSpan(index, size)).CopyTo(buffer);
                await stream.WriteAsync(buffer.AsMemory(0, size * sizeof(char)), token).ConfigureAwait(false);
                index += size;
            }
        }
    }

    public Task ValidateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => Validate(Path.GetFullPath(path), cancellationToken), cancellationToken);
    }

    private static void Validate(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var package = WordprocessingDocument.Open(path, false, new OpenSettings { AutoSave = false });
            if (package.DocumentType != WordprocessingDocumentType.Document || package.MainDocumentPart?.Document?.Body is null)
                throw new InvalidDataException(LocalizationService.T("ConversionUiDocxMainBodyMissing"));
            var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(package, token).Take(8).ToList();
            if (errors.Count > 0)
                throw new InvalidDataException(LocalizationService.T("ConversionUiDocxOpenXmlInvalid",
                    string.Join("; ", errors.Select(error => error.Description))));
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is OpenXmlPackageException or IOException or FormatException or XmlException)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiDocxDamagedOrEncrypted"), error);
        }
    }
}
