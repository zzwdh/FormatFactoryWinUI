using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public interface IArchiveConverter
{
    bool IsAvailable { get; }
    Task<string> ConvertAsync(string sourcePath, string outputDirectory, ArchiveConversionOptions options,
        Func<ArchivePasswordRequest, CancellationToken, Task<string?>> requestPassword,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default,
        Func<string, string, CancellationToken, Task>? prepareCommit = null);
}

public interface IArchiveTaskPersistence
{
    Task<ArchiveTaskSnapshot> LoadAsync(CancellationToken token = default);
    Task SaveAsync(ArchiveTaskSnapshot snapshot, CancellationToken token = default);
}

public interface IArchiveCommitReceiptStore
{
    Task PrepareAsync(ArchiveTaskCheckpoint task, string stagedPath, string finalPath, CancellationToken token = default);
    Task<ArchiveTaskCheckpoint?> TryRecoverAsync(ArchiveTaskCheckpoint task, CancellationToken token = default);
    Task ForgetAsync(Guid taskId, CancellationToken token = default);
}
