using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>Prevents normalization and OCR routes from claiming preservation they cannot verify.</summary>
public static class DocumentPreservationPolicy
{
    public static void Validate(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Mode?.Trim(), "lossless", StringComparison.OrdinalIgnoreCase)) return;
        if (IsDocumentFormat(request.TargetFormatId) || request.Inputs.Any(input =>
            IsDocumentFormat(input.FormatId) || input.Probe?.ContentKind is ContentKind.Document or ContentKind.Ebook))
            throw new NotSupportedException(LocalizationService.T("ConversionUiDocumentLosslessUnsupported"));
    }

    private static bool IsDocumentFormat(string formatId) =>
        FormatCatalog.FindById(FormatCatalog.NormalizeFormatId(formatId))?.ContentKind
            is ContentKind.Document or ContentKind.Ebook;
}
