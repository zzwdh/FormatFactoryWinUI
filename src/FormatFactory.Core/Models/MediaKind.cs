namespace FormatFactoryWinUI.Models;

public enum MediaKind
{
    Video = (int)ContentKind.Video,
    Audio = (int)ContentKind.Audio,
    Image = (int)ContentKind.Image,
    Document = (int)ContentKind.Document,
    Ebook = (int)ContentKind.Ebook,
    Disc = (int)ContentKind.Disc
}

public static class ContentKindCompatibility
{
    public static ContentKind ToContentKind(this MediaKind kind) => (ContentKind)kind;

    public static MediaKind ToMediaKind(this ContentKind kind) => (MediaKind)kind;
}

public enum ConversionState
{
    Waiting,
    Probing,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Blocked
}
