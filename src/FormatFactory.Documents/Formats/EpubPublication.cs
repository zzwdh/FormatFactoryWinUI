using System.Xml.Linq;

namespace FormatFactoryWinUI.Documents;

public sealed record EpubPublication(
    string Identifier,
    string Title,
    string Language,
    IReadOnlyList<EpubChapter> Chapters,
    IReadOnlyList<EpubResource>? Resources = null,
    string? Creator = null,
    DateTimeOffset? Modified = null,
    bool FixedLayout = false);

public sealed record EpubChapter(
    string Id,
    string Title,
    IReadOnlyList<XNode> Body,
    int? ViewportWidth = null,
    int? ViewportHeight = null);

public sealed record EpubResource(
    string Id,
    string FileName,
    string MediaType,
    ReadOnlyMemory<byte> Content)
{
    public string? ContentPath { get; init; }

    public Stream OpenRead() => Models.NormalizedResource.OpenContentRead(Content, ContentPath);
}
