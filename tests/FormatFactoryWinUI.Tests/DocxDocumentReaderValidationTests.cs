using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class DocxDocumentReaderValidationTests
{
    [Fact]
    public async Task Reader_AcceptsModernWordAttributesAndPreservesContent()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "modern-word.docx");
        WriteDocument(path, new Paragraph(new Run(new Text("Современный документ ✓")))
        {
            ParagraphId = "12345678",
            TextId = "23456789"
        });

        using (var package = WordprocessingDocument.Open(path, false))
        {
            // The old reader used Office 2007; this fixture must reproduce its
            // undeclared-attribute rejection rather than just assert success.
            Assert.Contains(new OpenXmlValidator().Validate(package), error =>
                error.Id == "Sch_UndeclaredAttribute");
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(package));
        }

        var before = await File.ReadAllBytesAsync(path);
        var document = await new DocxDocumentReader().ReadAsync(path);

        Assert.Equal("Современный документ ✓", Assert.IsType<NormalizedParagraph>(Assert.Single(document.Blocks)).Text);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Reader_RejectsUndeclaredAttributeEvenInModernDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "undeclared-attribute.docx");
        var paragraph = new Paragraph(new Run(new Text("content"))) { ParagraphId = "12345678" };
        paragraph.SetAttribute(new OpenXmlAttribute("url", string.Empty, "https://example.invalid/"));
        WriteDocument(path, paragraph);

        await Assert.ThrowsAsync<InvalidDataException>(() => new DocxDocumentReader().ReadAsync(path));
    }

    [Fact]
    public async Task Reader_RejectsInvalidModernAttributeValue()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "invalid-modern-value.docx");
        WriteDocument(path, new Paragraph(new Run(new Text("content"))) { ParagraphId = "not-hex" });

        await Assert.ThrowsAsync<InvalidDataException>(() => new DocxDocumentReader().ReadAsync(path));
    }

    [Fact]
    public async Task Reader_RejectsInvalidDocumentStructure()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "invalid-structure.docx");
        WriteDocument(path, new Text("text outside a paragraph"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new DocxDocumentReader().ReadAsync(path));
    }

    [Fact]
    public async Task Reader_RejectsPackageWithoutBody()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "missing-body.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var part = package.AddMainDocumentPart();
            part.Document = new Document();
            part.Document.Save();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new DocxDocumentReader().ReadAsync(path));
    }

    private static void WriteDocument(string path, OpenXmlElement content)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var part = package.AddMainDocumentPart();
        part.Document = new Document(new Body(content));
        part.Document.Save();
    }
}
