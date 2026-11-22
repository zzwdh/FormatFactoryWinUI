using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace FormatFactoryWinUI.Documents;

internal static class PlainTextDocxWriter
{
    public static void Write(string path, string heading, string text)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body(new Paragraph(new Run(new Text(heading))));
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            body.Append(new Paragraph(new Run(new Text(paragraph) { Space = SpaceProcessingModeValues.Preserve })));
        }
        main.Document = new Document(body);
        main.Document.Save();
    }
}
