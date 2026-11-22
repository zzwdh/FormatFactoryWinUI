using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class OcrLanguageUnavailableException : InvalidOperationException
{
    public OcrLanguageUnavailableException(string message)
        : base(message)
    {
    }

    public OcrLanguageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class OcrLanguagePolicy
{
    public static TEngine Resolve<TEngine>(
        string? languageTag,
        Func<string, TEngine?> createRequested,
        Func<TEngine?> createAutomatic)
        where TEngine : class
    {
        ArgumentNullException.ThrowIfNull(createRequested);
        ArgumentNullException.ThrowIfNull(createAutomatic);

        var requestedTag = NormalizeRequestedTag(languageTag);
        if (requestedTag is null)
        {
            return createAutomatic()
                ?? throw new OcrLanguageUnavailableException(LocalizationService.T("ConversionUiNoOcrLanguageInstalled"));
        }

        try
        {
            return createRequested(requestedTag)
                ?? throw new OcrLanguageUnavailableException(LocalizationService.T("ConversionUiOcrLanguageNotInstalled", requestedTag));
        }
        catch (ArgumentException ex)
        {
            throw new OcrLanguageUnavailableException(LocalizationService.T("ConversionUiOcrLanguageTagInvalid", requestedTag), ex);
        }
    }

    internal static string? NormalizeRequestedTag(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag) ||
            languageTag.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return languageTag.Trim();
    }
}
