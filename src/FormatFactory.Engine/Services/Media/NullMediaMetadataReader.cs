namespace FormatFactoryWinUI.Services;

/// <summary>
/// Leaves FFprobe metadata unchanged when the host has no supplemental reader.
/// </summary>
public sealed class NullMediaMetadataReader : IMediaMetadataReader
{
    public Task<MediaFoundationMetadata?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<MediaFoundationMetadata?>(null);
    }
}
