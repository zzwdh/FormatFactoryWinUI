namespace FormatFactoryWinUI.Services;

internal sealed class WindowsMediaMetadataReader : IMediaMetadataReader
{
    public Task<MediaFoundationMetadata?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        MediaFoundationMetadataService.ReadAsync(path, cancellationToken);
}
