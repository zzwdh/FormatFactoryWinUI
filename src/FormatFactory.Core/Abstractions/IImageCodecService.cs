using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Host image codecs. A null probe means the installed codecs cannot decode this file.</summary>
public interface IImageCodecService
{
    Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default);
    bool CanEncode(string targetFormatId);
    Task TranscodeAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, CancellationToken cancellationToken = default);

    /// <summary>Proves that every oriented page retains its original samples, alpha and color profile.</summary>
    /// <remarks>Codecs without a verifier cannot claim a lossless conversion.</remarks>
    Task<bool> ValidateLosslessAsync(string inputPath, string outputPath,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    Task TranscodeWithColorPolicyAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, string colorPolicy, CancellationToken cancellationToken = default) =>
        colorPolicy.Equals("srgb", StringComparison.OrdinalIgnoreCase)
            ? TranscodeAsync(inputPath, outputPath, targetFormatId, mode, cancellationToken)
            : throw new NotSupportedException(LocalizationService.T("NativeImagePreserveUnsupported"));

    /// <summary>Optional extension for quality and sizing; legacy codecs retain their existing contract.</summary>
    Task TranscodeWithOptionsAsync(string inputPath, string outputPath, string targetFormatId,
        string mode, string colorPolicy, MediaConversionOptions options, CancellationToken cancellationToken = default) =>
        options.ImageQuality.HasValue || options.HasDimensions
            ? throw new NotSupportedException(LocalizationService.T("MediaOptionsTargetUnsupported"))
            : TranscodeWithColorPolicyAsync(inputPath, outputPath, targetFormatId, mode, colorPolicy, cancellationToken);
}
