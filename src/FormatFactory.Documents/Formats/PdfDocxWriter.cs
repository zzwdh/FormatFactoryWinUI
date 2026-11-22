using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Writes the editable text and page breaks extracted from a PDF without requiring Office.</summary>
public sealed class PdfDocxWriter
{
    public Task WriteAsync(NormalizedDocument document, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return Task.Run(() => Write(document, Path.GetFullPath(outputPath), cancellationToken), cancellationToken);
    }

    public Task ValidateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => Validate(Path.GetFullPath(path), cancellationToken), cancellationToken);
    }

    private static void Write(NormalizedDocument document, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var package = WordprocessingDocument.Create(temporaryPath, WordprocessingDocumentType.Document))
            {
                var language = CleanXmlText(document.Language);
                if (string.IsNullOrWhiteSpace(language)) language = "zh-CN";
                package.PackageProperties.Title = CleanXmlText(document.Title);
                package.PackageProperties.Language = language;
                var main = package.AddMainDocumentPart();
                var styles = main.AddNewPart<StyleDefinitionsPart>();
                styles.Styles = new Styles(new DocDefaults(new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(new Languages { Val = language, EastAsia = language }))));
                styles.Styles.Save();

                // Stream the body so a long PDF does not also become a second in-memory XML tree.
                using var writer = OpenXmlWriter.Create(main);
                writer.WriteStartElement(new Document());
                writer.WriteStartElement(new Body());
                var hasVisibleText = false;
                foreach (var block in document.Blocks)
                {
                    token.ThrowIfCancellationRequested();
                    switch (block)
                    {
                        case NormalizedParagraph paragraph:
                            hasVisibleText |= HasVisibleText(paragraph.Text);
                            WriteParagraphs(writer, paragraph.Text, token);
                            break;
                        case NormalizedPageBreak:
                            writer.WriteElement(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                            break;
                        default:
                            // This writer is deliberately restricted to the PDF text extraction contract.
                            // Never silently discard images, tables or other structured content.
                            throw new NotSupportedException($"PDF DOCX text output does not support {block.GetType().Name}.");
                    }
                }
                if (!hasVisibleText) throw new InvalidDataException(LocalizationService.T("PdfDocxNoText"));
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            token.ThrowIfCancellationRequested();
            Validate(temporaryPath, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void WriteParagraphs(OpenXmlWriter writer, string text, CancellationToken token)
    {
        // PDF extraction provides one text block per page. Preserve its line boundaries
        // as Word paragraphs without allocating an array containing every source line.
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            if (text[index] is not ('\r' or '\n')) continue;
            WriteParagraph(writer, text.AsSpan(start, index - start), token);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            start = index + 1;
        }
        WriteParagraph(writer, text.AsSpan(start), token);
    }

    private static void WriteParagraph(OpenXmlWriter writer, ReadOnlySpan<char> text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        writer.WriteStartElement(new Paragraph());
        writer.WriteStartElement(new Run());
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            if (index < text.Length && text[index] != '\t') continue;
            if (index > start)
                writer.WriteElement(new Text(CleanXmlText(text[start..index])) { Space = SpaceProcessingModeValues.Preserve });
            if (index < text.Length) writer.WriteElement(new TabChar());
            start = index + 1;
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string CleanXmlText(ReadOnlySpan<char> value)
    {
        StringBuilder? cleaned = null;
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }
            if (XmlConvert.IsXmlChar(value[index])) continue;
            cleaned ??= new StringBuilder(value.Length);
            cleaned.Append(value[start..index]);
            start = index + 1;
        }
        return cleaned is null ? value.ToString() : cleaned.Append(value[start..]).ToString();
    }

    private static bool HasVisibleText(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                var rune = new Rune(text[index], text[index + 1]);
                if (Rune.GetUnicodeCategory(rune) is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control))
                    return true;
                index++;
                continue;
            }
            if (XmlConvert.IsXmlChar(text[index]) && !char.IsWhiteSpace(text[index]) && !char.IsControl(text[index]) &&
                char.GetUnicodeCategory(text[index]) != System.Globalization.UnicodeCategory.Format) return true;
        }
        return false;
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
