using System.Text.Json;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace FormatFactoryWinUI.Services;

internal enum StorageTokenKind
{
    File,
    Folder
}

internal static class StorageTokenRegistry
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string RegistryPath => Path.Combine(LogService.AppDataRoot, "storage-token-registry.json");

    public static async Task<string?> RememberFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var diagnostic = new StorageTokenDiagnosticOperation(StorageTokenDiagnosticAction.Remember, StorageTokenDiagnosticKind.File)
        { Stage = StorageTokenDiagnosticStage.ItemOpen };
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            return await RememberAsync(file, StorageTokenKind.File, diagnostic, cancellationToken);
        }
        catch (Exception ex)
        {
            diagnostic.Fail(ex);
            LogService.WriteApp(LocalizationService.T("OperationalLogFileAccessPermissionSaveFailed", ex.Message));
            return null;
        }
    }

    public static async Task<string?> RememberFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var diagnostic = new StorageTokenDiagnosticOperation(StorageTokenDiagnosticAction.Remember, StorageTokenDiagnosticKind.Folder)
        { Stage = StorageTokenDiagnosticStage.ItemOpen };
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            return await RememberAsync(folder, StorageTokenKind.Folder, diagnostic, cancellationToken);
        }
        catch (Exception ex)
        {
            diagnostic.Fail(ex);
            LogService.WriteApp(LocalizationService.T("OperationalLogFolderAccessPermissionSaveFailed", ex.Message));
            return null;
        }
    }

    public static async Task<string> ResolveFilePathAsync(
        string fallbackPath,
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ImportSourceDiagnostics.TokenResolution(StorageTokenDiagnosticKind.File, false, ImportTokenOutcome.NotRequested);
            return fallbackPath;
        }

        var diagnostic = new StorageTokenDiagnosticOperation(StorageTokenDiagnosticAction.Resolve, StorageTokenDiagnosticKind.File, tokenPresent: true)
        { Stage = StorageTokenDiagnosticStage.RestoreItem };
        try
        {
            var file = await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryUpdate;
            await TouchAsync(token, file.Path, StorageTokenKind.File, diagnostic, cancellationToken);
            diagnostic.Stage = StorageTokenDiagnosticStage.RestoreItem;
            ImportSourceDiagnostics.TokenResolution(diagnostic.Kind, true, ImportTokenOutcome.Restored, diagnostic.Id, diagnostic.Stage);
            return file.Path;
        }
        catch (Exception ex)
        {
            ImportSourceDiagnostics.TokenResolution(diagnostic.Kind, true, ImportTokenOutcome.Fallback, diagnostic.Id, diagnostic.Stage, ex);
            diagnostic.Fail(ex);
            LogService.WriteApp(LocalizationService.T("OperationalLogFileAccessPermissionRestoreFailed", ex.Message));
            return fallbackPath;
        }
    }

    public static async Task<string?> ResolveFolderPathAsync(
        string? fallbackPath,
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ImportSourceDiagnostics.TokenResolution(StorageTokenDiagnosticKind.Folder, false, ImportTokenOutcome.NotRequested);
            return fallbackPath;
        }

        var diagnostic = new StorageTokenDiagnosticOperation(StorageTokenDiagnosticAction.Resolve, StorageTokenDiagnosticKind.Folder, tokenPresent: true)
        { Stage = StorageTokenDiagnosticStage.RestoreItem };
        try
        {
            var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryUpdate;
            await TouchAsync(token, folder.Path, StorageTokenKind.Folder, diagnostic, cancellationToken);
            diagnostic.Stage = StorageTokenDiagnosticStage.RestoreItem;
            ImportSourceDiagnostics.TokenResolution(diagnostic.Kind, true, ImportTokenOutcome.Restored, diagnostic.Id, diagnostic.Stage);
            return folder.Path;
        }
        catch (Exception ex)
        {
            ImportSourceDiagnostics.TokenResolution(diagnostic.Kind, true, ImportTokenOutcome.Fallback, diagnostic.Id, diagnostic.Stage, ex);
            diagnostic.Fail(ex);
            LogService.WriteApp(LocalizationService.T("OperationalLogFolderAccessPermissionRestoreFailed", ex.Message));
            return fallbackPath;
        }
    }

    public static async Task ReleaseAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var diagnostic = new StorageTokenDiagnosticOperation(StorageTokenDiagnosticAction.Release, tokenPresent: true)
        { Stage = StorageTokenDiagnosticStage.AcquireLock };
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(diagnostic, cancellationToken);
            diagnostic.Stage = StorageTokenDiagnosticStage.FindExisting;
            var entry = entries.FirstOrDefault(item => item.Token.Equals(token, StringComparison.Ordinal));
            if (entry is null)
            {
                return;
            }

            diagnostic.Kind = DiagnosticKind(entry.Kind);
            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryUpdate;
            entry.ReferenceCount--;
            if (entry.ReferenceCount <= 0)
            {
                entries.Remove(entry);
                diagnostic.Stage = StorageTokenDiagnosticStage.FutureAccessCheck;
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                {
                    diagnostic.Stage = StorageTokenDiagnosticStage.FutureAccessRemove;
                    StorageApplicationPermissions.FutureAccessList.Remove(token);
                }
            }
            else
            {
                entry.LastUsedUtc = DateTimeOffset.UtcNow;
            }

            await SaveAsync(entries, diagnostic, cancellationToken);
        }
        catch (Exception ex)
        {
            diagnostic.Fail(ex);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var diagnostic = new StorageTokenDiagnosticOperation(StorageTokenDiagnosticAction.Recover)
        { Stage = StorageTokenDiagnosticStage.AcquireLock };
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(diagnostic, cancellationToken);
            var removed = entries.RemoveAll(entry =>
            {
                diagnostic.Kind = DiagnosticKind(entry.Kind);
                var tokenPresent = !string.IsNullOrWhiteSpace(entry.Token);
                diagnostic.TokenPresent = tokenPresent;
                diagnostic.Stage = StorageTokenDiagnosticStage.FutureAccessCheck;
                return !tokenPresent ||
                    !StorageApplicationPermissions.FutureAccessList.ContainsItem(entry.Token);
            });
            if (removed > 0)
            {
                diagnostic.Kind = StorageTokenDiagnosticKind.Unknown;
                diagnostic.TokenPresent = false;
                await SaveAsync(entries, diagnostic, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            diagnostic.Fail(ex);
            LogService.WriteApp(LocalizationService.T("OperationalLogAccessTokenRegistryRestoreFailed", ex.Message));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> RememberAsync(
        IStorageItem item,
        StorageTokenKind kind,
        StorageTokenDiagnosticOperation diagnostic,
        CancellationToken cancellationToken)
    {
        diagnostic.Stage = StorageTokenDiagnosticStage.NormalizePath;
        var fullPath = Path.GetFullPath(item.Path);
        diagnostic.Stage = StorageTokenDiagnosticStage.AcquireLock;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(diagnostic, cancellationToken);
            diagnostic.Stage = StorageTokenDiagnosticStage.FindExisting;
            var existing = entries.FirstOrDefault(entry =>
                entry.Kind == kind &&
                entry.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
                HasAccess(entry, diagnostic));
            if (existing is not null)
            {
                diagnostic.TokenPresent = !string.IsNullOrWhiteSpace(existing.Token);
                diagnostic.Stage = StorageTokenDiagnosticStage.RegistryUpdate;
                existing.ReferenceCount++;
                existing.LastUsedUtc = DateTimeOffset.UtcNow;
                await SaveAsync(entries, diagnostic, cancellationToken);
                return existing.Token;
            }

            diagnostic.TokenPresent = false;
            diagnostic.Stage = StorageTokenDiagnosticStage.FutureAccessAdd;
            var token = StorageApplicationPermissions.FutureAccessList.Add(item, kind.ToString());
            diagnostic.TokenPresent = !string.IsNullOrWhiteSpace(token);
            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryUpdate;
            entries.Add(new StorageTokenEntry
            {
                Token = token,
                Kind = kind,
                Path = fullPath,
                ReferenceCount = 1,
                LastUsedUtc = DateTimeOffset.UtcNow
            });
            await SaveAsync(entries, diagnostic, cancellationToken);
            return token;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task TouchAsync(
        string token,
        string path,
        StorageTokenKind kind,
        StorageTokenDiagnosticOperation diagnostic,
        CancellationToken cancellationToken)
    {
        diagnostic.Stage = StorageTokenDiagnosticStage.AcquireLock;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(diagnostic, cancellationToken);
            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryUpdate;
            var entry = entries.FirstOrDefault(item => item.Token.Equals(token, StringComparison.Ordinal));
            if (entry is null)
            {
                entries.Add(new StorageTokenEntry
                {
                    Token = token,
                    Kind = kind,
                    Path = Path.GetFullPath(path),
                    ReferenceCount = 1,
                    LastUsedUtc = DateTimeOffset.UtcNow
                });
            }
            else
            {
                entry.Path = Path.GetFullPath(path);
                entry.LastUsedUtc = DateTimeOffset.UtcNow;
            }

            await SaveAsync(entries, diagnostic, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<List<StorageTokenEntry>> LoadAsync(StorageTokenDiagnosticOperation diagnostic, CancellationToken cancellationToken)
    {
        diagnostic.Stage = StorageTokenDiagnosticStage.RegistryRead;
        if (!File.Exists(RegistryPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(RegistryPath);
            return await JsonSerializer.DeserializeAsync<List<StorageTokenEntry>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException ex)
        {
            diagnostic.Fail(ex);
            LogService.WriteApp(LocalizationService.T("OperationalLogAccessTokenRegistryInvalid", ex.Message));
            return [];
        }
    }

    private static async Task SaveAsync(IReadOnlyCollection<StorageTokenEntry> entries,
        StorageTokenDiagnosticOperation diagnostic, CancellationToken cancellationToken)
    {
        diagnostic.Stage = StorageTokenDiagnosticStage.CreateDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        var temporaryPath = $"{RegistryPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryWrite;
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
                diagnostic.Stage = StorageTokenDiagnosticStage.RegistryFlush;
                await stream.FlushAsync(cancellationToken);
            }

            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryCommit;
            File.Move(temporaryPath, RegistryPath, true);
        }
        catch (Exception ex)
        {
            diagnostic.Fail(ex);
            throw;
        }
        finally
        {
            diagnostic.Stage = StorageTokenDiagnosticStage.RegistryCleanup;
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static StorageTokenDiagnosticKind DiagnosticKind(StorageTokenKind kind) => kind switch
    {
        StorageTokenKind.File => StorageTokenDiagnosticKind.File,
        StorageTokenKind.Folder => StorageTokenDiagnosticKind.Folder,
        _ => StorageTokenDiagnosticKind.Unknown
    };

    private static bool HasAccess(StorageTokenEntry entry, StorageTokenDiagnosticOperation diagnostic)
    {
        diagnostic.Stage = StorageTokenDiagnosticStage.FutureAccessCheck;
        diagnostic.TokenPresent = !string.IsNullOrWhiteSpace(entry.Token);
        var present = StorageApplicationPermissions.FutureAccessList.ContainsItem(entry.Token);
        diagnostic.Stage = StorageTokenDiagnosticStage.FindExisting;
        return present;
    }

    private sealed class StorageTokenEntry
    {
        public string Token { get; set; } = string.Empty;
        public StorageTokenKind Kind { get; set; }
        public string Path { get; set; } = string.Empty;
        public int ReferenceCount { get; set; }
        public DateTimeOffset LastUsedUtc { get; set; }
    }
}
