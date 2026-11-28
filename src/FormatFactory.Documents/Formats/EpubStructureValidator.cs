using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class EpubStructureValidator
{
    public async Task<EpubValidationResult> ValidateAsync(string epubPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epubPath);
        var report = await ConversionBackend.RequestAsync<Report>("epub-validate",
            new { path = Path.GetFullPath(epubPath) }, cancellationToken);
        return new(report.Errors.Length == 0, report.Errors.Select(Localize).ToArray(),
            report.Warnings.Select(Localize).ToArray(), report.PackagePath);
    }

    private static string Localize(Issue issue) => issue.Argument is not null
        ? LocalizationService.T(issue.Key, issue.Argument)
        : issue.Key == "ConversionUiOpfSpineMissingManifestId"
            ? LocalizationService.T(issue.Key, LocalizationService.T("ConversionUiEmptyValue"))
            : LocalizationService.T(issue.Key);

    private sealed record Issue(string Key, string? Argument);
    private sealed record Report(Issue[] Errors, Issue[] Warnings, string? PackagePath);
}
