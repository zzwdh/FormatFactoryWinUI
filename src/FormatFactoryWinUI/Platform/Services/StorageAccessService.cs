namespace FormatFactoryWinUI.Services;

internal static class StorageAccessService
{
    public static Task<string?> RememberFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        StorageTokenRegistry.RememberFileAsync(path, cancellationToken);

    public static Task<string?> RememberFolderAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        StorageTokenRegistry.RememberFolderAsync(path, cancellationToken);

    public static Task<string> ResolveFilePathAsync(
        string path,
        string? token,
        CancellationToken cancellationToken = default) =>
        StorageTokenRegistry.ResolveFilePathAsync(path, token, cancellationToken);

    public static Task<string?> ResolveFolderPathAsync(
        string? path,
        string? token,
        CancellationToken cancellationToken = default) =>
        StorageTokenRegistry.ResolveFolderPathAsync(path, token, cancellationToken);

    public static Task ReleaseAsync(
        string? token,
        CancellationToken cancellationToken = default) =>
        StorageTokenRegistry.ReleaseAsync(token, cancellationToken);
}
