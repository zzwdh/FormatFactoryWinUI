using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace FormatFactoryWinUI.Documents;

public interface IPdfOcrService
{
    Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
        string pdfPath,
        IReadOnlyList<int> zeroBasedPageIndexes,
        string? languageTag,
        string? password,
        CancellationToken cancellationToken);
}

public interface IPdfOcrLayoutService : IPdfOcrService
{
    Task<IReadOnlyDictionary<int, OcrPageLayout>> RecognizePageLayoutsAsync(
        string pdfPath, IReadOnlyList<int> zeroBasedPageIndexes, string? languageTag,
        string? password, CancellationToken cancellationToken);
}

public sealed record PdfExtractionResult(
    NormalizedDocument Document,
    IReadOnlyList<string> PageTexts,
    IReadOnlyList<int> OcrPageIndexes)
{
    public IReadOnlyDictionary<int, OcrPageLayout> OcrLayouts { get; init; } =
        new Dictionary<int, OcrPageLayout>();
}

public sealed class PdfContentExtractor
{
    private readonly IPdfOcrService? _ocrService;

    public PdfContentExtractor(IPdfOcrService? ocrService = null) => _ocrService = ocrService;

    public async Task<PdfExtractionResult> ExtractAsync(
        string pdfPath,
        string? languageTag = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var path = Path.GetFullPath(pdfPath);
        if (!File.Exists(path)) throw FormatFailureDiagnostics.Attach(
            new FileNotFoundException(LocalizationService.T("ConversionUiPdfDocumentNotFound"), path),
            new(FormatFailureStage.PdfInputParse, FormatFailureReason.Missing) { Source = "pdf" });

        var extraction = await Task.Run(() => ExtractText(path, password, cancellationToken), cancellationToken);
        IReadOnlyDictionary<int, OcrPageLayout> layouts = new Dictionary<int, OcrPageLayout>();
        if (extraction.EmptyPages.Count > 0)
        {
            if (_ocrService is null)
                throw new OcrLanguageUnavailableException(LocalizationService.T("ConversionUiPdfNeedsUnavailableOcr"));
            IReadOnlyDictionary<int, string> ocr;
            if (_ocrService is IPdfOcrLayoutService layoutService)
            {
                layouts = await layoutService.RecognizePageLayoutsAsync(path, extraction.EmptyPages,
                    languageTag, password, cancellationToken);
                ocr = layouts.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
            }
            else ocr = await _ocrService.RecognizePagesAsync(
                path,
                extraction.EmptyPages,
                languageTag,
                password,
                cancellationToken);
            foreach (var page in extraction.EmptyPages)
            {
                // Sparse pages may already contain valid text such as a page number.
                // An empty or missing OCR result must not discard that source text.
                if (ocr.TryGetValue(page, out var recognizedText) && !string.IsNullOrWhiteSpace(recognizedText))
                    extraction.PageTexts[page] = recognizedText.Trim();
            }
        }

        var language = string.IsNullOrWhiteSpace(languageTag) ? "zh-CN" : languageTag;
        var sections = extraction.PageTexts.Select((text, index) =>
            new NormalizedSection(
                $"page-{index + 1}",
                index + 1 == extraction.PageTexts.Count
                    ? [new NormalizedParagraph(text)]
                    : [new NormalizedParagraph(text), new NormalizedPageBreak()]))
            .ToList();
        return new PdfExtractionResult(
            new NormalizedDocument(
                string.IsNullOrWhiteSpace(extraction.Title) ? Path.GetFileNameWithoutExtension(path) : extraction.Title,
                language, sections, []),
            extraction.PageTexts,
            extraction.EmptyPages) { OcrLayouts = layouts };
    }

    private static ExtractionState ExtractText(string path, string? password, CancellationToken token)
    {
        int? observedPages = null;
        try
        {
        var options = string.IsNullOrEmpty(password) ? null : new ParsingOptions { Password = password };
        using var pdf = options is null ? PdfDocument.Open(path) : PdfDocument.Open(path, options);
        observedPages = pdf.NumberOfPages;
        var texts = new List<string>(pdf.NumberOfPages);
        var empty = new List<int>();
        var index = 0;
        foreach (var page in pdf.GetPages())
        {
            token.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            texts.Add(text);
            if (!HasUsefulText(text)) empty.Add(index);
            index++;
        }
        if (texts.Count == 0) throw FormatFailureDiagnostics.Attach(
            new InvalidDataException(LocalizationService.T("ConversionUiPdfHasNoPages")),
            new(FormatFailureStage.PdfInputParse, FormatFailureReason.NoPages) { Source = "pdf", Pages = observedPages });
        return new ExtractionState(pdf.Information?.Title, texts, empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            FormatFailureDiagnostics.Record(error, new(FormatFailureStage.PdfInputParse, FormatFailureReason.ParseFailure)
                { Source = "pdf", Pages = observedPages });
            throw;
        }
    }

    private static bool HasUsefulText(string text) =>
        text.Count(character => char.IsLetterOrDigit(character) || character is >= '\u3400' and <= '\u9fff') >= 4;

    private sealed record ExtractionState(string? Title, List<string> PageTexts, List<int> EmptyPages);
}
