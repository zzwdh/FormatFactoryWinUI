using System.Text;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed class PlainTextDocumentWriter
{
    public async Task WriteAsync(NormalizedDocument document, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiTxtOutputNeedsDirectory"), nameof(outputPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var text = new StringBuilder();
            foreach (var block in document.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Append(text, block, 0, cancellationToken);
            }

            await File.WriteAllTextAsync(
                temporaryPath,
                text.ToString().TrimEnd() + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void Append(
        StringBuilder text,
        NormalizedBlock block,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var indent = new string(' ', depth * 2);
        switch (block)
        {
            case NormalizedHeading heading:
                text.Append(indent).Append(new string('#', Math.Clamp(heading.Level, 1, 6))).Append(' ').AppendLine(heading.Text).AppendLine();
                break;
            case NormalizedParagraph paragraph:
                text.Append(indent).AppendLine(paragraph.Text).AppendLine();
                break;
            case NormalizedList list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    text.Append(indent).Append(list.Ordered ? $"{i + 1}. " : "- ");
                    foreach (var child in list.Items[i].Blocks)
                    {
                        Append(text, child, depth + 1, cancellationToken);
                    }
                }
                text.AppendLine();
                break;
            case NormalizedTable table:
                foreach (var row in table.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    text.Append(indent).AppendLine(string.Join('\t', row.Cells.Select(cell => Flatten(cell.Blocks, cancellationToken))));
                }
                text.AppendLine();
                break;
            case NormalizedImage image:
                text.Append(indent).Append('[').Append(LocalizationService.T("ConversionUiImage"));
                if (!string.IsNullOrWhiteSpace(image.AlternativeText)) text.Append(": ").Append(image.AlternativeText);
                text.AppendLine("]").AppendLine();
                break;
            case NormalizedPageBreak:
                text.Append('\f').AppendLine();
                break;
            case NormalizedFootnote footnote:
                text.Append(indent)
                    .Append('[')
                    .Append(LocalizationService.T("ConversionUiFootnote"))
                    .Append(' ')
                    .Append(footnote.Id)
                    .AppendLine("]");
                foreach (var child in footnote.Blocks) Append(text, child, depth + 1, cancellationToken);
                break;
        }
    }

    private static string Flatten(IEnumerable<NormalizedBlock> blocks, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = block switch
            {
                NormalizedHeading heading => heading.Text,
                NormalizedParagraph paragraph => paragraph.Text,
                NormalizedImage image => image.AlternativeText ?? image.Caption ?? LocalizationService.T("ConversionUiImage"),
                NormalizedList list => string.Join("; ", list.Items.Select(item => Flatten(item.Blocks, cancellationToken))),
                NormalizedTable table => string.Join("; ", table.Rows.Select(row =>
                    string.Join(" / ", row.Cells.Select(cell => Flatten(cell.Blocks, cancellationToken))))),
                NormalizedFootnote footnote => Flatten(footnote.Blocks, cancellationToken),
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }

        return string.Join(" ", values);
    }
}
