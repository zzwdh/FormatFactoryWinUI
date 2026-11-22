using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

public sealed class NativeImageConversionEngine(NativeImageConversionService service, string ffmpegPath, AppSettings settings)
    : IConversionEngine
{
    private readonly AppSettings _settings = settings.Snapshot();
    public string EngineId => "windows-image";
    public bool CanHandle(string sourceFormatId, string targetFormatId) =>
        FormatCatalog.FindById(sourceFormatId)?.ContentKind == ContentKind.Image &&
        FormatCatalog.FindById(targetFormatId)?.ContentKind == ContentKind.Image;

    public async Task<ConversionExecutionResult> ConvertAsync(ConversionRequest request,
        IProgress<ConversionExecutionProgress>? progress, CancellationToken cancellationToken)
    {
        var result = await service.ConvertAsync(ConversionRequestFactory.CreateMediaTask(request, EngineId),
            ffmpegPath, ConversionRequestFactory.OutputSettings(_settings, request),
            value => progress?.Report(new(value.Percent, EngineId, value.Message, value.Speed, value.Remaining)),
            cancellationToken).ConfigureAwait(false);
        return ConversionRequestFactory.ToExecutionResult(result, request.TargetFormatId);
    }
}

public sealed class DiscConversionEngine(DiscConversionService service, string ffmpegPath, AppSettings settings)
    : IConversionEngine
{
    private readonly AppSettings _settings = settings.Snapshot();
    public string EngineId => DiscConversionService.EngineId;
    public bool CanHandle(string sourceFormatId, string targetFormatId) =>
        OpticalDiscFormatIds.IsPhysicalDisc(sourceFormatId) ||
        FormatCatalog.FindById(sourceFormatId)?.ContentKind == ContentKind.Disc;

    public async Task<ConversionExecutionResult> ConvertAsync(ConversionRequest request,
        IProgress<ConversionExecutionProgress>? progress, CancellationToken cancellationToken)
    {
        var result = await service.ConvertAsync(ConversionRequestFactory.CreateMediaTask(request, EngineId),
            ConversionRequestFactory.OutputSettings(_settings, request), ffmpegPath,
            value => progress?.Report(new(value.Percent, EngineId, value.Message, value.Speed, value.Remaining)),
            cancellationToken).ConfigureAwait(false);
        return ConversionRequestFactory.ToExecutionResult(result, request.TargetFormatId);
    }
}
