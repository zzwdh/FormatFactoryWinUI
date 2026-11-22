using System.Runtime.CompilerServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Composes Office PDF export with the existing rendered-page EPUB pipeline.</summary>
public sealed class OfficeFixedLayoutEpubConverter(
    ImageDocumentBuilder documentBuilder,
    NormalizedDocumentEpubAdapter adapter,
    Epub3Writer writer)
{
    public async Task ConvertAsync(
        string inputPath,
        string outputPath,
        string workspacePath,
        string? language,
        string? title,
        Func<string, string, CancellationToken, Task> exportPdf,
        Func<string, CancellationToken, IAsyncEnumerable<DecodedImagePage>> renderPdf,
        IProgress<ConversionExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pdfPath = Path.Combine(workspacePath, "office-layout.pdf");
        await exportPdf(inputPath, pdfPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ConversionExecutionProgress(45, "pdf-render",
            LocalizationService.T("ConversionUiRenderingPdfPages")));
        var viewports = new List<(int Width, int Height)>();
        async IAsyncEnumerable<DecodedImagePage> Pages([EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var page in renderPdf(pdfPath, token).WithCancellation(token).ConfigureAwait(false))
            {
                viewports.Add(((int)Math.Clamp(page.Width, 1u, (uint)int.MaxValue),
                    (int)Math.Clamp(page.Height, 1u, (uint)int.MaxValue)));
                yield return page with { SourceName = Path.GetFileName(inputPath) };
            }
        }
        var document = await documentBuilder.BuildAsync(
            Pages(cancellationToken),
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(inputPath) : title,
            string.IsNullOrWhiteSpace(language) ? "zh-CN" : language,
            fixedLayout: true,
            cancellationToken);
        var publication = adapter.CreatePublication(document, fixedLayout: true);
        publication = publication with
        {
            Chapters = publication.Chapters.Select((chapter, index) => chapter with
            {
                ViewportWidth = viewports[index].Width,
                ViewportHeight = viewports[index].Height
            }).ToList()
        };
        progress?.Report(new ConversionExecutionProgress(65, "epub",
            LocalizationService.T("ConversionUiGeneratingEpub")));
        await writer.WriteAsync(publication, outputPath, cancellationToken);
    }
}
