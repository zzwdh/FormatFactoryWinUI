namespace FormatFactoryWinUI.Services;

/// <summary>
/// Uses local paths directly when no platform access-token service is supplied.
/// </summary>
public sealed class LocalImportStorageAccess : IImportStorageAccess
{
    public Task<string?> RememberFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<string> ResolveFilePathAsync(
        string path,
        string? token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(path);
    }

    public Task<string?> ResolveFolderPathAsync(
        string? path,
        string? token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(path);
    }
}
