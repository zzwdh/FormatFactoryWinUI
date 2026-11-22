using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class ImageDocumentBuilder
{
    public NormalizedDocument Build(
        IReadOnlyList<DecodedImagePage> pages,
        string title,
        string language,
        bool fixedLayout)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException(LocalizationService.T("ConversionUiImagePageRequired"), nameof(pages));
        var resources = new List<NormalizedResource>(pages.Count);
        var sections = new List<NormalizedSection>(pages.Count);
        foreach (var page in pages) AddPage(page, fixedLayout, sections, resources);
        return new NormalizedDocument(title, language, sections, resources);
    }

    public async Task<NormalizedDocument> BuildAsync(
        IAsyncEnumerable<DecodedImagePage> pages,
        string title,
        string language,
        bool fixedLayout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var resources = new List<NormalizedResource>();
        var sections = new List<NormalizedSection>();
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddPage(page, fixedLayout, sections, resources);
        }
        if (sections.Count == 0) throw new ArgumentException(LocalizationService.T("ConversionUiImagePageRequired"), nameof(pages));
        return new NormalizedDocument(title, language, sections, resources);
    }

    private static void AddPage(DecodedImagePage page, bool fixedLayout,
        List<NormalizedSection> sections, List<NormalizedResource> resources)
    {
        var number = sections.Count + 1;
        var resourceId = $"page-image-{number}";
        if (page.PngPath is not null || page.PngContent.Length > 0)
            resources.Add(new NormalizedResource(resourceId, "image/png", $"page-{number:D4}.png", page.PngContent)
            {
                ContentPath = page.PngPath
            });
        var blocks = new List<NormalizedBlock>();
        if (fixedLayout || string.IsNullOrWhiteSpace(page.RecognizedText))
            blocks.Add(new NormalizedImage(resourceId,
                LocalizationService.T("ConversionUiImageFrame", page.SourceName, page.FrameIndex + 1)));
        if (!string.IsNullOrWhiteSpace(page.RecognizedText))
            blocks.Add(new NormalizedParagraph(page.RecognizedText.Trim()));
        sections.Add(new NormalizedSection($"page-{number}", blocks));
    }
}