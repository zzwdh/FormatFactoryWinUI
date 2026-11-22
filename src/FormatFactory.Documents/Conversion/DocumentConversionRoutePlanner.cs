using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

public static class DocumentEngineIds
{
    public const string Pipeline = "document-pipeline";
    public const string DocxReader = "docx-reader";
    public const string DocxWriter = "docx-writer";
    public const string TextWriter = "text-writer";
    public const string MarkdownWriter = "markdown-writer";
    public const string EpubWriter = "epub-writer";
    public const string PdfExtractor = "pdf-extractor";
    public const string PdfRenderer = "pdf-renderer";
    public const string PdfOcr = "pdf-ocr";
    public const string ImageDecoder = "image-decoder";
    public const string ImageOcr = "image-ocr";
    public const string ImagePdf = "image-pdf";
    public const string WindowsFilter = "windows-ifilter";
}

public sealed class DocumentEngineUnavailableException(string message) : InvalidOperationException(message);

public sealed class DocumentConversionRoutePlanner
{
    private static readonly HashSet<string> CajFormats = ["caj", "hn", "c8", "kdh"];

    public bool CanPlan(string sourceFormatId, string targetFormatId) =>
        TryBuild(sourceFormatId, targetFormatId, [], requireAvailableTools: false, out _);

    public ConversionPlan Plan(ConversionRequest request, IReadOnlyList<ExternalToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        request.Validate();
        ApplicationServices.DocumentPreservationPolicy.Validate(request);
        var sourceFormats = request.Inputs
            .Select(input => Normalize(input.FormatId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var isImageCollection = request.Inputs.All(input => IsImageFormat(input.FormatId));
        if (request.Inputs.Count > 1 && !isImageCollection)
        {
            throw new NotSupportedException(LocalizationService.T("ConversionUiSingleDocumentInputOnly"));
        }
        var source = sourceFormats.Count == 1
            ? sourceFormats[0]
            : isImageCollection
                ? "image"
                : throw new NotSupportedException(LocalizationService.T("ConversionUiSingleInputFormatOnly"));
        if (!TryBuild(source, request.TargetFormatId, tools, true, out var route,
            DocumentConversionOptions.IsFixedLayout(request)))
            throw new NotSupportedException(LocalizationService.T("ConversionUiRouteNotImplemented", source, request.TargetFormatId));

        var inputIds = request.Inputs.Select((_, index) => $"input:{index}").ToList();
        var steps = new List<ConversionStep>();
        var currentInputs = (IReadOnlyList<string>)inputIds;
        for (var order = 0; order < route.Count; order++)
        {
            var item = route[order];
            var artifactId = $"step:{order}";
            steps.Add(new ConversionStep(order, item.EngineId, item.Source, item.Target, currentInputs, artifactId));
            currentInputs = [artifactId];
        }
        var plan = new ConversionPlan(request, steps);
        plan.Validate();
        return plan;
    }

    private static bool TryBuild(
        string source,
        string target,
        IReadOnlyList<ExternalToolInfo> tools,
        bool requireAvailableTools,
        out IReadOnlyList<RouteItem> route,
        bool fixedLayout = false)
    {
        source = Normalize(source);
        target = Normalize(target);
        var result = new List<RouteItem>();

        bool Require(ExternalToolKind kind)
        {
            if (!requireAvailableTools) return true;
            if (tools.Any(tool => tool.Kind == kind && tool.IsAvailable)) return true;
            throw new DocumentEngineUnavailableException(kind switch
            {
                ExternalToolKind.MicrosoftWord => LocalizationService.T("ConversionUiRequiresMicrosoftWord"),
                ExternalToolKind.LibreOffice => LocalizationService.T("ConversionUiRequiresLibreOffice"),
                ExternalToolKind.Calibre => LocalizationService.T("ConversionUiRequiresCalibre"),
                ExternalToolKind.Caj2Pdf => LocalizationService.T("ConversionUiRequiresCaj2Pdf"),
                _ => LocalizationService.T("ConversionUiMissingConversionEngine")
            });
        }

        string OfficeEngine()
        {
            if (!requireAvailableTools) return ExternalEngineIds.Word;
            if (tools.Any(tool => tool.Kind == ExternalToolKind.MicrosoftWord && tool.IsAvailable)) return ExternalEngineIds.Word;
            if (tools.Any(tool => tool.Kind == ExternalToolKind.LibreOffice && tool.IsAvailable)) return ExternalEngineIds.LibreOffice;
            throw new DocumentEngineUnavailableException(LocalizationService.T("ConversionUiDocToPdfEngineRequired"));
        }

        if (source == "docx" && fixedLayout && target is "mobi" or "azw3")
            throw new NotSupportedException(LocalizationService.T("DocumentCompatibilityFixedEpubOnly"));

        if (source == "docx" && target == "epub" && fixedLayout)
        {
            result.Add(new RouteItem(OfficeEngine(), "docx", "pdf"));
            result.Add(new RouteItem(DocumentEngineIds.PdfRenderer, "pdf", "normalized-document"));
            result.Add(new RouteItem(DocumentEngineIds.EpubWriter, "normalized-document", "epub"));
        }
        else if (source == "docx" && target is "txt" or "md" or "epub")
        {
            result.Add(new RouteItem(DocumentEngineIds.DocxReader, "docx", "normalized-document"));
            result.Add(new RouteItem(NormalizedWriter(target), "normalized-document", target));
        }
        else if (source is "doc" or "docx" && target == "pdf")
        {
            result.Add(new RouteItem(OfficeEngine(), source, "pdf"));
        }
        else if (source == "doc" && target is "txt" or "md" or "epub")
        {
            result.Add(new RouteItem(DocumentEngineIds.WindowsFilter, "doc", "normalized-document"));
            result.Add(new RouteItem(NormalizedWriter(target), "normalized-document", target));
        }
        else if (source == "epub" && target is "mobi" or "azw3")
        {
            Require(ExternalToolKind.Calibre);
            result.Add(new RouteItem(ExternalEngineIds.Calibre, "epub", target));
        }
        else if (CajFormats.Contains(source) && target == "pdf")
        {
            Require(ExternalToolKind.Caj2Pdf);
            result.Add(new RouteItem(ExternalEngineIds.Caj2Pdf, source, "pdf"));
        }
        else if (source == "pdf" && target is "mobi" or "azw3")
        {
            Require(ExternalToolKind.Calibre);
            result.Add(new RouteItem(DocumentEngineIds.PdfExtractor, "pdf", "normalized-document"));
            result.Add(new RouteItem(DocumentEngineIds.EpubWriter, "normalized-document", "epub"));
            result.Add(new RouteItem(ExternalEngineIds.Calibre, "epub", target));
        }
        else if (source == "docx" && target is "mobi" or "azw3")
        {
            Require(ExternalToolKind.Calibre);
            result.Add(new RouteItem(DocumentEngineIds.DocxReader, "docx", "normalized-document"));
            result.Add(new RouteItem(DocumentEngineIds.EpubWriter, "normalized-document", "epub"));
            result.Add(new RouteItem(ExternalEngineIds.Calibre, "epub", target));
        }
        else if (source == "doc" && target is "mobi" or "azw3")
        {
            Require(ExternalToolKind.Calibre);
            result.Add(new RouteItem(DocumentEngineIds.WindowsFilter, "doc", "normalized-document"));
            result.Add(new RouteItem(DocumentEngineIds.EpubWriter, "normalized-document", "epub"));
            result.Add(new RouteItem(ExternalEngineIds.Calibre, "epub", target));
        }
        else if (source == "pdf" && target is "pdf" or "docx" or "txt" or "md" or "epub")
        {
            result.Add(new RouteItem(target == "pdf" ? DocumentEngineIds.PdfOcr : DocumentEngineIds.PdfExtractor, "pdf", target == "pdf" ? "pdf" : "normalized-document"));
            if (target == "docx") result.Add(new RouteItem(DocumentEngineIds.DocxWriter, "normalized-document", "docx"));
            if (target == "txt") result.Add(new RouteItem(DocumentEngineIds.TextWriter, "normalized-document", "txt"));
            if (target == "md") result.Add(new RouteItem(DocumentEngineIds.MarkdownWriter, "normalized-document", "md"));
            if (target == "epub") result.Add(new RouteItem(DocumentEngineIds.EpubWriter, "normalized-document", "epub"));
        }
        else if ((source == "image" || IsImageFormat(source)) && target is "pdf" or "txt" or "md" or "epub" or "mobi" or "azw3")
        {
            result.Add(new RouteItem(
                target == "pdf" ? DocumentEngineIds.ImageDecoder : DocumentEngineIds.ImageOcr,
                source,
                target == "pdf" ? "image-pages" : "normalized-document"));
            if (target == "pdf") result.Add(new RouteItem(DocumentEngineIds.ImagePdf, "image-pages", "pdf"));
            else
            {
                result.Add(new RouteItem(
                    target is "txt" or "md" ? NormalizedWriter(target) : DocumentEngineIds.EpubWriter,
                    "normalized-document",
                    target is "txt" or "md" ? target : "epub"));
                if (target is "mobi" or "azw3")
                {
                    Require(ExternalToolKind.Calibre);
                    result.Add(new RouteItem(ExternalEngineIds.Calibre, "epub", target));
                }
            }
        }
        else if (CajFormats.Contains(source) && target is "txt" or "md" or "epub" or "mobi" or "azw3")
        {
            Require(ExternalToolKind.Caj2Pdf);
            result.Add(new RouteItem(ExternalEngineIds.Caj2Pdf, source, "pdf"));
            result.Add(new RouteItem(DocumentEngineIds.PdfExtractor, "pdf", "normalized-document"));
            result.Add(new RouteItem(
                target is "txt" or "md" ? NormalizedWriter(target) : DocumentEngineIds.EpubWriter,
                "normalized-document",
                target is "txt" or "md" ? target : "epub"));
            if (target is "mobi" or "azw3")
            {
                Require(ExternalToolKind.Calibre);
                result.Add(new RouteItem(ExternalEngineIds.Calibre, "epub", target));
            }
        }
        else
        {
            route = [];
            return false;
        }

        route = result;
        return true;
    }

    internal static bool IsImageFormat(string formatId) =>
        (FormatCatalog.FindById(formatId) ?? FormatCatalog.FindByExtension("." + Normalize(formatId)))
        is { ContentKind: ContentKind.Image, CanImport: true };

    private static string NormalizedWriter(string target) => target switch
    {
        "txt" => DocumentEngineIds.TextWriter,
        "md" => DocumentEngineIds.MarkdownWriter,
        "epub" => DocumentEngineIds.EpubWriter,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, LocalizationService.T("ConversionUiNotNormalizedDocumentFormat"))
    };

    private static string Normalize(string formatId) =>
        formatId.Trim().TrimStart('.').ToLowerInvariant();

    private sealed record RouteItem(string EngineId, string Source, string Target);
}
