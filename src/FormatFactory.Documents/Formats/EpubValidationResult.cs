namespace FormatFactoryWinUI.Documents;

public sealed record EpubValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? PackagePath);
