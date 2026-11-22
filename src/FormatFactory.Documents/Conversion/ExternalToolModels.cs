namespace FormatFactoryWinUI.ConversionEngines;

public static class ExternalEngineIds
{
    public const string Word = "word";
    public const string LibreOffice = "libreoffice";
    public const string Calibre = "calibre";
    public const string Caj2Pdf = "caj2pdf";
}

public enum ExternalToolKind
{
    MicrosoftWord,
    LibreOffice,
    Calibre,
    Caj2Pdf
}

public sealed record ExternalToolConfiguration(
    string? LibreOfficePath = null,
    string? CalibreEbookConvertPath = null,
    string? Caj2PdfPath = null);

public sealed record ExternalToolInfo(
    ExternalToolKind Kind,
    string DisplayName,
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string StatusMessage)
{
    public IReadOnlyList<string> ArgumentPrefix { get; init; } = [];
}

public sealed record ExternalConversionResult(
    ExternalToolKind Tool,
    string OutputPath,
    string StandardOutput,
    string StandardError);
