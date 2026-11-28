using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

internal static class DocumentTextWorker
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 512 };

    internal static Task<bool> WriteAsync(NormalizedDocument document, string format, string temporaryPath,
        IReadOnlyDictionary<string, string>? links, CancellationToken token) =>
        ConversionBackend.RequestAsync<bool>("document-text", new
        {
            Format = format, Output = temporaryPath, document.Title, document.Blocks,
            Links = links ?? new Dictionary<string, string>(),
            ImageLabel = LocalizationService.T("ConversionUiImage"),
            FootnoteLabel = LocalizationService.T("ConversionUiFootnote")
        }, token, JsonOptions);
}
