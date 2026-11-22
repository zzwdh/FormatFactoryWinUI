using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using PdfSharp.Pdf;

namespace FormatFactoryWinUI.Tests;

public sealed class DocumentContentPreservationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public async Task PdfExtractor_PreservesSparseSourceTextWhenOcrFindsNoText(string? ocrText)
    {
        using var directory = new TemporaryDirectory();
        var blankPath = Path.Combine(directory.Path, "blank.pdf");
        var inputPath = Path.Combine(directory.Path, "short-text.pdf");
        using (var pdf = new PdfDocument())
        {
            pdf.AddPage();
            pdf.Save(blankPath);
        }
        await new SearchablePdfWriter().WriteAsync(blankPath, ["A"], inputPath);
        var ocr = new FixedResultOcrService(ocrText);

        var result = await new PdfContentExtractor(ocr).ExtractAsync(inputPath);

        Assert.Equal([0], ocr.PageIndexes);
        Assert.Equal("A", Assert.Single(result.PageTexts));
        Assert.Equal("A", Assert.IsType<NormalizedParagraph>(Assert.Single(result.Document.Blocks)).Text);
    }

    [Fact]
    public async Task PdfExtractor_UsesNonemptyOcrResultForSparsePage()
    {
        using var directory = new TemporaryDirectory();
        var inputPath = Path.Combine(directory.Path, "blank.pdf");
        using (var pdf = new PdfDocument())
        {
            pdf.AddPage();
            pdf.Save(inputPath);
        }

        var result = await new PdfContentExtractor(new FixedResultOcrService(" recognized text "))
            .ExtractAsync(inputPath);

        Assert.Equal("recognized text", Assert.Single(result.PageTexts));
    }

    [Fact]
    public async Task PlainTextWriter_PreservesNestedTablesAndFootnotesInsideCells()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "nested.txt");
        var nestedTable = new NormalizedTable([
            new NormalizedTableRow([
                new NormalizedTableCell([new NormalizedParagraph("nested first cell")]),
                new NormalizedTableCell([
                    new NormalizedParagraph("nested second cell"),
                    new NormalizedFootnote("inner-note", [new NormalizedParagraph("inner footnote")])
                ])
            ])
        ]);
        var document = new NormalizedDocument("Nested content", "en", [
            new NormalizedSection("section", [
                new NormalizedTable([
                    new NormalizedTableRow([
                        new NormalizedTableCell([
                            new NormalizedParagraph("before nested content"),
                            nestedTable,
                            new NormalizedFootnote("outer-note", [new NormalizedParagraph("outer footnote")]),
                            new NormalizedParagraph("after nested content")
                        ])
                    ])
                ])
            ])
        ], []);

        await new PlainTextDocumentWriter().WriteAsync(document, outputPath);

        var text = await File.ReadAllTextAsync(outputPath);
        string[] expectedInOrder = [
            "before nested content", "nested first cell", "nested second cell",
            "inner footnote", "outer footnote", "after nested content"
        ];
        var previousIndex = -1;
        foreach (var expected in expectedInOrder)
        {
            var index = text.IndexOf(expected, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Missing or out-of-order content: {expected}");
            previousIndex = index;
        }
    }

    private sealed class FixedResultOcrService(string? text) : IPdfOcrService
    {
        public IReadOnlyList<int> PageIndexes { get; private set; } = [];

        public Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
            string pdfPath,
            IReadOnlyList<int> zeroBasedPageIndexes,
            string? languageTag,
            string? password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageIndexes = zeroBasedPageIndexes.ToArray();
            IReadOnlyDictionary<int, string> results = text is null
                ? new Dictionary<int, string>()
                : zeroBasedPageIndexes.ToDictionary(index => index, _ => text);
            return Task.FromResult(results);
        }
    }
}
