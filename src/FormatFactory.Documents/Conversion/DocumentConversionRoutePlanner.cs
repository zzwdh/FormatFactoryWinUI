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
    private static readonly object CatalogLock = new();
    private static Dictionary<string, string[]>? _catalog;

    public bool CanPlan(string sourceFormatId, string targetFormatId)
    {
        var source = Normalize(sourceFormatId);
        if (IsImageFormat(source)) source = "image";
        // The UI queries many format pairs synchronously. One catalogue describes
        // the complete capabilities of this worker build, independent of installed tools.
        lock (CatalogLock)
        {
            try
            {
                _catalog ??= ConversionBackend.RequestAsync<Dictionary<string, string[]>>(
                    "document-route", new { kind = "catalog" }).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
            return _catalog.TryGetValue(source, out var targets) && targets.Contains(Normalize(targetFormatId));
        }
    }

    public ConversionPlan Plan(ConversionRequest request, IReadOnlyList<ExternalToolInfo> tools) =>
        PlanAsync(request, tools, CancellationToken.None).GetAwaiter().GetResult();

    internal async Task<ConversionPlan> PlanAsync(ConversionRequest request, IReadOnlyList<ExternalToolInfo> tools,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        request.Validate();
        ApplicationServices.DocumentPreservationPolicy.Validate(request);
        var sources = request.Inputs.Select(input => Normalize(input.FormatId)).Distinct(StringComparer.Ordinal).ToArray();
        var images = request.Inputs.All(input => IsImageFormat(input.FormatId));
        if (request.Inputs.Count > 1 && !images)
            throw new NotSupportedException(LocalizationService.T("ConversionUiSingleDocumentInputOnly"));
        var source = sources.Length == 1 ? sources[0] : "image";
        var route = await ConversionBackend.RequestAsync<Route>("document-route", new
        {
            kind = "plan", source, target = Normalize(request.TargetFormatId), image = images,
            @fixed = DocumentConversionOptions.IsFixedLayout(request),
            tools = tools.Where(tool => tool.IsAvailable).Select(tool => tool.Kind.ToString()).ToArray()
        }, token).ConfigureAwait(false);
        if (route.Error == "ConversionUiRouteNotImplemented")
            throw new NotSupportedException(LocalizationService.T(route.Error, source, request.TargetFormatId));
        if (route.Error == "DocumentCompatibilityFixedEpubOnly")
            throw new NotSupportedException(LocalizationService.T(route.Error));
        if (route.Error is not null)
            throw new DocumentEngineUnavailableException(LocalizationService.T(route.Error));

        IReadOnlyList<string> inputs = request.Inputs.Select((_, index) => $"input:{index}").ToArray();
        var steps = new List<ConversionStep>();
        foreach (var item in route.Steps)
        {
            var artifact = $"step:{steps.Count}";
            steps.Add(new(steps.Count, item.EngineId, item.Source, item.Target, inputs, artifact));
            inputs = [artifact];
        }
        var plan = new ConversionPlan(request, steps);
        plan.Validate();
        return plan;
    }

    internal static bool IsImageFormat(string formatId) =>
        (FormatCatalog.FindById(formatId) ?? FormatCatalog.FindByExtension("." + Normalize(formatId)))
        is { ContentKind: ContentKind.Image, CanImport: true };

    private static string Normalize(string formatId) =>
        formatId.Trim().TrimStart('.').ToLowerInvariant();

    private sealed record Route(RouteItem[] Steps, string? Error);
    private sealed record RouteItem(string EngineId, string Source, string Target);
}
