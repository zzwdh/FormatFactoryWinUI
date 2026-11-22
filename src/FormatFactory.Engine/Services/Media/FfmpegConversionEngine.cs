using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ApplicationServices;

namespace FormatFactoryWinUI.Services;

/// <summary>
/// Adapts the established FFmpeg media converter to the engine-neutral v2
/// conversion contract.
/// </summary>
public sealed class FfmpegConversionEngine : IConversionEngine
{
    private readonly ConversionService _conversionService;
    private readonly string _ffmpegPath;
    private readonly AppSettings _settings;

    public FfmpegConversionEngine(
        string ffmpegPath,
        AppSettings settings,
        ConversionService? conversionService = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = Path.GetFullPath(ffmpegPath);
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Snapshot();
        _conversionService = conversionService ?? new ConversionService();
    }

    public string EngineId => "ffmpeg";

    public bool CanHandle(string sourceFormatId, string targetFormatId)
    {
        var source = FormatCatalog.FindById(sourceFormatId);
        var target = FormatCatalog.FindById(targetFormatId);
        return source is not null &&
               target is not null &&
               source.ContentKind is ContentKind.Video or ContentKind.Audio or ContentKind.Image &&
               target.ContentKind is ContentKind.Video or ContentKind.Audio or ContentKind.Image &&
               string.Equals(target.DefaultEngineId, EngineId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ConversionExecutionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Inputs.Count != 1)
        {
            return Failed(LocalizationService.T("GeneralServiceSingleInputOnly"));
        }

        var input = request.Inputs[0];
        if (!CanHandle(input.FormatId, request.TargetFormatId))
        {
            return Failed(LocalizationService.T("GeneralServiceUnsupportedRoute", input.FormatId, request.TargetFormatId));
        }

        var target = FormatCatalog.FindById(request.TargetFormatId)!;
        var task = ConversionRequestFactory.CreateMediaTask(request, EngineId);

        var result = await _conversionService.ConvertAsync(
            task,
            _ffmpegPath,
            ConversionRequestFactory.OutputSettings(_settings, request),
            value => progress?.Report(new ConversionExecutionProgress(
                value.Percent,
                "ffmpeg",
                value.Message,
                value.Speed,
                value.Remaining)),
            cancellationToken);
        return ConversionRequestFactory.ToExecutionResult(result, target.Id);
    }

    private static ConversionExecutionResult Failed(string message) =>
        new(false, false, [], message);
}
