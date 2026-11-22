using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

internal sealed class CalibreEbookArtifactValidator
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private readonly Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> _runProcess;

    public CalibreEbookArtifactValidator()
        : this(static (executable, arguments, timeout, token) =>
            ProcessRunner.RunAsync(executable, arguments, timeout, token))
    {
    }

    internal CalibreEbookArtifactValidator(
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> runProcess)
    {
        _runProcess = runProcess ?? throw new ArgumentNullException(nameof(runProcess));
    }

    public async Task<ArtifactValidationResult> ValidateAsync(
        ExternalToolInfo calibre,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calibre);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        cancellationToken.ThrowIfCancellationRequested();

        var artifact = Path.GetFullPath(artifactPath);
        if (!File.Exists(artifact) || new FileInfo(artifact).Length == 0)
        {
            return new ArtifactValidationResult(false, LocalizationService.T("ConversionUiCalibreNoReadableEbook"));
        }

        var extension = Path.GetExtension(artifact).ToLowerInvariant();
        if (extension is not (".mobi" or ".azw3" or ".epub"))
        {
            return new ArtifactValidationResult(false, LocalizationService.T("ConversionUiEbookFormatUnreadable", extension));
        }

        var metadataExecutable = FindMetadataExecutable(calibre.ExecutablePath);
        if (metadataExecutable is null)
        {
            return new ArtifactValidationResult(false, LocalizationService.T("ConversionUiCalibreEbookMetaNotFound"));
        }

        try
        {
            var result = await _runProcess(
                metadataExecutable,
                [artifact],
                MetadataTimeout,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                var details = FirstNonEmpty(result.StandardError, result.StandardOutput, LocalizationService.T("ConversionUiNoErrorDetails"));
                return new ArtifactValidationResult(false, LocalizationService.T("ConversionUiCalibreMetadataReadFailed", result.ExitCode, details));
            }

            if (!ContainsMetadata(result.StandardOutput))
            {
                return new ArtifactValidationResult(false, LocalizationService.T("ConversionUiCalibreMetadataUnrecognized"));
            }

            return new ArtifactValidationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ArtifactValidationResult(false, LocalizationService.T("ConversionUiCalibreMetadataReadException", ex.Message));
        }
    }

    internal static string? FindMetadataExecutable(string? converterExecutable)
    {
        if (string.IsNullOrWhiteSpace(converterExecutable)) return null;
        string? directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(converterExecutable));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var name in OperatingSystem.IsWindows()
                     ? new[] { "ebook-meta.exe", "ebook-meta" }
                     : new[] { "ebook-meta", "ebook-meta.exe" })
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && !PortableExecutableInspector.RequiresUnavailableX64Support(candidate)) return candidate;
        }

        return null;
    }

    internal static bool ContainsMetadata(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Split(
                new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line =>
            {
                var separator = line.IndexOf(':');
                return separator > 0 && separator < line.Length - 1 &&
                       !string.IsNullOrWhiteSpace(line[(separator + 1)..]);
            });
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value)).Trim();
}
