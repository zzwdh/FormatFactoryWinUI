using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests.TestDoubles;

internal sealed class ImportStorageAccessFake : IImportStorageAccess
{
    public Action<string>? FileResolving { get; set; }

    public Task<string> ResolveFilePathAsync(
        string path,
        string? token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileResolving?.Invoke(path);
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

    public Task<string?> RememberFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
