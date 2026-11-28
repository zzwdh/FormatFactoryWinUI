using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum QueueLoadStatus { Missing, Success, CorruptRecovered, FutureVersion, IoFailure }

public sealed record QueueLoadResult(QueueLoadStatus Status, IReadOnlyList<PersistedQueueItem> Items,
    Exception? Error = null, string? QuarantinePath = null)
{
    public bool CanSave => Status is not (QueueLoadStatus.FutureVersion or QueueLoadStatus.IoFailure);
}

public interface IQueuePersistence
{
    Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default);
}

public sealed class QueueStore : IQueuePersistence
{
    internal const int CurrentSchemaVersion = 4;
    private static readonly TimeSpan FileLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Func<IReadOnlyList<PersistedQueueItem>> _checkpointLoader;
    private readonly Func<string, Stream> _openRead;
    private readonly HashSet<Guid> _knownPersistedPaths = [];
    private readonly object _saveSync = new();
    private PendingSave? _pendingSave;
    private bool _saveWriterRunning;
    private bool _baselineInitialized;
    private bool _writesBlocked;

    public QueueStore()
        : this(Path.Combine(LogService.AppDataRoot, "pending-queue.json"))
    {
    }

    internal QueueStore(string path, Func<IReadOnlyList<PersistedQueueItem>>? checkpointLoader = null,
        Func<string, Stream>? openRead = null)
    {
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        _checkpointLoader = checkpointLoader ?? RecoveryService.LoadCheckpoint;
        _openRead = openRead ?? File.OpenRead;
    }

    public async Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture nested inputs/options before the caller can edit its models.
        var incoming = tasks
            .Where(task => task.State != ConversionState.Completed ||
                task.OutputGroupId is not null && !task.OutputGroupFinalized)
            .Select(ToPersistedItem)
            .ToArray();

        Task completion;
        lock (_saveSync)
        {
            if (_pendingSave is null)
            {
                _pendingSave = new PendingSave(incoming);
            }
            else
            {
                _pendingSave.Items = incoming;
            }
            completion = _pendingSave.Completion.Task;
            if (!_saveWriterRunning)
            {
                _saveWriterRunning = true;
                _ = Task.Run(DrainSavesAsync);
            }
        }

        // Cancellation stops this caller's wait, not an already accepted write.
        // A coalesced batch completes only after its latest snapshot is durable.
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainSavesAsync()
    {
        while (true)
        {
            PendingSave batch;
            lock (_saveSync)
            {
                if (_pendingSave is null)
                {
                    _saveWriterRunning = false;
                    return;
                }
                batch = _pendingSave;
                _pendingSave = null;
            }

            var saveStarted = Stopwatch.GetTimestamp();
            var saved = false;
            try
            {
                await SaveSnapshotAsync(batch.Items, CancellationToken.None).ConfigureAwait(false);
                saved = true;
                batch.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                batch.Completion.TrySetException(ex);
            }
            finally
            {
                NativeDiagnostics.Log.PersistenceCompleted("queue", saved, (long)Stopwatch.GetElapsedTime(saveStarted).TotalMilliseconds);
            }
        }
    }

    private async Task SaveSnapshotAsync(PersistedQueueItem[] incoming, CancellationToken cancellationToken)
    {
        await ProcessGate.WaitAsync(cancellationToken);
        try
        {
            if (_writesBlocked) throw new InvalidOperationException(LocalizationService.T("ConversionUiQueueSaveUnavailable"));
            using var transaction = await StoreTransaction.OpenAsync(_path, cancellationToken).ConfigureAwait(false);
            var current = await LoadCoreAsync(cancellationToken);
            var merged = await transaction.SelectAsync("snapshot", current, incoming,
                item => transaction.Identify(item.TaskId, string.Empty), _baselineInitialized ? _knownPersistedPaths : [],
                cancellationToken).ConfigureAwait(false);
            RecoveryService.UpdateCheckpoint(merged);
            await PublishAsync(transaction, merged, cancellationToken).ConfigureAwait(false);
            RecoveryService.ClearCheckpoint();
            ReplaceKnownPaths(incoming);
            _baselineInitialized = true;
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistedQueueItem>> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadResultAsync(cancellationToken).ConfigureAwait(false)).Items;

    public async Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default)
    {
        await ProcessGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            QueueLoadResult result;
            try
            {
                var items = await LoadCoreAsync(cancellationToken, throwIfMissing: true);
                result = new(QueueLoadStatus.Success, items);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                result = new(QueueLoadStatus.Missing, LoadCheckpointSafely());
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // Move the exact unreadable bytes aside under the same interprocess lock.
                // Only a successful quarantine permits writing a replacement queue.
                var quarantine = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                File.Move(_path, quarantine);
                result = new(QueueLoadStatus.CorruptRecovered,
                    LoadCheckpointSafely().Select(MigrateItem).ToArray(), exception, quarantine);
                SafeDiagnostics.Report(exception);
            }
            _writesBlocked = false;
            ReplaceKnownPaths(result.Items);
            _baselineInitialized = true;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogQueueRestoreFailed", ex.Message));
            SafeDiagnostics.Report(ex);
            _writesBlocked = true;
            return new(ex is NotSupportedException ? QueueLoadStatus.FutureVersion : QueueLoadStatus.IoFailure,
                LoadCheckpointSafely(), ex);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    private IReadOnlyList<PersistedQueueItem> LoadCheckpointSafely()
    {
        try
        {
            var result = _checkpointLoader().Select(MigrateItem).ToArray();
            return result;
        }
        catch (Exception exception) { SafeDiagnostics.Report(exception); return []; }
    }

    public async Task MergePendingAsync(
        IEnumerable<PersistedQueueItem> additions,
        CancellationToken cancellationToken = default)
    {
        var pending = additions.Select(MigrateItem).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        await ProcessGate.WaitAsync(cancellationToken);
        try
        {
            if (_writesBlocked) throw new InvalidOperationException(LocalizationService.T("ConversionUiQueueSaveUnavailable"));
            using var transaction = await StoreTransaction.OpenAsync(_path, cancellationToken).ConfigureAwait(false);
            var current = await LoadCoreAsync(cancellationToken);
            var merged = await transaction.SelectAsync("discover", current, pending,
                item => transaction.Identify(item.TaskId, SourcePathKey(item), item.OutputGroupId is not null),
                [], cancellationToken).ConfigureAwait(false);
            await PublishAsync(transaction, merged, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    private async Task<IReadOnlyList<PersistedQueueItem>> LoadCoreAsync(CancellationToken cancellationToken,
        bool throwIfMissing = false)
    {
        Stream source;
        try { source = _openRead(_path); }
        catch (Exception exception) when (!throwIfMissing && (exception is FileNotFoundException or DirectoryNotFoundException))
        {
            return [];
        }
        await using var stream = source;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        PersistedQueueItem[] items;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            items = document.RootElement.Deserialize<PersistedQueueItem[]>(SerializerOptions) ?? [];
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var version = document.RootElement.EnumerateObject().FirstOrDefault(property =>
                property.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase));
            if (version.Value.ValueKind == JsonValueKind.Number && version.Value.TryGetInt64(out var schema) && schema > CurrentSchemaVersion)
                throw new NotSupportedException(LocalizationService.T("GeneralServiceQueueUnsupportedVersion", schema));
            var queue = document.RootElement.Deserialize<PersistedQueueDocument>(SerializerOptions)
                ?? throw new InvalidDataException(LocalizationService.T("GeneralServiceQueueInvalidStructure"));
            if (queue.SchemaVersion > CurrentSchemaVersion)
            {
                throw new NotSupportedException(LocalizationService.T("GeneralServiceQueueUnsupportedVersion", queue.SchemaVersion));
            }
            items = queue.Items ?? throw new InvalidDataException(LocalizationService.T("GeneralServiceQueueInvalidStructure"));
        }
        else
        {
            throw new InvalidDataException(LocalizationService.T("GeneralServiceQueueInvalidRoot"));
        }

        if (items.Any(item => item is null ||
            (string.IsNullOrWhiteSpace(item.InputPath) && (item.Inputs is null || item.Inputs.Count == 0)) ||
            item.Inputs is not null && item.Inputs.Any(input => input is null || string.IsNullOrWhiteSpace(input.Path))))
            throw new InvalidDataException(LocalizationService.T("GeneralServiceQueueInvalidStructure"));
        return items.Select(MigrateItem).ToArray();
    }

    private static async Task PublishAsync(StoreTransaction transaction, PersistedQueueItem[] items, CancellationToken token)
    {
        try
        {
            await transaction.PublishAsync(items.Length == 0 ? null :
                new PersistedQueueDocument { SchemaVersion = CurrentSchemaVersion, Items = items },
                SerializerOptions, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PersistenceFailureDiagnostics.Record(error, DiagnosticStore.Queue, DiagnosticPersistenceStage.CommitSnapshot);
            throw;
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var stopwatch = Stopwatch.StartNew();
        IOException? lastError = null;
        while (stopwatch.Elapsed < FileLockTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return PersistenceFileLock.Open(_lockPath, "queue");
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(50, cancellationToken);
            }
        }

        var failure = new TimeoutException(LocalizationService.T("GeneralServiceQueueLockTimeout", _lockPath), lastError);
        PersistenceFailureDiagnostics.Record(failure, DiagnosticStore.Queue, DiagnosticPersistenceStage.AcquireLock,
            elapsedMs: (long)stopwatch.Elapsed.TotalMilliseconds);
        throw failure;
    }

    private void ReplaceKnownPaths(IEnumerable<PersistedQueueItem> items)
    {
        _knownPersistedPaths.Clear();
        foreach (var item in items)
        {
            _knownPersistedPaths.Add(item.TaskId);
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string SourcePathKey(PersistedQueueItem item)
    {
        var paths = item.Inputs is { Count: > 0 }
            ? item.Inputs.Select(input => input.Path)
            : string.IsNullOrWhiteSpace(item.InputPath) ? [] : [item.InputPath];
        return string.Join('\u001f', paths.Select(NormalizePath));
    }

    internal static PersistedQueueItem ToPersistedItem(MediaTask task) => new()
    {
        TaskId = task.Id,
        OutputGroupId = task.OutputGroupId,
        OutputConfigurationId = task.OutputConfigurationId,
        OutputLabel = task.OutputLabel,
        OutputOrder = task.OutputOrder,
        OutputExpectedCount = task.OutputExpectedCount,
        OutputGroupFinalized = task.OutputGroupFinalized,
        OutputHistoryRecorded = task.OutputHistoryRecorded,
        OutputFingerprint = task.OutputFingerprint,
        ResultStatistics = task.ResultStatistics,
        OutputPath = task.OutputPath,
        InputPath = task.InputPath,
        SourceRoot = task.SourceRoot,
        InputAccessToken = task.InputAccessToken,
        SourceRootAccessToken = task.SourceRootAccessToken,
        Inputs = task.Inputs.Count > 0
            ? task.Inputs.Select(input => new PersistedConversionInput
            {
                Path = input.Path,
                FormatId = input.FormatId,
                AccessToken = input.AccessToken
            }).ToList()
            : string.IsNullOrWhiteSpace(task.InputPath)
                ? []
                : [new PersistedConversionInput
                {
                    Path = task.InputPath,
                    FormatId = string.IsNullOrWhiteSpace(task.SourceFormatId)
                        ? FormatCatalog.FindByExtension(task.InputPath)?.Id ?? string.Empty
                        : task.SourceFormatId,
                    AccessToken = task.InputAccessToken
                }],
        ContentKind = task.ContentKind,
        SourceFormatId = string.IsNullOrWhiteSpace(task.SourceFormatId)
            ? FormatCatalog.FindByExtension(task.InputPath)?.Id ?? string.Empty
            : task.SourceFormatId,
        TargetFormat = task.TargetFormat,
        TargetFormatId = task.TargetFormatId,
        EngineId = task.EngineId,
        ConversionMode = task.ConversionMode,
        ParametersVersion = task.ParametersVersion,
        Options = task.Options
            .Where(option => !option.Key.Equals("password", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(option => option.Key, option => option.Value, StringComparer.OrdinalIgnoreCase),
        SelectedAudioTrack = task.SelectedAudioTrack,
        State = task.State,
        Message = task.Message,
        LastError = task.LastError,
        RecoverableOutputId = task.RecoverableOutputId,
        PreservedOutputPath = task.PreservedOutputPath,
        BlockedReason = task.BlockedReason,
        LastAttemptAtUtc = task.LastAttemptAtUtc,
        StateBeforeBlocked = task.StateBeforeBlocked
    };

    private static PersistedQueueItem MigrateItem(PersistedQueueItem item)
    {
        if (item.TaskId == Guid.Empty)
        {
            // Legacy records have no ID. Derive it deterministically so repeated
            // reads during a migration do not invent an external duplicate.
            var identity = SourcePathKey(item) + '\u001e' + FormatCatalog.NormalizeFormatId(
                string.IsNullOrWhiteSpace(item.TargetFormatId) ? item.TargetFormat : item.TargetFormatId)
                + '\u001e' + NormalizeConversionMode(item.ConversionMode)
                + '\u001e' + item.SelectedAudioTrack
                + '\u001e' + string.Join('\u001e', (item.Options ?? []).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key.ToLowerInvariant() + "=" + pair.Value));
            item.TaskId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
        }
        item.Inputs ??= [];
        if (item.Inputs.Count == 0 && !string.IsNullOrWhiteSpace(item.InputPath))
        {
            item.Inputs.Add(new PersistedConversionInput
            {
                Path = item.InputPath,
                FormatId = item.SourceFormatId,
                AccessToken = item.InputAccessToken
            });
        }
        if (string.IsNullOrWhiteSpace(item.InputPath) && item.Inputs.Count > 0)
        {
            item.InputPath = item.Inputs[0].Path;
            item.InputAccessToken = item.Inputs[0].AccessToken;
        }

        foreach (var input in item.Inputs)
        {
            input.FormatId = FormatCatalog.NormalizeFormatId(
                string.IsNullOrWhiteSpace(input.FormatId)
                    ? FormatCatalog.FindByExtension(input.Path)?.Id
                    : input.FormatId);
        }

        var source = FormatCatalog.FindById(item.SourceFormatId)
            ?? FormatCatalog.FindByExtension(item.InputPath);
        item.SourceFormatId = source?.Id ?? FormatCatalog.NormalizeFormatId(item.SourceFormatId);
        item.ContentKind ??= source?.ContentKind;

        var target = FormatCatalog.FindById(
            string.IsNullOrWhiteSpace(item.TargetFormatId) ? item.TargetFormat : item.TargetFormatId);
        item.TargetFormatId = target?.Id ?? FormatCatalog.NormalizeFormatId(item.TargetFormat);
        if (target is not null)
        {
            item.TargetFormat = target.DisplayName;
        }
        item.EngineId = FormatCatalog.NormalizeEngineId(
            string.IsNullOrWhiteSpace(item.EngineId)
                ? target?.DefaultEngineId ?? source?.DefaultEngineId
                : item.EngineId);
        if (item.ContentKind == ContentKind.Disc)
        {
            item.EngineId = "disc-pipeline";
        }
        item.ConversionMode = NormalizeConversionMode(item.ConversionMode);
        item.ParametersVersion = Math.Max(1, item.ParametersVersion);
        item.Options = new Dictionary<string, string>(item.Options ?? [], StringComparer.OrdinalIgnoreCase);
        item.Options.Remove("password");
        item.OutputLabel ??= string.Empty;
        item.OutputPath ??= string.Empty;
        if (item.OutputGroupId == Guid.Empty || item.OutputConfigurationId == Guid.Empty ||
            item.OutputOrder < 0 || item.OutputExpectedCount < 0 ||
            item.OutputGroupId is not null && (item.OutputConfigurationId is null || item.OutputExpectedCount == 0) ||
            item.OutputFingerprint is { Length: < 0 } ||
            item.ResultStatistics is { } statistics &&
                (statistics.InputBytes < 0 || statistics.OutputBytes < 0 || statistics.ElapsedMilliseconds < 0))
            throw new InvalidDataException("Invalid persisted output group.");
        if (item.State is ConversionState.Running or ConversionState.Probing) item.State = ConversionState.Waiting;
        if (item.StateBeforeBlocked is ConversionState.Running or ConversionState.Probing) item.StateBeforeBlocked = ConversionState.Waiting;
        if (!Enum.IsDefined(item.State) || (item.StateBeforeBlocked is { } priorState && !Enum.IsDefined(priorState)))
            throw new InvalidDataException("Invalid persisted task state.");
        return item;
    }

    private static string NormalizeConversionMode(string? mode) => mode switch
    {
        "快速" or "fast" => "fast",
        "高质量" or "quality" => "quality",
        "无损" or "lossless" => "lossless",
        "平衡" or "balanced" => "balanced",
        _ => FormatCatalog.DefaultConversionMode
    };

    private sealed class PendingSave(PersistedQueueItem[] items)
    {
        public PersistedQueueItem[] Items { get; set; } = items;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PersistedQueueDocument
    {
        public int SchemaVersion { get; set; }
        public PersistedQueueItem[]? Items { get; set; }
    }
}

public sealed class PersistedQueueItem
{
    public Guid TaskId { get; set; }
    public Guid? OutputGroupId { get; set; }
    public Guid? OutputConfigurationId { get; set; }
    public string OutputLabel { get; set; } = string.Empty;
    public int OutputOrder { get; set; }
    public int OutputExpectedCount { get; set; }
    public bool OutputGroupFinalized { get; set; }
    public bool OutputHistoryRecorded { get; set; }
    public AutomationFileFingerprint? OutputFingerprint { get; set; }
    public ConversionResultStatistics? ResultStatistics { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public string InputPath { get; set; } = string.Empty;
    public string? SourceRoot { get; set; }
    public string? InputAccessToken { get; set; }
    public string? SourceRootAccessToken { get; set; }
    public List<PersistedConversionInput> Inputs { get; set; } = [];
    public ContentKind? ContentKind { get; set; }
    public string SourceFormatId { get; set; } = string.Empty;
    public string TargetFormat { get; set; } = string.Empty;
    public string TargetFormatId { get; set; } = string.Empty;
    public string EngineId { get; set; } = string.Empty;
    public string ConversionMode { get; set; } = "balanced";
    public int ParametersVersion { get; set; } = 1;
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int SelectedAudioTrack { get; set; }
    public ConversionState State { get; set; } = ConversionState.Waiting;
    public string Message { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public string? RecoverableOutputId { get; set; }
    public string? PreservedOutputPath { get; set; }
    public string BlockedReason { get; set; } = string.Empty;
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public ConversionState? StateBeforeBlocked { get; set; }
}

public sealed class PersistedConversionInput
{
    public string Path { get; set; } = string.Empty;
    public string FormatId { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
}
