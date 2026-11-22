using System.Net;
using System.Text;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Writes a normalized document as UTF-8 Markdown plus a sibling asset directory.</summary>
public sealed class MarkdownDocumentWriter
{
    public async Task WriteAsync(
        NormalizedDocument document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiMarkdownOutputNeedsDirectory"), nameof(outputPath));
        Directory.CreateDirectory(directory);

        var token = Guid.NewGuid().ToString("N");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{token}.tmp");
        var finalAssetDirectory = document.Resources.Count == 0 ? null : ReserveAssetDirectory(fullPath);
        var temporaryAssetDirectory = finalAssetDirectory is null
            ? null
            : Path.Combine(directory, $".{Path.GetFileName(finalAssetDirectory)}.{token}.tmp");
        var committedAssets = false;
        var committedMarkdown = false;

        try
        {
            var resourceLinks = temporaryAssetDirectory is null || finalAssetDirectory is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : await StageResourcesAsync(
                    document.Resources,
                    temporaryAssetDirectory,
                    Path.GetFileName(finalAssetDirectory),
                    cancellationToken);

            var markdown = new Renderer(resourceLinks, cancellationToken).Render(document);
            await File.WriteAllTextAsync(
                temporaryPath,
                markdown.TrimEnd() + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            OutputCommitReceiptStore.Shared.PrepareRegisteredPath(fullPath, temporaryPath,
                temporaryAssetDirectory, finalAssetDirectory);
            if (temporaryAssetDirectory is not null && finalAssetDirectory is not null)
            {
                Directory.Move(temporaryAssetDirectory, finalAssetDirectory);
                committedAssets = true;
            }
            File.Move(temporaryPath, fullPath, overwrite: false);
            committedMarkdown = true;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (temporaryAssetDirectory is not null && Directory.Exists(temporaryAssetDirectory))
                DeleteDirectory(temporaryAssetDirectory);
            if (!committedMarkdown && committedAssets && finalAssetDirectory is not null && Directory.Exists(finalAssetDirectory))
                DeleteDirectory(finalAssetDirectory);
        }
    }

    private static async Task<Dictionary<string, string>> StageResourcesAsync(
        IReadOnlyList<NormalizedResource> resources,
        string temporaryDirectory,
        string finalDirectoryName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < resources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = resources[index];
            var fileName = UniqueFileName(ResourceFileName(resource, index), names);
            await using (var stream = new FileStream(
                Path.Combine(temporaryDirectory, fileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await using var source = resource.OpenRead();
                await source.CopyToAsync(stream, cancellationToken);
            }
            links.TryAdd(resource.Id,
                $"{Uri.EscapeDataString(finalDirectoryName)}/{Uri.EscapeDataString(fileName)}");
        }
        return links;
    }

    private static string ResourceFileName(NormalizedResource resource, int index)
    {
        var name = string.IsNullOrWhiteSpace(resource.FileName)
            ? $"resource-{index + 1}{ExtensionFor(resource.MediaType)}"
            : Path.GetFileName(resource.FileName.Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            name = $"resource-{index + 1}{ExtensionFor(resource.MediaType)}";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(value => invalid.Contains(value) ? '_' : value).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"resource-{index + 1}{ExtensionFor(resource.MediaType)}"
            : sanitized;
    }

    private static string UniqueFileName(string requested, HashSet<string> names)
    {
        if (names.Add(requested)) return requested;
        var stem = Path.GetFileNameWithoutExtension(requested);
        var extension = Path.GetExtension(requested);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem}-{suffix}{extension}";
            if (names.Add(candidate)) return candidate;
        }
    }

    private static string ExtensionFor(string mediaType) => mediaType.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/tiff" => ".tiff",
        "image/svg+xml" => ".svg",
        _ => ".bin"
    };

    private static string ReserveAssetDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        var baseName = Path.GetFileNameWithoutExtension(outputPath) + ".assets";
        var candidate = Path.Combine(directory, baseName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName}-{suffix}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Renderer(
        IReadOnlyDictionary<string, string> resourceLinks,
        CancellationToken cancellationToken)
    {
        private readonly List<FootnoteEntry> _footnotes = [];
        private readonly Dictionary<string, FootnoteEntry> _footnotesBySourceId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _footnoteIds = new(StringComparer.OrdinalIgnoreCase);

        public string Render(NormalizedDocument document)
        {
            var markdown = new StringBuilder();
            var firstBlock = document.Blocks.FirstOrDefault(block => block is not NormalizedFootnote);
            var alreadyHasTitle = firstBlock is NormalizedHeading { Level: 1 } heading &&
                string.Equals(heading.Text, document.Title, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(document.Title) && !alreadyHasTitle)
                AppendHeading(markdown, 1, document.Title, 0);

            foreach (var section in document.Sections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendBlocks(markdown, section.Blocks, 0);
            }

            if (_footnotes.Count > 0)
            {
                TrimLineEndings(markdown);
                markdown.AppendLine().AppendLine();
                for (var index = 0; index < _footnotes.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var footnote = _footnotes[index];
                    markdown.Append("[^").Append(footnote.Id).AppendLine("]:");
                    AppendBlocks(markdown, footnote.Blocks, 1);
                }
            }
            return markdown.ToString();
        }

        private void AppendBlocks(StringBuilder markdown, IEnumerable<NormalizedBlock> blocks, int depth)
        {
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendBlock(markdown, block, depth);
            }
        }

        private void AppendBlock(StringBuilder markdown, NormalizedBlock block, int depth)
        {
            var indent = new string(' ', depth * 4);
            switch (block)
            {
                case NormalizedHeading heading:
                    AppendHeading(markdown, heading.Level, heading.Text, depth);
                    break;
                case NormalizedParagraph paragraph:
                    markdown.Append(indent).AppendLine(EscapeInline(paragraph.Text)).AppendLine();
                    break;
                case NormalizedList list:
                    AppendList(markdown, list, depth);
                    break;
                case NormalizedTable table:
                    AppendTable(markdown, table, depth);
                    break;
                case NormalizedImage image:
                    markdown.Append(indent).AppendLine(ImageReference(image));
                    if (!string.IsNullOrWhiteSpace(image.Caption))
                        markdown.Append(indent).Append('*').Append(EscapeInline(image.Caption)).AppendLine("*");
                    markdown.AppendLine();
                    break;
                case NormalizedPageBreak:
                    markdown.Append(indent).AppendLine("<!-- page-break -->");
                    markdown.Append(indent).AppendLine("<div style=\"page-break-after: always;\"></div>").AppendLine();
                    break;
                case NormalizedFootnote footnote:
                    var id = RegisterFootnote(footnote);
                    TrimLineEndings(markdown);
                    markdown.Append("[^").Append(id).AppendLine("]").AppendLine();
                    break;
            }
        }

        private static void AppendHeading(StringBuilder markdown, int level, string text, int depth) =>
            markdown.Append(' ', depth * 4)
                .Append('#', Math.Clamp(level, 1, 6))
                .Append(' ')
                .AppendLine(EscapeInline(text))
                .AppendLine();

        private void AppendList(StringBuilder markdown, NormalizedList list, int depth)
        {
            var indent = new string(' ', depth * 4);
            for (var index = 0; index < list.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = list.Items[index];
                markdown.Append(indent).Append(list.Ordered ? $"{index + 1}. " : "- ");
                if (item.Blocks.Count == 0)
                {
                    markdown.AppendLine();
                    continue;
                }

                var first = item.Blocks[0];
                switch (first)
                {
                    case NormalizedParagraph paragraph:
                        markdown.AppendLine(EscapeInline(paragraph.Text));
                        break;
                    case NormalizedHeading heading:
                        markdown.Append("**").Append(EscapeInline(heading.Text)).AppendLine("**");
                        break;
                    case NormalizedImage image:
                        markdown.AppendLine(ImageReference(image));
                        break;
                    case NormalizedFootnote footnote:
                        markdown.Append("[^").Append(RegisterFootnote(footnote)).AppendLine("]");
                        break;
                    default:
                        markdown.AppendLine();
                        AppendBlock(markdown, first, depth + 1);
                        break;
                }
                for (var childIndex = 1; childIndex < item.Blocks.Count; childIndex++)
                    AppendBlock(markdown, item.Blocks[childIndex], depth + 1);
            }
            markdown.AppendLine();
        }

        private void AppendTable(StringBuilder markdown, NormalizedTable table, int depth)
        {
            if (table.Rows.Count == 0) return;
            if (table.Rows.SelectMany(row => row.Cells).Any(cell => cell.ColumnSpan > 1 || cell.RowSpan > 1))
            {
                AppendHtmlTable(markdown, table, depth);
                return;
            }

            var width = table.Rows.Max(row => row.Cells.Count);
            if (width == 0) return;
            var indent = new string(' ', depth * 4);
            AppendTableRow(markdown, table.Rows[0], width, indent);
            markdown.Append(indent).Append('|');
            for (var column = 0; column < width; column++) markdown.Append(" --- |");
            markdown.AppendLine();
            foreach (var row in table.Rows.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendTableRow(markdown, row, width, indent);
            }
            markdown.AppendLine();
        }

        private void AppendTableRow(StringBuilder markdown, NormalizedTableRow row, int width, string indent)
        {
            markdown.Append(indent).Append('|');
            for (var column = 0; column < width; column++)
            {
                var value = column < row.Cells.Count ? FlattenMarkdown(row.Cells[column].Blocks) : string.Empty;
                markdown.Append(' ').Append(value.Replace("|", "\\|", StringComparison.Ordinal)).Append(" |");
            }
            markdown.AppendLine();
        }

        private void AppendHtmlTable(StringBuilder markdown, NormalizedTable table, int depth)
        {
            var indent = new string(' ', depth * 4);
            markdown.Append(indent).AppendLine("<table>");
            markdown.Append(indent).AppendLine("  <tbody>");
            foreach (var row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                markdown.Append(indent).AppendLine("    <tr>");
                foreach (var cell in row.Cells)
                {
                    var attributes = new StringBuilder();
                    if (cell.ColumnSpan > 1) attributes.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                    if (cell.RowSpan > 1) attributes.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                    markdown.Append(indent).Append("      <td").Append(attributes).Append('>')
                        .Append(WebUtility.HtmlEncode(FlattenPlain(cell.Blocks)).Replace("\n", "<br>", StringComparison.Ordinal))
                        .AppendLine("</td>");
                }
                markdown.Append(indent).AppendLine("    </tr>");
            }
            markdown.Append(indent).AppendLine("  </tbody>");
            markdown.Append(indent).AppendLine("</table>").AppendLine();
        }

        private string FlattenMarkdown(IEnumerable<NormalizedBlock> blocks) => string.Join("<br>", blocks
            .Select(block => block switch
            {
                NormalizedHeading heading => EscapeInline(heading.Text),
                NormalizedParagraph paragraph => EscapeInline(paragraph.Text),
                NormalizedImage image => ImageReference(image),
                NormalizedList list => string.Join("; ", list.Items.Select(item => FlattenMarkdown(item.Blocks))),
                NormalizedFootnote footnote => $"[^{RegisterFootnote(footnote)}]",
                NormalizedPageBreak => "<!-- page-break -->",
                NormalizedTable table => string.Join("; ", table.Rows.Select(row =>
                    string.Join(" / ", row.Cells.Select(cell => FlattenMarkdown(cell.Blocks))))),
                _ => string.Empty
            }).Where(value => !string.IsNullOrWhiteSpace(value)));

        private static string FlattenPlain(IEnumerable<NormalizedBlock> blocks) => string.Join("\n", blocks
            .Select(block => block switch
            {
                NormalizedHeading heading => heading.Text,
                NormalizedParagraph paragraph => paragraph.Text,
                NormalizedImage image => image.AlternativeText ?? image.Caption ?? LocalizationService.T("ConversionUiImage"),
                NormalizedList list => string.Join("; ", list.Items.Select(item => FlattenPlain(item.Blocks))),
                NormalizedFootnote footnote => FlattenPlain(footnote.Blocks),
                NormalizedTable table => string.Join("; ", table.Rows.Select(row =>
                    string.Join(" / ", row.Cells.Select(cell => FlattenPlain(cell.Blocks))))),
                NormalizedPageBreak => "---",
                _ => string.Empty
            }).Where(value => !string.IsNullOrWhiteSpace(value)));

        private string ImageReference(NormalizedImage image)
        {
            var alternative = image.AlternativeText ?? image.Caption ?? LocalizationService.T("ConversionUiImage");
            var link = resourceLinks.TryGetValue(image.ResourceId, out var resourceLink)
                ? resourceLink
                : Uri.EscapeDataString(image.ResourceId);
            return $"![{EscapeAlternativeText(alternative)}]({link})";
        }

        private string RegisterFootnote(NormalizedFootnote footnote)
        {
            if (_footnotesBySourceId.TryGetValue(footnote.Id, out var existing)) return existing.Id;
            var baseId = SafeFootnoteId(footnote.Id);
            var id = baseId;
            for (var suffix = 2; !_footnoteIds.Add(id); suffix++) id = $"{baseId}-{suffix}";
            var entry = new FootnoteEntry(id, footnote.Blocks);
            _footnotes.Add(entry);
            _footnotesBySourceId.TryAdd(footnote.Id, entry);
            return id;
        }

        private static string SafeFootnoteId(string value)
        {
            var result = new string(value.Where(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.').ToArray());
            return string.IsNullOrWhiteSpace(result) ? "footnote" : result;
        }

        private static string EscapeInline(string value)
        {
            var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            return string.Join("<br>", normalized.Split('\n').Select(line => line
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("`", "\\`", StringComparison.Ordinal)
                .Replace("*", "\\*", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal)
                .Replace("<", "\\<", StringComparison.Ordinal)
                .Replace(">", "\\>", StringComparison.Ordinal)));
        }

        private static string EscapeAlternativeText(string value) => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        private static void TrimLineEndings(StringBuilder value)
        {
            while (value.Length > 0 && value[^1] is '\r' or '\n') value.Length--;
        }

        private sealed record FootnoteEntry(string Id, IReadOnlyList<NormalizedBlock> Blocks);
    }
}
