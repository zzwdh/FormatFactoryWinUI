namespace FormatFactoryWinUI.Models;

public enum MediaKind
{
    Video,
    Audio,
    Image
}

public enum ConversionState
{
    Waiting,
    Probing,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}
