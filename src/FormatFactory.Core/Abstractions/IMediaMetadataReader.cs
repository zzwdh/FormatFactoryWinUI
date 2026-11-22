namespace FormatFactoryWinUI.Services;

/// <summary>
/// Reads optional host metadata to supplement the FFprobe result.
/// </summary>
public interface IMediaMetadataReader
{
    Task<MediaFoundationMetadata?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public sealed record MediaFoundationMetadata(
    TimeSpan Duration,
    uint Bitrate,
    int Width,
    int Height,
    string Title,
    string Artist,
    string Album);
