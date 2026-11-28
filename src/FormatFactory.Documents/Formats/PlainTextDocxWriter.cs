using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Documents;

internal static class PlainTextDocxWriter
{
    public static Task WriteAsync(string path, string heading, string text)
    {
        var document = new NormalizedDocument(heading, string.Empty,
            [new NormalizedSection(null, [new NormalizedParagraph(heading), new NormalizedParagraph(text)])], []);
        return new PdfDocxWriter().WritePackageAsync(document, path, includeProperties: false, CancellationToken.None);
    }
}
