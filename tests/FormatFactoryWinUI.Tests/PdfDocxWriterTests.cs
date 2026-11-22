using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class PdfDocxWriterTests
{
    [Fact]
    public async Task TextPdf_BecomesEditableDocxWithMetadataAndPageBreaks_WithoutChangingSource()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "two-pages.pdf");
        var output = Path.Combine(directory.Path, "结果.docx");
        WriteTextPdf(source);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(source));

        var extraction = await new PdfContentExtractor().ExtractAsync(source, "en-US");
        await new PdfDocxWriter().WriteAsync(extraction.Document, output);
        await new PdfDocxWriter().ValidateAsync(output);

        using var docx = WordprocessingDocument.Open(output, false);
        Assert.Equal("PDF title", docx.PackageProperties.Title);
        Assert.Equal("en-US", docx.PackageProperties.Language);
        var main = Assert.IsType<MainDocumentPart>(docx.MainDocumentPart);
        var body = Assert.IsType<Body>(Assert.IsType<Document>(main.Document).Body);
        Assert.Contains("First page editable text", body.InnerText, StringComparison.Ordinal);
        Assert.Contains("Second page editable text", body.InnerText, StringComparison.Ordinal);
        Assert.Single(body.Descendants<Break>(), item => item.Type?.Value == BreakValues.Page);
        Assert.Empty(main.ImageParts);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    [Fact]
    public async Task Writer_PreservesUnicodeWhitespaceAndParagraphs_AndRemovesInvalidXmlControls()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "unicode.docx");
        var document = Document("  中文 😀 & <xml>  \t值\r\n第二行\n\n末行\u0000\u0001\ud800\ufffe", "标题 😀", "zh-CN");

        await new PdfDocxWriter().WriteAsync(document, path);

        using var package = WordprocessingDocument.Open(path, false);
        var main = Assert.IsType<MainDocumentPart>(package.MainDocumentPart);
        var paragraphs = Assert.IsType<Body>(Assert.IsType<Document>(main.Document).Body).Elements<Paragraph>().ToArray();
        Assert.Equal(4, paragraphs.Length);
        Assert.Equal("  中文 😀 & <xml>  值", paragraphs[0].InnerText);
        Assert.Single(paragraphs[0].Descendants<TabChar>());
        Assert.All(paragraphs[0].Descendants<Text>(), text => Assert.Equal(SpaceProcessingModeValues.Preserve, text.Space!.Value));
        Assert.Equal("第二行", paragraphs[1].InnerText);
        Assert.Equal(string.Empty, paragraphs[2].InnerText);
        Assert.Equal("末行", paragraphs[3].InnerText);
        Assert.Equal("标题 😀", package.PackageProperties.Title);
        Assert.Equal("zh-CN", package.PackageProperties.Language);
        var styles = Assert.IsType<Styles>(Assert.IsType<StyleDefinitionsPart>(main.StyleDefinitionsPart).Styles);
        Assert.Equal("zh-CN", styles.Descendants<Languages>().Single().Val!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScannedAndEncryptedPdf_OcrTextIsEditableAndPasswordReachesOcr(bool encrypted)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "scanned.pdf");
        var output = Path.Combine(directory.Path, "ocr.docx");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            pdf.AddPage();
            pdf.AddPage();
            if (encrypted)
            {
                pdf.SecuritySettings.UserPassword = "reader-secret";
                pdf.SecuritySettings.OwnerPassword = "owner-secret";
                pdf.SecuritySettings.PermitExtractContent = true;
            }
            pdf.Save(source);
        }
        var ocr = new RecordingOcr();
        var password = encrypted ? "reader-secret" : null;
        var extraction = await new PdfContentExtractor(ocr).ExtractAsync(source, "zh-Hans", password);

        await new PdfDocxWriter().WriteAsync(extraction.Document, output);

        Assert.Equal(password, ocr.Password);
        Assert.Equal("zh-Hans", ocr.Language);
        Assert.Equal(new[] { 0, 1 }, ocr.Pages);
        var roundtrip = await new DocxDocumentReader().ReadAsync(output);
        Assert.Contains(roundtrip.Blocks, block => block is NormalizedParagraph { Text: "扫描文字 第1页" });
        Assert.Contains(roundtrip.Blocks, block => block is NormalizedParagraph { Text: "扫描文字 第2页" });
        Assert.Single(roundtrip.Blocks.OfType<NormalizedPageBreak>());
    }

    [Fact]
    public async Task CancelledWriter_DoesNotCreateStagingFileOrReplaceExistingOutput()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "existing.docx");
        await File.WriteAllTextAsync(output, "existing output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfDocxWriter().WriteAsync(Document("text"), output, cancellation.Token));

        Assert.Equal("existing output", await File.ReadAllTextAsync(output));
        Assert.Equal(new[] { output }, Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancellationAfterWritingStarts_RemovesStagingPackage()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "cancelled.docx");
        using var cancellation = new CancellationTokenSource();
        var document = new NormalizedDocument("cancel", "en", [new NormalizedSection("page",
            new CancelAfterFirstBlock(cancellation))], []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfDocxWriter().WriteAsync(document, output, cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ExistingOutput_IsPreservedAndStagingPackageIsCleaned()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "existing.docx");
        await File.WriteAllTextAsync(output, "previous content");

        await Assert.ThrowsAsync<IOException>(() => new PdfDocxWriter().WriteAsync(Document("new content"), output));

        Assert.Equal("previous content", await File.ReadAllTextAsync(output));
        Assert.Equal(new[] { output }, Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task UnexpectedStructuredContent_IsRejectedWithoutSilentDataLossOrPartialOutput()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "unsupported.docx");
        var document = new NormalizedDocument("unsupported", "en",
            [new NormalizedSection("page", [new NormalizedParagraph("before"), new NormalizedImage("image")])], []);

        await Assert.ThrowsAsync<NotSupportedException>(() => new PdfDocxWriter().WriteAsync(document, output));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task EmptyOcrResult_IsRejectedInsteadOfReportingAnEmptyDocxAsSuccess()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "no-text.docx");
        var document = new NormalizedDocument("blank", "en", [new NormalizedSection("page",
            [new NormalizedParagraph(" \t\r\n\0\u0001\u200b\U000e0001"), new NormalizedPageBreak(), new NormalizedParagraph("")])], []);

        await Assert.ThrowsAsync<InvalidDataException>(() => new PdfDocxWriter().WriteAsync(document, output));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task MixedTextAndEmptyPages_PreservesBlankPagesAndTheirPageBreaks()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "mixed.docx");
        var document = new NormalizedDocument("mixed", "en", [new NormalizedSection("pages",
            [new NormalizedParagraph(""), new NormalizedPageBreak(), new NormalizedParagraph("Editable text"),
                new NormalizedPageBreak(), new NormalizedParagraph("")])], []);

        await new PdfDocxWriter().WriteAsync(document, output);

        using var package = WordprocessingDocument.Open(output, false);
        var main = Assert.IsType<MainDocumentPart>(package.MainDocumentPart);
        var body = Assert.IsType<Body>(Assert.IsType<Document>(main.Document).Body);
        Assert.Equal(2, body.Descendants<Break>().Count(item => item.Type?.Value == BreakValues.Page));
        Assert.Equal("Editable text", body.InnerText);
        Assert.Equal(string.Empty, body.Elements<Paragraph>().First().InnerText);
        Assert.Equal(string.Empty, body.Elements<Paragraph>().Last().InnerText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validation_RejectsDamagedOrSchemaInvalidDocx(bool validPackage)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "invalid.docx");
        if (validPackage)
        {
            using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var part = package.AddMainDocumentPart();
            var paragraph = new Paragraph(new Run(new Text("content")));
            paragraph.SetAttribute(new OpenXmlAttribute("unexpected", "invalid", "urn:invalid", "1"));
            part.Document = new Document(new Body(paragraph));
            part.Document.Save();
        }
        else await File.WriteAllTextAsync(path, "not a ZIP package");

        await Assert.ThrowsAsync<InvalidDataException>(() => new PdfDocxWriter().ValidateAsync(path));
    }

    private static NormalizedDocument Document(string text, string title = "Title", string language = "en") =>
        new(title, language, [new NormalizedSection("page-1", [new NormalizedParagraph(text)])], []);

    private sealed class RecordingOcr : IPdfOcrService
    {
        public string? Password { get; private set; }
        public string? Language { get; private set; }
        public IReadOnlyList<int> Pages { get; private set; } = [];

        public Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(string pdfPath,
            IReadOnlyList<int> zeroBasedPageIndexes, string? languageTag, string? password, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Password = password;
            Language = languageTag;
            Pages = zeroBasedPageIndexes.ToArray();
            return Task.FromResult<IReadOnlyDictionary<int, string>>(zeroBasedPageIndexes.ToDictionary(
                index => index, index => $"扫描文字 第{index + 1}页"));
        }
    }

    private sealed class CancelAfterFirstBlock(CancellationTokenSource cancellation) : IReadOnlyList<NormalizedBlock>
    {
        public int Count => 2;
        public NormalizedBlock this[int index] => new NormalizedParagraph("text " + index);
        public IEnumerator<NormalizedBlock> GetEnumerator()
        {
            yield return this[0];
            cancellation.Cancel();
            yield return this[1];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void WriteTextPdf(string path)
    {
        const string first = "BT /F1 12 Tf 50 750 Td (First page editable text) Tj ET\n";
        const string second = "BT /F1 12 Tf 50 750 Td (Second page editable text) Tj ET\n";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(first)} >>\nstream\n{first}endstream",
            $"<< /Length {Encoding.ASCII.GetByteCount(second)} >>\nstream\n{second}endstream",
            "<< /Title (PDF title) >>"
        ];
        using var stream = File.Create(path);
        void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(stream.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info 8 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
