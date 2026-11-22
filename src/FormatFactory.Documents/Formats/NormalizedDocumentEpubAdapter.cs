using System.Xml.Linq;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class NormalizedDocumentEpubAdapter
{
    private static readonly XNamespace EpubNamespace = "http://www.idpf.org/2007/ops";

    public EpubPublication CreatePublication(
        NormalizedDocument document,
        string? identifier = null,
        bool fixedLayout = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var resources = document.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        var chapters = document.Sections.Select((section, index) =>
        {
            var title = section.Blocks.OfType<NormalizedHeading>().FirstOrDefault()?.Text
                ?? (document.Sections.Count == 1
                    ? document.Title
                    : LocalizationService.T("ConversionUiChapterNumber", index + 1));
            return new EpubChapter(
                $"chapter-{index + 1}",
                title,
                section.Blocks.Select(block => RenderBlock(block, resources)).ToList());
        }).ToList();
        if (chapters.Count == 0)
        {
            chapters.Add(new EpubChapter("chapter-1", document.Title, [new XElement("p")]));
        }

        return new EpubPublication(
            identifier ?? $"urn:uuid:{Guid.NewGuid():D}",
            document.Title,
            string.IsNullOrWhiteSpace(document.Language) ? "zh-CN" : document.Language,
            chapters,
            document.Resources.Select(resource =>
                new EpubResource(resource.Id, resource.FileName, resource.MediaType, resource.Content)
                {
                    ContentPath = resource.ContentPath
                }).ToList(),
            FixedLayout: fixedLayout);
    }

    private static XNode RenderBlock(NormalizedBlock block, IReadOnlyDictionary<string, NormalizedResource> resources) => block switch
    {
        NormalizedHeading value => new XElement($"h{Math.Clamp(value.Level, 1, 6)}", value.Text),
        NormalizedParagraph value => new XElement("p", value.Text),
        NormalizedList value => new XElement(
            value.Ordered ? "ol" : "ul",
            value.Items.Select(item => new XElement("li", item.Blocks.Select(child => RenderBlock(child, resources))))),
        NormalizedTable value => RenderTable(value, resources),
        NormalizedImage value => RenderImage(value, resources),
        NormalizedPageBreak => new XElement("div", new XAttribute("class", "page-break")),
        NormalizedFootnote value => new XElement(
            "aside",
            new XAttribute(XNamespace.Xmlns + "epub", EpubNamespace),
            new XAttribute(EpubNamespace + "type", "footnote"),
            new XAttribute("id", value.Id),
            value.Blocks.Select(child => RenderBlock(child, resources))),
        _ => new XElement("p")
    };

    private static XElement RenderTable(
        NormalizedTable table,
        IReadOnlyDictionary<string, NormalizedResource> resources)
    {
        var body = new XElement("tbody");
        foreach (var row in table.Rows)
        {
            var rowElement = new XElement("tr");
            foreach (var cell in row.Cells)
            {
                rowElement.Add(new XElement(
                    "td",
                    cell.ColumnSpan > 1 ? new XAttribute("colspan", cell.ColumnSpan) : null,
                    cell.RowSpan > 1 ? new XAttribute("rowspan", cell.RowSpan) : null,
                    cell.Blocks.Select(child => RenderBlock(child, resources))));
            }
            body.Add(rowElement);
        }
        return new XElement("table", body);
    }

    private static XElement RenderImage(NormalizedImage image, IReadOnlyDictionary<string, NormalizedResource> resources)
    {
        if (!resources.TryGetValue(image.ResourceId, out var resource))
        {
            return new XElement("p", image.AlternativeText ?? image.Caption ?? LocalizationService.T("ConversionUiImage"));
        }

        var result = new XElement(
            "figure",
            new XElement(
                "img",
                new XAttribute("src", $"assets/{resource.FileName.Replace('\\', '/')}"),
                new XAttribute("alt", image.AlternativeText ?? string.Empty)));
        if (!string.IsNullOrWhiteSpace(image.Caption)) result.Add(new XElement("figcaption", image.Caption));
        return result;
    }
}
