namespace FormatFactoryWinUI.Services;

/// <summary>
/// Restores import access through the Windows Future Access List.
/// </summary>
internal sealed class WindowsImportStorageAccess : IImportStorageAccess
{
    public async Task<string?> RememberFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = await StorageTokenRegistry.RememberFileAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return token;
    }

    public async Task<string> ResolveFilePathAsync(
        string path,
        string? token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = await StorageTokenRegistry.ResolveFilePathAsync(path, token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return resolvedPath;
    }

    public async Task<string?> ResolveFolderPathAsync(
        string? path,
        string? token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = await StorageTokenRegistry.ResolveFolderPathAsync(path, token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return resolvedPath;
    }
}
