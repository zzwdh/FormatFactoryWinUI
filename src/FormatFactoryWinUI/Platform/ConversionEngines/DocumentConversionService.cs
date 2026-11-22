using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

/// <summary>Connects the document pipeline to Windows OCR, PDF rendering and image decoding.</summary>
public sealed class DocumentConversionService : IConversionEngine
{
    private readonly DocumentConversionPipeline _pipeline;

    public DocumentConversionService(ExternalToolConfiguration? configuration = null)
    {
        _pipeline = new DocumentConversionPipeline(new ExternalToolDiscovery(configuration).DiscoverAsync,
            new PdfContentExtractor(new WindowsPdfOcrService()),
            new WindowsPdfPageContentService().RenderPagesAsync, new WindowsImageContentService().DecodePagesAsync);
    }

    public string EngineId => _pipeline.EngineId;
    public bool CanHandle(string sourceFormatId, string targetFormatId) => _pipeline.CanHandle(sourceFormatId, targetFormatId);

    public Task<ConversionExecutionResult> ConvertAsync(ConversionRequest request,
        IProgress<ConversionExecutionProgress>? progress, CancellationToken cancellationToken) =>
        _pipeline.ConvertAsync(request, progress, cancellationToken);
}
