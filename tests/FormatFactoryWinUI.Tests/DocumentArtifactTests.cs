using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using PdfSharp.Pdf;

namespace FormatFactoryWinUI.Tests;

public sealed class DocumentArtifactTests
{
    [Fact]
    public async Task DocxReader_OpensSdkPackageAndExtractsHeadingParagraphAndTable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "示例.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var part = document.AddMainDocumentPart();
            part.Document = new Document(new Body(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text("章节标题"))),
                new Paragraph(new Run(new Text("Unicode 正文内容"))),
                new Table(
                    new TableProperties(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto }),
                    new TableGrid(new GridColumn(), new GridColumn()),
                    new TableRow(
                        new TableCell(new Paragraph(new Run(new Text("甲")))),
                        new TableCell(new Paragraph(new Run(new Text("乙"))))))));
            var styles = part.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(new Style(
                new StyleName { Val = "heading 1" },
                new BasedOn { Val = "Normal" },
                new UIPriority { Val = 9 },
                new PrimaryStyle(),
                new StyleParagraphProperties(new OutlineLevel { Val = 0 }))
            { Type = StyleValues.Paragraph, StyleId = "Heading1" });
            part.Document.Save();
            styles.Styles.Save();
        }

        var result = await new DocxDocumentReader().ReadAsync(path);

        Assert.Contains(result.Blocks, block => block is NormalizedHeading { Text: "章节标题", Level: 1 });
        Assert.Contains(result.Blocks, block => block is NormalizedParagraph { Text: "Unicode 正文内容" });
        var table = Assert.Single(result.Blocks.OfType<NormalizedTable>());
        Assert.Equal(2, table.Rows[0].Cells.Count);
    }

    [Fact]
    public async Task DocxReader_PreservesNestedContentControlsAndTheirDocumentOrder()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "content-controls.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var part = document.AddMainDocumentPart();
            part.Document = new Document(new Body(
                new Paragraph(new Run(new Text("before"))),
                new SdtBlock(new SdtContentBlock(
                    new Paragraph(new Run(new Text("inside"))),
                    new SdtBlock(new SdtContentBlock(new Paragraph(new Run(new Text("nested"))))))),
                new Paragraph(new Run(new Text("after")))));
            part.Document.Save();
        }

        var result = await new DocxDocumentReader().ReadAsync(path);

        Assert.Equal(new[] { "before", "inside", "nested", "after" },
            result.Blocks.OfType<NormalizedParagraph>().Select(paragraph => paragraph.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocxReader_ResolvesImageRelationshipTargets(bool absolute)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "absolute-image.docx");
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        string imageTarget;
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var part = document.AddMainDocumentPart();
            part.Document = new Document(new Body(new Paragraph(new Run(new Text("image document")))));
            var image = part.AddImagePart(ImagePartType.Png);
            using var source = new MemoryStream(imageBytes);
            image.FeedData(source);
            imageTarget = image.Uri.OriginalString;
            part.Document.Save();
        }
        if (absolute)
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var relationships = archive.GetEntry("word/_rels/document.xml.rels")!;
            XDocument xml;
            using (var stream = relationships.Open()) xml = XDocument.Load(stream);
            xml.Root!.Elements().Single(element =>
                ((string?)element.Attribute("Type"))?.EndsWith("/image", StringComparison.Ordinal) == true)
                .SetAttributeValue("Target", imageTarget);
            relationships.Delete();
            using var replacement = archive.CreateEntry("word/_rels/document.xml.rels").Open();
            xml.Save(replacement);
        }

        var result = await new DocxDocumentReader().ReadAsync(path);

        Assert.Equal(imageBytes, Assert.Single(result.Resources).Content.ToArray());
    }

    [Fact]
    public async Task PlainTextWriter_UsesUtf8WithoutBom()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "结果.txt");
        var document = SampleDocument();

        await new PlainTextDocumentWriter().WriteAsync(document, path);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Contains("中文正文", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainTextWriter_CancellationDoesNotCommitOutputOrLeaveStagingFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "取消.txt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlainTextDocumentWriter().WriteAsync(SampleDocument(), path, cancellation.Token));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task MarkdownWriter_PreservesStructureAndWritesResourcesAsUtf8()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "结果.md");
        var document = new NormalizedDocument(
            "Markdown 测试",
            "zh-CN",
            [new NormalizedSection("chapter-1", [
                new NormalizedHeading(2, "章节"),
                new NormalizedParagraph("中文正文 and Unicode ✓"),
                new NormalizedList(false, [
                    new NormalizedListItem([new NormalizedParagraph("第一项")]),
                    new NormalizedListItem([new NormalizedParagraph("第二项")])
                ]),
                new NormalizedTable([
                    new NormalizedTableRow([
                        new NormalizedTableCell([new NormalizedParagraph("名称")]),
                        new NormalizedTableCell([new NormalizedParagraph("值")])
                    ]),
                    new NormalizedTableRow([
                        new NormalizedTableCell([new NormalizedParagraph("语言")]),
                        new NormalizedTableCell([new NormalizedParagraph("中文")])
                    ])
                ]),
                new NormalizedImage("cover", "封面", "图片说明"),
                new NormalizedPageBreak(),
                new NormalizedFootnote("note-1", [new NormalizedParagraph("脚注内容")])
            ])],
            [new NormalizedResource("cover", "image/png", "封面 image.png", new byte[] { 1, 2, 3, 4 })]);

        await new MarkdownDocumentWriter().WriteAsync(document, path);

        var bytes = await File.ReadAllBytesAsync(path);
        var markdown = Encoding.UTF8.GetString(bytes);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Contains("# Markdown 测试", markdown, StringComparison.Ordinal);
        Assert.Contains("## 章节", markdown, StringComparison.Ordinal);
        Assert.Contains("- 第一项", markdown, StringComparison.Ordinal);
        Assert.Contains("| 名称 | 值 |", markdown, StringComparison.Ordinal);
        Assert.Contains("![封面](", markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- page-break -->", markdown, StringComparison.Ordinal);
        Assert.Contains("[^note-1]", markdown, StringComparison.Ordinal);
        Assert.Contains("[^note-1]:", markdown, StringComparison.Ordinal);
        Assert.Contains("脚注内容", markdown, StringComparison.Ordinal);
        var assetPath = Path.Combine(directory.Path, "结果.assets", "封面 image.png");
        Assert.True(File.Exists(assetPath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(assetPath));
    }

    [Fact]
    public async Task MarkdownWriter_CancellationDoesNotCommitMarkdownOrAssets()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "取消.md");
        var document = new NormalizedDocument(
            "取消测试",
            "zh-CN",
            [new NormalizedSection(null, [new NormalizedParagraph("正文")])],
            [new NormalizedResource("image", "image/png", "image.png", new byte[] { 1 })]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MarkdownDocumentWriter().WriteAsync(document, path, cancellation.Token));

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "取消.assets")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task EpubWriter_ProducesStructurallyValidEpub3()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "结果.epub");
        var publication = new NormalizedDocumentEpubAdapter().CreatePublication(SampleDocument(), "urn:test:document");

        await new Epub3Writer().WriteAsync(publication, path);
        var validation = await new EpubStructureValidator().ValidateAsync(path);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        using var archive = ZipFile.OpenRead(path);
        Assert.Equal("mimetype", archive.Entries[0].FullName);
        Assert.Contains(archive.Entries, entry => entry.FullName == "EPUB/package.opf");
    }

    [Fact]
    public async Task EpubWriter_DeclaresPrePaginatedLayoutForFixedPages()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "固定版式.epub");
        var publication = new NormalizedDocumentEpubAdapter().CreatePublication(
            SampleDocument(),
            "urn:test:fixed",
            fixedLayout: true);

        await new Epub3Writer().WriteAsync(publication, path);

        using var archive = ZipFile.OpenRead(path);
        var package = archive.GetEntry("EPUB/package.opf");
        Assert.NotNull(package);
        using var stream = package!.Open();
        var xml = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
        Assert.Contains(xml.Descendants(), element =>
            element.Name.LocalName == "meta" &&
            (string?)element.Attribute("property") == "rendition:layout" &&
            element.Value == "pre-paginated");

        var chapter = archive.GetEntry("EPUB/chapter-0001.xhtml");
        Assert.NotNull(chapter);
        await using var chapterStream = chapter!.Open();
        var chapterXml = await XDocument.LoadAsync(chapterStream, LoadOptions.None, CancellationToken.None);
        var viewport = Assert.Single(chapterXml.Descendants(), element =>
            element.Name.LocalName == "meta" &&
            (string?)element.Attribute("name") == "viewport");
        Assert.Equal("width=1200,height=1600", (string?)viewport.Attribute("content"));
    }

    [Fact]
    public async Task EpubValidator_RejectsFixedLayoutChapterWithoutViewport()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "缺少视口.epub");
        var publication = new NormalizedDocumentEpubAdapter().CreatePublication(
            SampleDocument(),
            "urn:test:missing-viewport",
            fixedLayout: true);
        await new Epub3Writer().WriteAsync(publication, path);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var chapter = archive.GetEntry("EPUB/chapter-0001.xhtml");
            Assert.NotNull(chapter);
            XDocument chapterXml;
            using (var stream = chapter!.Open()) chapterXml = XDocument.Load(stream);
            chapterXml.Descendants()
                .First(element =>
                    element.Name.LocalName == "meta" &&
                    (string?)element.Attribute("name") == "viewport")
                .Remove();
            chapter.Delete();
            var replacement = archive.CreateEntry("EPUB/chapter-0001.xhtml", CompressionLevel.Optimal);
            using var replacementStream = replacement.Open();
            chapterXml.Save(replacementStream);
        }

        var validation = await new EpubStructureValidator().ValidateAsync(path);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("viewport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PdfValidator_ReloadsGeneratedPdfAndChecksPageCount()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "结果.pdf");
        using (var pdf = new PdfDocument())
        {
            pdf.AddPage();
            pdf.AddPage();
            pdf.Save(path);
        }

        var result = await new PdfArtifactValidator().ValidateAsync(
            new ConversionArtifact("output", "pdf", path), CancellationToken.None);

        Assert.True(result.IsValid, result.Message);
        using var reloaded = UglyToad.PdfPig.PdfDocument.Open(path);
        Assert.Equal(2, reloaded.NumberOfPages);
    }

    [Fact]
    public async Task PdfExtractor_OpensPasswordProtectedPdfAndPassesPasswordToOcr()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "加密.pdf");
        WriteEncryptedBlankPdf(path, pageCount: 2);
        var ocr = new RecordingPdfOcrService();

        var result = await new PdfContentExtractor(ocr).ExtractAsync(
            path,
            "zh-Hans",
            "owner-secret");

        Assert.Equal("owner-secret", ocr.Password);
        Assert.Equal("zh-Hans", ocr.LanguageTag);
        Assert.Equal([0, 1], ocr.PageIndexes);
        Assert.Equal(2, result.PageTexts.Count);
        Assert.All(result.PageTexts, text => Assert.Contains("OCR page", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchablePdfWriter_PreservesEncryptedSourcePagesAndAddsOcrText()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "加密源.pdf");
        var output = Path.Combine(directory.Path, "可搜索.pdf");
        WriteEncryptedBlankPdf(input, pageCount: 2);

        await new SearchablePdfWriter().WriteAsync(
            input,
            ["OCR page one", "OCR page two"],
            output,
            "owner-secret");

        using var reloaded = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(2, reloaded.NumberOfPages);
        var extracted = string.Join(Environment.NewLine, reloaded.GetPages().Select(page => page.Text));
        Assert.Contains("OCR page one", extracted, StringComparison.Ordinal);
        Assert.Contains("OCR page two", extracted, StringComparison.Ordinal);
    }

    private static void WriteEncryptedBlankPdf(string path, int pageCount)
    {
        using var pdf = new PdfDocument();
        for (var index = 0; index < pageCount; index++) pdf.AddPage();
        pdf.SecuritySettings.UserPassword = "user-secret";
        pdf.SecuritySettings.OwnerPassword = "owner-secret";
        pdf.SecuritySettings.PermitExtractContent = true;
        pdf.Save(path);
    }

    private sealed class RecordingPdfOcrService : IPdfOcrService
    {
        public IReadOnlyList<int> PageIndexes { get; private set; } = [];
        public string? LanguageTag { get; private set; }
        public string? Password { get; private set; }

        public Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
            string pdfPath,
            IReadOnlyList<int> zeroBasedPageIndexes,
            string? languageTag,
            string? password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageIndexes = zeroBasedPageIndexes.ToArray();
            LanguageTag = languageTag;
            Password = password;
            IReadOnlyDictionary<int, string> result = zeroBasedPageIndexes.ToDictionary(
                index => index,
                index => $"OCR page {index + 1}");
            return Task.FromResult(result);
        }
    }

    private static NormalizedDocument SampleDocument() => new(
        "测试文档",
        "zh-CN",
        [new NormalizedSection("chapter-1", [
            new NormalizedHeading(1, "第一章"),
            new NormalizedParagraph("中文正文 and Unicode ✓"),
            new NormalizedTable([new NormalizedTableRow([
                new NormalizedTableCell([new NormalizedParagraph("单元格")])
            ])])
        ])],
        []);
}
