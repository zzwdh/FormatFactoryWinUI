using System.Collections.ObjectModel;
using System.Diagnostics;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public enum ArchiveSessionNoticeKind { Information, Warning, Error }

/// <summary>
/// Owns archive execution and durable task state without depending on a window.
/// Call operations on the host context; password requests are dispatched back to it.
/// </summary>
public sealed class ArchiveSession : IDisposable
{
    private readonly IArchiveConverter _converter;
    private readonly IArchiveTaskPersistence? _taskStore;
    private readonly IArchiveCommitReceiptStore? _commitReceipts;
    private readonly Func<ConversionHistoryItem, CancellationToken, Task>? _recordHistory;
    private readonly Func<Func<Task>, Task> _dispatch;
    private ArchiveConversionOptions _defaults = new(ArchiveFormat.Zip);
    private bool _defaultsEncrypted;
    private string _outputDirectory = string.Empty;
    private bool _persistenceReady;
    private bool _restoring;
    private bool _running;
    private bool _disposed;
    private Task? _restoreTask;
    private CancellationTokenSource? _saveDelay;
    private Task _pendingSave = Task.CompletedTask;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _batchCancellation;
    private CancellationTokenSource? _itemCancellation;
    private Task? _run;
    private long _runVersion;

    public ArchiveSession(IArchiveConverter? converter = null, IArchiveTaskPersistence? taskStore = null,
        Func<ConversionHistoryItem, CancellationToken, Task>? recordHistory = null,
        IArchiveCommitReceiptStore? commitReceipts = null, Func<Func<Task>, Task>? dispatch = null)
    {
        _converter = converter ?? new ArchiveConversionService();
        _taskStore = taskStore;
        _commitReceipts = taskStore is null ? null : commitReceipts ?? new ArchiveCommitReceiptStore();
        _recordHistory = recordHistory;
        _dispatch = dispatch ?? (action => action());
        _persistenceReady = taskStore is null;
        Tasks.CollectionChanged += (_, _) => QueueCheckpoint();
    }

    public ObservableCollection<ArchiveTask> Tasks { get; } = [];
    public IReadOnlyList<ArchiveTask> LastBatch { get; private set; } = Array.Empty<ArchiveTask>();
    public bool IsRunning => _running;
    public bool IsRestoring => _restoring;
    public bool CanPersist => _persistenceReady;
    public bool IsAvailable => _converter.IsAvailable;
    public ArchiveConversionOptions Defaults => _defaults;
    public bool DefaultEncryptOutput => _defaultsEncrypted;
    public string OutputDirectory
    {
        get => _outputDirectory;
        set { _outputDirectory = value; QueueCheckpoint(); }
    }
    public event Action? Changed;
    public event Action<string, ArchiveSessionNoticeKind>? Status;
    private void NotifyChanged() { if (!_disposed) Changed?.Invoke(); }
    private void NotifyStatus(string key, ArchiveSessionNoticeKind kind) { if (!_disposed) Status?.Invoke(key, kind); }

    public void SetDefaults(ArchiveConversionOptions options, bool encrypted)
    { _defaults = options; _defaultsEncrypted = encrypted; QueueCheckpoint(); }

    public void AddInputs(IEnumerable<string> paths, ArchiveConversionOptions options)
    {
        if (_disposed || IsRunning || _restoring || !_persistenceReady) return;
        foreach (var path in ArchiveInput.NormalizeInputs(paths))
        {
            if (Tasks.Any(task => string.Equals(task.SourcePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            Tasks.Add(new(path, options));
        }
        NotifyChanged();
        QueueCheckpoint();
    }

    public Task StartAsync(Func<ArchiveTask, ArchivePasswordRequest, CancellationToken, Task<string?>> requestPassword)
    {
        ArgumentNullException.ThrowIfNull(requestPassword);
        if (_disposed || IsRunning || _restoring || !_persistenceReady) return _run ?? Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        { NotifyStatus("ArchiveOutputFolderRequired", ArchiveSessionNoticeKind.Warning); return Task.CompletedTask; }
        var pending = Tasks.Where(task => task.State == ArchiveTaskState.Waiting).ToArray();
        if (pending.Length == 0) return Task.CompletedTask;
        if (pending.Any(task => task.HasEncryption && string.IsNullOrEmpty(task.Options.OutputPassword)))
        { NotifyStatus("ArchiveOutputPassword", ArchiveSessionNoticeKind.Warning); return Task.CompletedTask; }
        _batchCancellation = new();
        _running = true;
        _run = RunBatchAsync(pending, OutputDirectory.Trim(), _batchCancellation.Token, ++_runVersion, requestPassword);
        NotifyChanged();
        return _run;
    }

    public Task RestoreAsync(CancellationToken token = default)
    {
        if (_taskStore is null || _disposed) return Task.CompletedTask;
        return _restoreTask ??= RestoreCoreAsync(token);
    }

    private async Task RestoreCoreAsync(CancellationToken token)
    {
        _restoring = true;
        NotifyChanged();
        try
        {
            var snapshot = await _taskStore!.LoadAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            _defaults = new(snapshot.DefaultTargetFormat, snapshot.DefaultStrictVerification);
            _defaultsEncrypted = snapshot.DefaultEncryptOutput;
            _outputDirectory = snapshot.OutputDirectory;
            foreach (var saved in snapshot.Tasks)
            {
                if (!Tasks.Any(task => task.Id == saved.Id ||
                        task.SourcePath.Equals(saved.SourcePath, StringComparison.OrdinalIgnoreCase)))
                {
                    var recovered = saved;
                    if (_commitReceipts is not null && saved.State != ArchiveTaskState.Succeeded)
                    {
                        try { recovered = await _commitReceipts.TryRecoverAsync(saved, token) ?? saved; }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception) { SafeDiagnostics.Report(exception); }
                    }
                    if (_disposed) return;
                    Tasks.Add(ArchiveTask.Restore(recovered));
                }
            }
            _persistenceReady = true;
            foreach (var task in Tasks.Where(task => task.State == ArchiveTaskState.Succeeded && !task.HistoryRecorded).ToArray())
                if (Tasks.Contains(task)) await RecordHistorySafelyAsync(task);
            if (snapshot.Tasks.Count > 0)
                NotifyStatus("ArchiveTasksRestored", ArchiveSessionNoticeKind.Information);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { throw; }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            // Keep the unreadable original untouched and prevent accidental replacement.
            NotifyStatus("ArchiveTaskRestoreFailed", ArchiveSessionNoticeKind.Error);
        }
        finally { _restoring = false; NotifyChanged(); QueueCheckpoint(); }
    }

    public bool AddRestoredInputs(IEnumerable<string> paths, ArchiveConversionOptions options, bool encrypted)
    {
        if (_disposed || IsRunning || _restoring || !_persistenceReady) return false;
        var accepted = false;
        foreach (var path in ArchiveInput.NormalizeInputs(paths))
        {
            var task = Tasks.FirstOrDefault(task => task.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (task?.State == ArchiveTaskState.Succeeded)
            {
                // Repeating a history entry represents a new conversion. Keep its
                // historical result while giving the new task a fresh identity.
                Tasks.Remove(task);
                task = null;
            }
            if (task is null) { task = new(path, options); Tasks.Add(task); }
            else task.SetOptions(options);
            if (encrypted) task.RequireOutputPassword();
            accepted = true;
        }
        NotifyChanged();
        QueueCheckpoint();
        return accepted;
    }

    private ArchiveTaskSnapshot CaptureSnapshot() => new()
    {
        OutputDirectory = OutputDirectory.Trim(),
        DefaultTargetFormat = _defaults.TargetFormat,
        DefaultStrictVerification = _defaults.StrictVerification,
        DefaultEncryptOutput = _defaultsEncrypted,
        Tasks = Tasks.Select(task => task.Snapshot()).ToArray()
    };

    public void QueueCheckpoint()
    {
        if (_disposed || _restoring || !_persistenceReady || _taskStore is null) return;
        _saveDelay?.Cancel();
        _saveDelay?.Dispose();
        _saveDelay = new();
        _pendingSave = SaveAfterDelayAsync(_saveDelay.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            if (!_disposed) await SaveCheckpointSafelyAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task SaveCheckpointSafelyAsync()
    {
        try { await SaveCheckpointAsync(); }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            NotifyStatus("ArchiveTaskSaveFailed", ArchiveSessionNoticeKind.Warning);
        }
    }

    private async Task SaveCompletedCheckpointAsync(ArchiveTask task)
    {
        try
        {
            await SaveCheckpointAsync();
            if (_commitReceipts is not null) await _commitReceipts.ForgetAsync(task.Id);
        }
        catch (Exception exception)
        {
            // Keep the write-ahead receipt if the durable succeeded row could not be saved.
            SafeDiagnostics.Report(exception);
            NotifyStatus("ArchiveTaskSaveFailed", ArchiveSessionNoticeKind.Warning);
        }
    }

    private async Task SaveCheckpointAsync()
    {
        if (_taskStore is null || !_persistenceReady || _restoring) return;
        // Capture after entering the serial writer: an older delayed request cannot
        // overwrite a later completed-task checkpoint.
        await _saveGate.WaitAsync();
        try
        {
            await _taskStore.SaveAsync(CaptureSnapshot());
        }
        finally { _saveGate.Release(); }
    }

    public async Task FlushAsync()
    {
        if (_taskStore is not null) await RestoreAsync();
        _saveDelay?.Cancel();
        await _pendingSave;
        if (_taskStore is null) return;
        if (!_persistenceReady)
        {
            // No edits are permitted before a valid restore; an existing failed
            // load can safely remain on disk without blocking an otherwise idle exit.
            return;
        }
        await SaveCheckpointAsync();
    }

    private async Task RecordHistorySafelyAsync(ArchiveTask task)
    {
        if (_recordHistory is null || task.HistoryRecorded || task.State != ArchiveTaskState.Succeeded ||
            !File.Exists(task.OutputPath)) return;
        try
        {
            var extension = ArchiveFormats.Extension(task.Options.TargetFormat)[1..];
            await _recordHistory(new ConversionHistoryItem
            {
                TaskId = task.Id, InputPaths = new[] { task.SourcePath }, OutputPath = task.OutputPath,
                DisplayName = Path.GetFileName(task.OutputPath), ContentKind = ContentKind.Archive,
                SourceFormatId = SourceFormatId(task.SourcePath), TargetFormatId = extension,
                EngineId = "archive", SnapshotVersion = 1,
                CompletedAtUtc = task.CompletedAtUtc ?? DateTimeOffset.UtcNow,
                ResultStatistics = task.ResultStatistics,
                Options = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["archive.strictVerification"] = task.Options.StrictVerification ? "true" : "false",
                    ["archive.encryptOutput"] = task.HasEncryption ? "true" : "false"
                }
            }, CancellationToken.None);
            task.HistoryRecorded = true;
            task.HistorySaveFailed = false;
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            task.HistorySaveFailed = true;
            NotifyStatus("ArchiveHistorySaveFailed", ArchiveSessionNoticeKind.Warning);
        }
        task.Refresh();
    }

    private async Task RunBatchAsync(ArchiveTask[] pending, string outputDirectory, CancellationToken cancellationToken, long version,
        Func<ArchiveTask, ArchivePasswordRequest, CancellationToken, Task<string?>> requestPassword)
    {
        await Task.Yield();
        try
        {
            foreach (var task in pending)
            {
                if (!Tasks.Contains(task)) continue;
                if (cancellationToken.IsCancellationRequested)
                { task.ClearPassword(); task.SetState(ArchiveTaskState.Cancelled); continue; }
                using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _itemCancellation = itemCancellation;
                task.OutputDirectory = outputDirectory;
                task.SetState(ArchiveTaskState.Running);
                await SaveCheckpointSafelyAsync();
                var timer = Stopwatch.StartNew();
                try
                {
                    var inputBytes = await Task.Run(() => MeasureArchiveSource(task.SourcePath));
                    var progress = new Progress<ArchiveProgress>(value =>
                    {
                        if (_disposed || version != _runVersion || itemCancellation.IsCancellationRequested ||
                            task.State != ArchiveTaskState.Running) return;
                        task.SetProgress(value.Fraction * 100, value.Phase);
                    });
                    var options = task.Options;
                    var admitted = task.Snapshot();
                    task.OutputPath = await Task.Run(() => _converter.ConvertAsync(task.SourcePath, outputDirectory, options,
                        (request, token) => RequestPasswordAsync(task, request, token, requestPassword), progress, itemCancellation.Token,
                        _commitReceipts is null ? null : (staged, final, token) =>
                            _commitReceipts.PrepareAsync(admitted, staged, final, token)), itemCancellation.Token);
                    task.ResultStatistics = new(inputBytes, TryFileSize(task.OutputPath), timer.ElapsedMilliseconds);
                    task.CompletedAtUtc = DateTimeOffset.UtcNow;
                    task.SetState(ArchiveTaskState.Succeeded);
                    await SaveCompletedCheckpointAsync(task);
                    await RecordHistorySafelyAsync(task);
                }
                catch (OperationCanceledException) { task.SetState(ArchiveTaskState.Cancelled); }
                catch (ArchiveConversionException exception)
                { task.SetState(ArchiveTaskState.Failed, ErrorKey(exception.Code)); }
                catch (Exception) { task.SetState(ArchiveTaskState.Failed, "ArchiveErrorEngineFailed"); }
                finally { _itemCancellation = null; task.ClearPassword(); await SaveCheckpointSafelyAsync(); NotifyChanged(); }
            }
        }
        finally
        {
            _defaults = _defaults with { OutputPassword = null };
            _batchCancellation?.Dispose();
            _batchCancellation = null;
            _running = false;
            LastBatch = pending;
            await SaveCheckpointSafelyAsync();
            NotifyChanged();
        }
    }

    private async Task<string?> RequestPasswordAsync(ArchiveTask task, ArchivePasswordRequest request,
        CancellationToken token, Func<ArchiveTask, ArchivePasswordRequest, CancellationToken, Task<string?>> requestPassword)
    {
        string? password = null;
        await _dispatch(async () =>
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            task.SetState(ArchiveTaskState.AwaitingPassword);
            NotifyChanged();
            try { password = await requestPassword(task, request, token); }
            finally
            {
                if (!_disposed && !token.IsCancellationRequested && task.State == ArchiveTaskState.AwaitingPassword)
                    task.SetState(ArchiveTaskState.Running);
                NotifyChanged();
            }
        }).ConfigureAwait(false);
        return password;
    }

    public void CancelOrRemove(ArchiveTask task)
    {
        if (_disposed || !Tasks.Contains(task)) return;
        if (task.State is ArchiveTaskState.Running or ArchiveTaskState.AwaitingPassword)
            _itemCancellation?.Cancel();
        else { task.ClearPassword(); Tasks.Remove(task); QueueCheckpoint(); }
        NotifyChanged();
    }

    public void ClearFinished()
    {
        if (_disposed || IsRunning) return;
        foreach (var task in Tasks.Where(task => task.State is ArchiveTaskState.Succeeded or ArchiveTaskState.Failed or ArchiveTaskState.Cancelled).ToArray())
        { task.ClearPassword(); Tasks.Remove(task); }
        _defaults = _defaults with { OutputPassword = null };
        QueueCheckpoint();
        NotifyChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveDelay?.Cancel();
        _batchCancellation?.Cancel();
        foreach (var task in Tasks) task.ClearPassword();
        _defaults = _defaults with { OutputPassword = null };
    }

    public async Task StopAsync(bool preserveForRestart = false)
    {
        // An explicit user cancellation wins over a later application exit.
        var interrupted = preserveForRestart && _batchCancellation?.IsCancellationRequested != true
            ? Tasks.Where(task => task.State == ArchiveTaskState.Waiting ||
                task.State is ArchiveTaskState.Running or ArchiveTaskState.AwaitingPassword &&
                _itemCancellation?.IsCancellationRequested != true).ToArray()
            : Array.Empty<ArchiveTask>();
        _batchCancellation?.Cancel();
        if (_run is not null) await _run;
        foreach (var task in interrupted.Where(Tasks.Contains))
        {
            task.ClearPassword();
            if (task.State != ArchiveTaskState.Cancelled) continue;
            task.SetProgress(0, string.Empty);
            task.SetState(ArchiveTaskState.Waiting);
        }
        NotifyChanged();
        await FlushAsync();
    }

    private static string SourceFormatId(string path)
    {
        var name = path.ToLowerInvariant();
        foreach (var suffix in new[] { "tar.gz", "tar.xz", "tgz", "txz", "7z", "zip", "rar", "tar" })
            if (name.EndsWith("." + suffix, StringComparison.Ordinal)) return suffix;
        return "archive";
    }

    private static long? TryFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    private static long? MeasureArchiveSource(string primary)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var count = 0;
            long total = 0;
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(primary)!))
            {
                if (++count > 10000 || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2)) return null;
                if (!ArchiveInput.IsSupported(path) ||
                    !ArchiveInput.GetPrimaryPath(path).Equals(primary, StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                total = checked(total + new FileInfo(path).Length);
            }
            return count > 0 && File.Exists(primary) ? total : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return null; }
    }
    private static string ErrorKey(ArchiveFailureCode code) => code switch
    {
        ArchiveFailureCode.EngineUnavailable => "ArchiveErrorEngineUnavailable",
        ArchiveFailureCode.UnsupportedInput => "ArchiveErrorUnsupportedInput",
        ArchiveFailureCode.MissingVolume => "ArchiveErrorMissingVolume",
        ArchiveFailureCode.PasswordRequired => "ArchiveErrorPasswordRequired",
        ArchiveFailureCode.PasswordSkipped => "ArchiveErrorPasswordSkipped",
        ArchiveFailureCode.CorruptArchive => "ArchiveErrorCorruptArchive",
        ArchiveFailureCode.UnsafeEntry => "ArchiveErrorUnsafeEntry",
        ArchiveFailureCode.InsufficientSpace => "ArchiveErrorInsufficientSpace",
        ArchiveFailureCode.ValidationFailed => "ArchiveErrorValidationFailed",
        ArchiveFailureCode.InvalidOptions => "ArchiveErrorInvalidOptions",
        ArchiveFailureCode.OutputUnavailable => "ArchiveErrorOutputUnavailable",
        _ => "ArchiveErrorEngineFailed"
    };
}
