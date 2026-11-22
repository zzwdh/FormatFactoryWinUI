using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public interface IContentProbe
{
    string ProbeId { get; }
    bool CanProbe(FormatDescriptor format);
    Task<ContentProbeResult> ProbeAsync(ConversionInput input, CancellationToken cancellationToken);
}

public interface IConversionEngine
{
    string EngineId { get; }
    bool CanHandle(string sourceFormatId, string targetFormatId);
    Task<ConversionExecutionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IArtifactValidator
{
    string ValidatorId { get; }
    bool CanValidate(string formatId);
    Task<ArtifactValidationResult> ValidateAsync(
        ConversionArtifact artifact,
        CancellationToken cancellationToken);
}

public interface IEngineDiscovery
{
    Task<IReadOnlyList<EngineAvailability>> DiscoverAsync(CancellationToken cancellationToken);
}
