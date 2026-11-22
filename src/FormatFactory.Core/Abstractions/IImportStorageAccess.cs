namespace FormatFactoryWinUI.Services;

/// <summary>
/// Resolves import paths and preserves any access granted by the host platform.
/// Access tokens are opaque to the import workflow.
/// </summary>
public interface IImportStorageAccess
{
    Task<string?> RememberFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<string> ResolveFilePathAsync(
        string path,
        string? token,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveFolderPathAsync(
        string? path,
        string? token,
        CancellationToken cancellationToken = default);
}
