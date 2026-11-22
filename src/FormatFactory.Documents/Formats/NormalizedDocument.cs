namespace FormatFactoryWinUI.Models;

public sealed record NormalizedDocument(
    string Title,
    string Language,
    IReadOnlyList<NormalizedSection> Sections,
    IReadOnlyList<NormalizedResource> Resources)
{
    public IEnumerable<NormalizedBlock> Blocks => Sections.SelectMany(section => section.Blocks);
}

public sealed record NormalizedSection(
    string? Id,
    IReadOnlyList<NormalizedBlock> Blocks);

public abstract record NormalizedBlock;

public sealed record NormalizedHeading(int Level, string Text) : NormalizedBlock;

public sealed record NormalizedParagraph(string Text) : NormalizedBlock;

public sealed record NormalizedList(
    bool Ordered,
    IReadOnlyList<NormalizedListItem> Items) : NormalizedBlock;

public sealed record NormalizedListItem(IReadOnlyList<NormalizedBlock> Blocks);

public sealed record NormalizedTable(
    IReadOnlyList<NormalizedTableRow> Rows) : NormalizedBlock;

public sealed record NormalizedTableRow(IReadOnlyList<NormalizedTableCell> Cells);

public sealed record NormalizedTableCell(
    IReadOnlyList<NormalizedBlock> Blocks,
    int ColumnSpan = 1,
    int RowSpan = 1);

public sealed record NormalizedImage(
    string ResourceId,
    string? AlternativeText = null,
    string? Caption = null) : NormalizedBlock;

public sealed record NormalizedPageBreak : NormalizedBlock;

public sealed record NormalizedFootnote(
    string Id,
    IReadOnlyList<NormalizedBlock> Blocks) : NormalizedBlock;

public sealed record NormalizedResource(
    string Id,
    string MediaType,
    string FileName,
    ReadOnlyMemory<byte> Content)
{
    // The pipeline owns this file until all writers have finished.
    public string? ContentPath { get; init; }

    public Stream OpenRead() => OpenContentRead(Content, ContentPath);

    internal static Stream OpenContentRead(ReadOnlyMemory<byte> content, string? path)
    {
        if (path is not null)
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(content, out var bytes))
            return new MemoryStream(bytes.Array ?? [], bytes.Offset, bytes.Count, writable: false, publiclyVisible: true);
        return new MemoryStream(content.ToArray(), writable: false);
    }
}
