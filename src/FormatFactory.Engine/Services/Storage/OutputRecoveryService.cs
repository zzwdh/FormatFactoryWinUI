using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FormatFactoryWinUI.Services;

public sealed class OutputCommitException : IOException
{
    internal OutputCommitException(Exception commitError, string? recoveryId, string preservedPath, Exception? preservationError)
        : base(recoveryId is null
            ? LocalizationService.T("OutputRecoveryPreservedWithoutIndex", preservedPath, commitError.Message)
            : LocalizationService.T("OutputRecoveryCommitFailed", commitError.Message), commitError)
    {
        RecoveryId = recoveryId;
        PreservedPath = preservedPath;
        PreservationError = preservationError;
    }
    public string? RecoveryId { get; }
    public string PreservedPath { get; }
    public Exception? PreservationError { get; }
}

public sealed record OutputRecoveryRecord
{
    public string Id { get; init; } = string.Empty;
    public Guid TaskId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string TargetFormatId { get; init; } = string.Empty;
    public string ConversionMode { get; init; } = string.Empty;
    public string EngineId { get; init; } = string.Empty;
    // Optional for older recovery entries. Persists the exact failed commit through restart.
    public Guid? OriginalCommitOperation { get; init; }
    public OutputRecoveryFingerprint[] Files { get; init; } = [];
    [JsonIgnore] public string ArtifactPath { get; init; } = string.Empty;
}

public sealed record OutputRecoveryFingerprint(string Path, bool IsDirectory, long Length, string Sha256);

/// <summary>Only validated artifacts whose final rename failed are admitted by OutputPathLease.</summary>
public sealed class OutputRecoveryService
{
    public const int MaximumItems = 20;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly object Gate = new();
    // Entry leases protect artifacts while large copies and hashes run outside the metadata gate.
    private static readonly Dictionary<string, EntryUse> ActiveEntries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action? _recoveryCopyStarting;
    private RecoveryIndex _index = RecoveryIndex.Empty;
    private Task<IReadOnlyList<OutputRecoveryRecord>>? _indexLoad;
    private bool _indexLoaded;
    private long _metadataReadCount;
    internal long MetadataReadCount => Interlocked.Read(ref _metadataReadCount);
    public static OutputRecoveryService Shared { get; } = new();

    public OutputRecoveryService() : this(Path.Combine(LogService.AppDataRoot, "output-recovery")) { }
    internal OutputRecoveryService(string root, Func<DateTimeOffset>? clock = null, Action? recoveryCopyStarting = null)
    {
        _root = Path.GetFullPath(root);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        // Internal observer makes cancellation and contention tests deterministic without large fixture files.
        _recoveryCopyStarting = recoveryCopyStarting;
    }

    internal OutputRecoveryRecord PreserveValidated(Guid taskId, string source, string finalPath, bool isDirectory,
        string? targetFormatId = null, string? conversionMode = null, string? engineId = null, Guid? operationId = null)
    {
        var operation = operationId ?? Guid.NewGuid();
        var stage = StorageFailureStage.PreserveFingerprint;
        try
        {
        var fingerprints = Fingerprint(source, isDirectory, CancellationToken.None);
        if (fingerprints.Length == 0 || !fingerprints.Any(file => !file.IsDirectory && file.Length > 0))
            throw new InvalidDataException("The validated output is missing or empty.");
        var id = Guid.NewGuid().ToString("N");
        var entry = EntryPath(id);
        OutputRecoveryRecord record;
        using (var usage = AcquireEntry(entry, CancellationToken.None))
        {
            stage = StorageFailureStage.PreserveCreate;
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(entry);
            var artifact = Path.Combine(entry, "artifact");
            record = new OutputRecoveryRecord
            {
                Id = id, TaskId = taskId, FileName = Path.GetFileName(finalPath), IsDirectory = isDirectory,
                CreatedAtUtc = _clock(), Files = fingerprints, ArtifactPath = artifact,
                TargetFormatId = targetFormatId ?? string.Empty, ConversionMode = conversionMode ?? string.Empty,
                EngineId = engineId ?? string.Empty, OriginalCommitOperation = operationId
            };
            try
            {
                // Copy first: the original survives a full disk, access failure or interrupted preservation.
                stage = StorageFailureStage.PreserveCopy;
                CopyArtifact(source, artifact, isDirectory, fingerprints, CancellationToken.None);
                stage = StorageFailureStage.PreserveVerify;
                Verify(record, artifact, CancellationToken.None);
                lock (Gate)
                {
                    stage = StorageFailureStage.PreserveIndex;
                    var metadata = Path.Combine(entry, "entry.json");
                    File.WriteAllText(metadata + ".tmp", JsonSerializer.Serialize(record, JsonOptions));
                    File.Move(metadata + ".tmp", metadata);
                    RefreshIndexCore();
                }
            }
            catch
            {
                TryDeleteEntry(entry);
                throw;
            }
            TryDeleteArtifact(source, isDirectory);
        }
        try { PruneCore(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        StorageFailureDiagnostics.WriteRecoveryOutcome(operation, taskId, isDirectory,
            StorageFailureDiagnostics.ClassifyPath(finalPath), StorageRecoveryState.Indexed,
            StorageFailureStage.PreserveIndex, operationId);
        return record;
        }
        catch (Exception error)
        {
            StorageFailureDiagnostics.Record(error, new(stage, operation, taskId,
                StorageFailureDiagnostics.ClassifyPath(finalPath), isDirectory, Recovery: StorageRecoveryState.Unindexed));
            throw;
        }
    }

    public OutputRecoveryRecord? Get(Guid taskId) => Load().FirstOrDefault(record => record.TaskId == taskId);
    public OutputRecoveryRecord? GetById(string? id) => string.IsNullOrWhiteSpace(id) ? null : Load().FirstOrDefault(record => record.Id == id);

    /// <summary>Loads metadata once in the background. Retention cleanup is deliberately separate.</summary>
    public Task<IReadOnlyList<OutputRecoveryRecord>> LoadIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (_indexLoaded) return Task.FromResult<IReadOnlyList<OutputRecoveryRecord>>(CurrentRecords());
            _indexLoad ??= Task.Run<IReadOnlyList<OutputRecoveryRecord>>(() =>
            {
                lock (Gate)
                {
                    try
                    {
                        if (!_indexLoaded) RefreshIndexCore();
                        return CurrentRecords();
                    }
                    catch { _indexLoad = null; throw; }
                }
            });
            return cancellationToken.CanBeCanceled ? _indexLoad.WaitAsync(cancellationToken) : _indexLoad;
        }
    }

    /// <summary>Looks up the published metadata snapshot without touching the filesystem.</summary>
    public OutputRecoveryRecord? GetCached(Guid taskId)
    {
        var snapshot = Volatile.Read(ref _index);
        return snapshot.ByTask.TryGetValue(taskId, out var record) && !HasExpired(record) ? record : null;
    }

    public OutputRecoveryRecord? GetByIdCached(string? id)
    {
        var snapshot = Volatile.Read(ref _index);
        return id is not null && snapshot.ById.TryGetValue(id, out var record) && !HasExpired(record) ? record : null;
    }

    public Task PruneAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); PruneCore(); }, cancellationToken);

    public IReadOnlyList<OutputRecoveryRecord> Load()
    {
        PruneCore();
        lock (Gate) { RefreshIndexCore(); return CurrentRecords(); }
    }

    /// <summary>A cancelled or failed save preserves the recovery record and never replaces existing user output.</summary>
    public Task<string> SaveAsAsync(string id, string outputDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() => SaveAs(id, outputDirectory, cancellationToken), cancellationToken);

    private string SaveAs(string id, string outputDirectory, CancellationToken token)
    {
        var operation = Guid.NewGuid();
        var stage = StorageFailureStage.RecoveryLookup;
        Guid? taskId = null;
        bool? directoryOutput = null;
        Guid? originalCommitOperation = null;
        var destinationCommitted = false;
        try
        {
        token.ThrowIfCancellationRequested();
        using var usage = AcquireEntry(EntryPath(id), token);
        OutputRecoveryRecord record;
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            RefreshIndexCore();
            record = GetByIdCached(id)
                ?? throw new FileNotFoundException(LocalizationService.T("OutputRecoveryMissing"));
            taskId = record.TaskId;
            directoryOutput = record.IsDirectory;
            originalCommitOperation = record.OriginalCommitOperation;
        }
        stage = StorageFailureStage.RecoveryVerify;
        Verify(record, record.ArtifactPath, token);
        stage = StorageFailureStage.RecoveryReserve;
        using var destination = OutputPathService.ReserveRecoveryDestination(outputDirectory, record.FileName, record.IsDirectory,
            taskId, operation);
        _recoveryCopyStarting?.Invoke();
        stage = StorageFailureStage.RecoveryCopy;
        CopyArtifact(record.ArtifactPath, destination.TemporaryPath, record.IsDirectory, record.Files, token);
        stage = StorageFailureStage.RecoveryVerify;
        Verify(record, destination.TemporaryPath, token);
        token.ThrowIfCancellationRequested();
        stage = StorageFailureStage.RecoveryCommit;
        destination.Commit();
        destinationCommitted = true;
        // The destination is committed before the original recovery is removed, including across volumes.
        stage = StorageFailureStage.RecoveryCleanup;
        TryDeleteEntry(EntryPath(record.Id));
        lock (Gate) RefreshIndexCore();
        StorageFailureDiagnostics.WriteRecoveryOutcome(operation, record.TaskId, record.IsDirectory,
            StorageFailureDiagnostics.ClassifyPath(outputDirectory), StorageRecoveryState.Saved,
            StorageFailureStage.RecoveryCommit, originalCommitOperation);
        return destination.FinalPath;
        }
        catch (Exception error)
        {
            var recoveryState = destinationCommitted ? StorageRecoveryState.Saved :
                error is OperationCanceledException ? StorageRecoveryState.Cancelled :
                taskId is null ? StorageRecoveryState.NotAttempted : StorageRecoveryState.Indexed;
            StorageFailureDiagnostics.Record(error, new(stage, operation, taskId,
                StorageFailureDiagnostics.ClassifyPath(outputDirectory), directoryOutput,
                Recovery: recoveryState, OriginalCommitOperation: originalCommitOperation));
            StorageFailureDiagnostics.RecoveryOutcome(error, recoveryState);
            // SaveAs has no conversion task log; the fixed line is included only when users include app logs.
            StorageFailureDiagnostics.WriteRecorded(error);
            throw;
        }
    }

    private void PruneCore()
    {
        var deletions = new List<EntryLease>();
        try
        {
            lock (Gate)
            {
                if (!Directory.Exists(_root)) { PublishIndex([]); return; }
                var records = ReadRecords().OrderByDescending(record => record.CreatedAtUtc).ToArray();
                foreach (var record in records.Where((record, index) => index >= MaximumItems || HasExpired(record)))
                    ReserveDeletion(EntryPath(record.Id));
                // Unfinished preservation has no published index. Clean only old app-owned entry directories.
                var indexedIds = records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var entry in Directory.EnumerateDirectories(_root))
                {
                    try
                    {
                        if (!Guid.TryParseExact(Path.GetFileName(entry), "N", out _) || IsReparsePoint(entry)) continue;
                        if (!indexedIds.Contains(Path.GetFileName(entry)) && _clock() - Directory.GetLastWriteTimeUtc(entry) > Retention)
                            ReserveDeletion(entry);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            foreach (var deletion in deletions) TryDeleteEntry(deletion.Path);
            lock (Gate) RefreshIndexCore();
        }
        finally
        {
            foreach (var deletion in deletions) deletion.Dispose();
        }

        void ReserveDeletion(string entry)
        {
            // Save/preserve leases include callers waiting for this entry, so cleanup cannot race them.
            if (!ActiveEntries.ContainsKey(entry)) deletions.Add(AcquireEntry(entry, CancellationToken.None));
        }
    }

    private bool HasExpired(OutputRecoveryRecord record) => _clock() - record.CreatedAtUtc > Retention;
    private OutputRecoveryRecord[] VisibleRecords(IEnumerable<OutputRecoveryRecord> records) =>
        records.OrderByDescending(record => record.CreatedAtUtc).Where(record => !HasExpired(record)).Take(MaximumItems).ToArray();
    private IReadOnlyList<OutputRecoveryRecord> CurrentRecords() =>
        Array.AsReadOnly(Volatile.Read(ref _index).Records.Where(record => !HasExpired(record)).ToArray());
    private void RefreshIndexCore() => PublishIndex(VisibleRecords(ReadRecords()));
    private void PublishIndex(OutputRecoveryRecord[] records)
    {
        Volatile.Write(ref _index, new RecoveryIndex(records,
            records.GroupBy(record => record.TaskId).ToDictionary(group => group.Key, group => group.First()),
            records.ToDictionary(record => record.Id, StringComparer.Ordinal)));
        _indexLoaded = true;
    }

    private sealed record RecoveryIndex(OutputRecoveryRecord[] Records, Dictionary<Guid, OutputRecoveryRecord> ByTask,
        Dictionary<string, OutputRecoveryRecord> ById)
    {
        public static RecoveryIndex Empty { get; } = new([], [], new(StringComparer.Ordinal));
    }

    private sealed class EntryUse
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    private static EntryLease AcquireEntry(string path, CancellationToken token)
    {
        EntryUse usage;
        lock (Gate)
        {
            if (!ActiveEntries.TryGetValue(path, out usage!)) ActiveEntries.Add(path, usage = new());
            usage.References++;
        }
        try { usage.Semaphore.Wait(token); return new EntryLease(path, usage); }
        catch { ReleaseEntry(path, usage, acquired: false); throw; }
    }

    private static void ReleaseEntry(string path, EntryUse usage, bool acquired)
    {
        lock (Gate)
        {
            if (acquired) usage.Semaphore.Release();
            if (--usage.References == 0) { ActiveEntries.Remove(path); usage.Semaphore.Dispose(); }
        }
    }

    private sealed class EntryLease(string path, EntryUse usage) : IDisposable
    {
        public string Path { get; } = path;
        public void Dispose() => ReleaseEntry(Path, usage, acquired: true);
    }

    private IEnumerable<OutputRecoveryRecord> ReadRecords()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var entry in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(entry);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            OutputRecoveryRecord? record;
            try
            {
                if (IsReparsePoint(entry)) continue;
                var metadata = Path.Combine(entry, "entry.json");
                if (!File.Exists(metadata) || IsReparsePoint(metadata)) continue;
                Interlocked.Increment(ref _metadataReadCount);
                record = JsonSerializer.Deserialize<OutputRecoveryRecord>(File.ReadAllText(metadata), JsonOptions);
                if (record is null || record.Id != id || record.TaskId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(record.FileName) || Path.GetFileName(record.FileName) != record.FileName ||
                    record.FileName is "." or ".." || record.Files is null || record.Files.Length == 0 ||
                    record.TargetFormatId is null || record.ConversionMode is null || record.EngineId is null ||
                    record.CreatedAtUtc == default || record.CreatedAtUtc > _clock() + TimeSpan.FromMinutes(5)) continue;
                record = record with { ArtifactPath = Path.Combine(entry, "artifact") };
                if (!(record.IsDirectory ? Directory.Exists(record.ArtifactPath) : File.Exists(record.ArtifactPath))) continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { continue; }
            yield return record;
        }
    }

    private string EntryPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid output recovery ID.", nameof(id));
        return Path.Combine(_root, id);
    }

    private static void Verify(OutputRecoveryRecord record, string path, CancellationToken token)
    {
        if (!record.Files.SequenceEqual(Fingerprint(path, record.IsDirectory, token)))
            throw new InvalidDataException(LocalizationService.T("OutputRecoveryChanged"));
    }

    private static OutputRecoveryFingerprint[] Fingerprint(string path, bool isDirectory, CancellationToken token)
    {
        var files = new List<OutputRecoveryFingerprint>();
        void AddFile(string file, string relative)
        {
            token.ThrowIfCancellationRequested();
            if (IsReparsePoint(file)) throw new InvalidDataException("Output recovery cannot contain links.");
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
            files.Add(new(relative, false, length, Convert.ToHexString(hash.GetHashAndReset())));
        }
        void AddDirectory(string directory)
        {
            token.ThrowIfCancellationRequested();
            if (IsReparsePoint(directory)) throw new InvalidDataException("Output recovery cannot contain links.");
            foreach (var child in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(path, child);
                if (Directory.Exists(child)) { files.Add(new(relative, true, 0, string.Empty)); AddDirectory(child); }
                else AddFile(child, relative);
            }
        }
        if (isDirectory) AddDirectory(path); else AddFile(path, string.Empty);
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static void CopyArtifact(string source, string destination, bool isDirectory, IReadOnlyList<OutputRecoveryFingerprint> files, CancellationToken token)
    {
        if (isDirectory) Directory.CreateDirectory(destination);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var from = isDirectory ? SafeChild(source, file.Path) : source;
            var to = isDirectory ? SafeChild(destination, file.Path) : destination;
            if (file.IsDirectory) { Directory.CreateDirectory(to); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            output.Flush(flushToDisk: true);
        }
    }

    private static string SafeChild(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid output recovery artifact path.");
        return path;
    }
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void TryDeleteArtifact(string path, bool isDirectory)
    {
        try { if (isDirectory) Directory.Delete(path, true); else File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
    private static void TryDeleteEntry(string path)
    {
        try { if (Directory.Exists(path) && !IsReparsePoint(path)) Directory.Delete(path, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
