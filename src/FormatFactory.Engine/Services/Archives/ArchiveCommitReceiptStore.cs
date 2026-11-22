using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>
/// A durable write-ahead receipt identifies a validated archive across the brief gap
/// between atomic publication and its task checkpoint. Credentials are never accepted.
/// </summary>
public sealed class ArchiveCommitReceiptStore : IArchiveCommitReceiptStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _root;
    private readonly SemaphoreSlim _gate;

    public ArchiveCommitReceiptStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(LogService.AppDataRoot, "archive-commit-receipts"));
        _gate = Gates.GetOrAdd(_root, _ => new(1, 1));
    }

    public async Task PrepareAsync(ArchiveTaskCheckpoint task, string stagedPath, string finalPath,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateTask(task);
        var staged = Path.GetFullPath(stagedPath);
        var final = Path.GetFullPath(finalPath);
        var directory = Path.GetDirectoryName(final)!;
        var relativeStaged = Path.GetRelativePath(directory, staged);
        if (!string.Equals(directory, Path.GetFullPath(task.OutputDirectory), StringComparison.OrdinalIgnoreCase) ||
            !final.EndsWith(ArchiveFormats.Extension(task.TargetFormat), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(final, Path.GetFullPath(task.SourcePath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(staged, final, StringComparison.OrdinalIgnoreCase) || Path.IsPathFullyQualified(relativeStaged) ||
            relativeStaged == ".." || relativeStaged.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid archive publication reservation.");

        var fingerprint = await FingerprintAsync(staged, token).ConfigureAwait(false);
        var receipt = new Receipt(1, task.Id, Path.GetFullPath(task.SourcePath), directory,
            task.TargetFormat, task.StrictVerification, task.EncryptOutput, final,
            fingerprint.Length, fingerprint.CreationTimeUtcTicks, fingerprint.Sha256, DateTimeOffset.UtcNow);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var path = PathFor(task.Id);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, receipt, JsonOptions, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Only an already published, byte-identical file can be recovered; this never moves files.</summary>
    public async Task<ArchiveTaskCheckpoint?> TryRecoverAsync(ArchiveTaskCheckpoint task, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ValidateTask(task);
            await using var stream = new FileStream(PathFor(task.Id), FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > 256 * 1024) return null;
            var receipt = await JsonSerializer.DeserializeAsync<Receipt>(stream, JsonOptions, token).ConfigureAwait(false);
            if (receipt is null || receipt.Version != 1 || receipt.TaskId != task.Id ||
                receipt.TargetFormat != task.TargetFormat || receipt.StrictVerification != task.StrictVerification ||
                receipt.EncryptOutput != task.EncryptOutput || receipt.SourcePath is null || receipt.OutputDirectory is null ||
                receipt.FinalPath is null || receipt.Sha256 is null || receipt.Sha256.Length != 64 ||
                receipt.Length < 0 || receipt.CreationTimeUtcTicks <= 0 || receipt.PreparedAtUtc == default ||
                !Path.IsPathFullyQualified(receipt.SourcePath) || !Path.IsPathFullyQualified(receipt.OutputDirectory) ||
                !Path.IsPathFullyQualified(receipt.FinalPath) ||
                !string.Equals(receipt.SourcePath, Path.GetFullPath(task.SourcePath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.OutputDirectory, Path.GetFullPath(task.OutputDirectory), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(receipt.FinalPath), receipt.OutputDirectory, StringComparison.OrdinalIgnoreCase) ||
                !receipt.FinalPath.EndsWith(ArchiveFormats.Extension(task.TargetFormat), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(receipt.FinalPath, receipt.SourcePath, StringComparison.OrdinalIgnoreCase)) return null;

            var fingerprint = await FingerprintAsync(receipt.FinalPath, token).ConfigureAwait(false);
            if (fingerprint.Length != receipt.Length || fingerprint.CreationTimeUtcTicks != receipt.CreationTimeUtcTicks ||
                !string.Equals(fingerprint.Sha256, receipt.Sha256, StringComparison.Ordinal)) return null;
            return task with
            {
                State = ArchiveTaskState.Succeeded, ErrorKey = null, OutputPath = receipt.FinalPath,
                ResultStatistics = null, CompletedAtUtc = receipt.PreparedAtUtc, HistoryRecorded = false
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        { return null; }
        finally { _gate.Release(); }
    }

    public async Task ForgetAsync(Guid taskId, CancellationToken token = default)
    {
        if (taskId == Guid.Empty) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { File.Delete(PathFor(taskId)); }
        finally { _gate.Release(); }
    }

    private string PathFor(Guid taskId) => Path.Combine(_root, taskId.ToString("N") + ".json");

    private static void ValidateTask(ArchiveTaskCheckpoint task)
    {
        if (task.Id == Guid.Empty || string.IsNullOrWhiteSpace(task.SourcePath) ||
            !Path.IsPathFullyQualified(task.SourcePath) || string.IsNullOrWhiteSpace(task.OutputDirectory) ||
            !Path.IsPathFullyQualified(task.OutputDirectory) || !Enum.IsDefined(task.TargetFormat) ||
            task.EncryptOutput && !ArchiveFormats.SupportsEncryption(task.TargetFormat))
            throw new InvalidDataException("Invalid archive publication reservation.");
    }

    private static async Task<Fingerprint> FingerprintAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Archive publication receipts do not accept links or directories.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var creation = File.GetCreationTimeUtc(path).Ticks;
        var length = stream.Length;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (stream.Length != length || File.GetCreationTimeUtc(path).Ticks != creation)
            throw new InvalidDataException("The archive changed while preparing its publication receipt.");
        return new(length, creation, hash);
    }

    private sealed record Fingerprint(long Length, long CreationTimeUtcTicks, string Sha256);
    private sealed record Receipt(int Version, Guid TaskId, string SourcePath, string OutputDirectory,
        ArchiveFormat TargetFormat, bool StrictVerification, bool EncryptOutput, string FinalPath,
        long Length, long CreationTimeUtcTicks, string Sha256, DateTimeOffset PreparedAtUtc);
}
