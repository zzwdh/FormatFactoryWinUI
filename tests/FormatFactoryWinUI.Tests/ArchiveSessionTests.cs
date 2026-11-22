using System.Collections.Concurrent;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ArchiveSessionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FailedSucceededCheckpointRetainsReceiptAndSuccessfulOutput()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateSourceAsync(directory.Path, "source.zip");
        var persistence = new MemoryPersistence();
        var receipts = new MemoryReceipts();
        var converter = new DelegateConverter(PublishAsync);
        var notices = new ConcurrentQueue<string>();
        using var session = new ArchiveSession(converter, persistence, commitReceipts: receipts);
        session.Status += (key, _) => notices.Enqueue(key);
        await session.RestoreAsync();
        session.OutputDirectory = directory.Path;
        session.AddInputs([source], new(ArchiveFormat.SevenZip, true, "memory-only-output-secret"));
        persistence.SaveFailure = snapshot => snapshot.Tasks.Any(task => task.State == ArchiveTaskState.Succeeded)
            ? new IOException("Synthetic completed checkpoint failure.") : null;

        await session.StartAsync(NoPassword).WaitAsync(Deadline);

        var task = Assert.Single(session.Tasks);
        Assert.Equal(ArchiveTaskState.Succeeded, task.State);
        Assert.True(File.Exists(task.OutputPath));
        Assert.True(receipts.Prepared.ContainsKey(task.Id));
        Assert.Empty(receipts.Forgotten);
        Assert.Contains("ArchiveTaskSaveFailed", notices);
        Assert.True(task.HasEncryption);
        Assert.Null(task.Options.OutputPassword);
        persistence.SaveFailure = null;
        await session.FlushAsync();
        Assert.Equal(ArchiveTaskState.Succeeded, Assert.Single(persistence.Current.Tasks).State);
        Assert.True(receipts.Prepared.ContainsKey(task.Id));
    }

    [Fact]
    public async Task HistoryFailureKeepsSuccessAndRestoreRetriesHistoryWithoutReconversion()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateSourceAsync(directory.Path, "source.zip");
        var persistence = new MemoryPersistence();
        var receipts = new MemoryReceipts();
        var converter = new DelegateConverter(PublishAsync);
        var historyAttempts = new List<ConversionHistoryItem>();
        Guid taskId;
        using (var first = new ArchiveSession(converter, persistence, (item, _) =>
        {
            historyAttempts.Add(item);
            throw new IOException("Synthetic unavailable history store.");
        }, receipts))
        {
            await first.RestoreAsync();
            first.OutputDirectory = directory.Path;
            first.AddInputs([source], new(ArchiveFormat.SevenZip, true, "never-record-this-password"));
            await first.StartAsync(NoPassword).WaitAsync(Deadline);
            var task = Assert.Single(first.Tasks);
            taskId = task.Id;
            Assert.Equal(ArchiveTaskState.Succeeded, task.State);
            Assert.True(task.HistorySaveFailed);
            Assert.False(task.HistoryRecorded);
            await first.FlushAsync();
        }
        Assert.False(Assert.Single(persistence.Current.Tasks).HistoryRecorded);
        var recoveredHistory = new List<ConversionHistoryItem>();
        using var restored = new ArchiveSession(converter, persistence, (item, _) =>
        {
            recoveredHistory.Add(item);
            return Task.CompletedTask;
        }, receipts);

        await restored.RestoreAsync();
        await restored.FlushAsync();

        var recovered = Assert.Single(restored.Tasks);
        Assert.Equal(taskId, recovered.Id);
        Assert.Equal(ArchiveTaskState.Succeeded, recovered.State);
        Assert.True(recovered.HistoryRecorded);
        Assert.False(recovered.HistorySaveFailed);
        Assert.True(Assert.Single(persistence.Current.Tasks).HistoryRecorded);
        Assert.Equal(1, converter.Calls);
        Assert.Single(historyAttempts);
        var history = Assert.Single(recoveredHistory);
        Assert.Equal(taskId, history.TaskId);
        Assert.Equal("true", history.Options[ArchiveHistoryReplayService.EncryptOutputKey]);
        Assert.Equal("true", history.Options[ArchiveHistoryReplayService.StrictVerificationKey]);
        Assert.DoesNotContain(history.Options.Keys, key => key.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(history.Options.Values, value => value.Contains("never-record", StringComparison.Ordinal));
        Assert.Null(recovered.Options.OutputPassword);
        Assert.True(recovered.HasEncryption);
    }

    [Fact]
    public async Task FailedRestoreBlocksNewWorkAndNeverOverwritesOriginalSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateSourceAsync(directory.Path, "source.zip");
        var original = new ArchiveTaskSnapshot { OutputDirectory = directory.Path,
            Tasks = [new() { SourcePath = source, EncryptOutput = true, TargetFormat = ArchiveFormat.SevenZip }] };
        var persistence = new MemoryPersistence(original) { LoadFailure = new InvalidDataException("Unsupported snapshot.") };
        var converter = new DelegateConverter(PublishAsync);
        using var session = new ArchiveSession(converter, persistence, commitReceipts: new MemoryReceipts());
        var notices = new List<string>();
        session.Status += (key, _) => notices.Add(key);

        await session.RestoreAsync();
        session.OutputDirectory = directory.Path;
        session.AddInputs([source], new(ArchiveFormat.Zip));
        Assert.False(session.AddRestoredInputs([source], new(ArchiveFormat.SevenZip), true));
        await session.StartAsync(NoPassword);
        await session.FlushAsync();
        await session.StopAsync(preserveForRestart: true);

        Assert.False(session.CanPersist);
        Assert.False(session.IsRestoring);
        Assert.Empty(session.Tasks);
        Assert.Equal(0, converter.Calls);
        Assert.Empty(persistence.Writes);
        Assert.Equal(original, persistence.Current);
        Assert.Contains("ArchiveTaskRestoreFailed", notices);
    }

    [Fact]
    public async Task ExitPreservesInterruptedAndQueuedWorkButClearsEveryOutputPassword()
    {
        using var directory = new TemporaryDirectory();
        var first = await CreateSourceAsync(directory.Path, "first.zip");
        var second = await CreateSourceAsync(directory.Path, "second.zip");
        var started = Signal();
        var converter = new DelegateConverter(async call =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return string.Empty;
        });
        var persistence = new MemoryPersistence();
        using var session = new ArchiveSession(converter, persistence, commitReceipts: new MemoryReceipts());
        await session.RestoreAsync();
        session.OutputDirectory = directory.Path;
        session.SetDefaults(new(ArchiveFormat.SevenZip, true, "temporary-default-secret"), true);
        session.AddInputs([first, second], session.Defaults);
        var run = session.StartAsync(NoPassword);
        await started.Task.WaitAsync(Deadline);

        await session.StopAsync(preserveForRestart: true).WaitAsync(Deadline);
        await run.WaitAsync(Deadline);

        Assert.False(session.IsRunning);
        Assert.All(session.Tasks, task =>
        {
            Assert.Equal(ArchiveTaskState.Waiting, task.State);
            Assert.Equal(0, task.Progress);
            Assert.True(task.HasEncryption);
            Assert.Null(task.Options.OutputPassword);
        });
        Assert.All(persistence.Current.Tasks, task =>
        {
            Assert.Equal(ArchiveTaskState.Waiting, task.State);
            Assert.True(task.EncryptOutput);
        });
        Assert.Null(session.Defaults.OutputPassword);
        Assert.True(session.DefaultEncryptOutput);
        Assert.Equal(1, converter.Calls);
    }

    [Fact]
    public async Task ExplicitCancellationRemainsCancelledWhenApplicationExitsLater()
    {
        using var directory = new TemporaryDirectory();
        var first = await CreateSourceAsync(directory.Path, "first.zip");
        var second = await CreateSourceAsync(directory.Path, "second.zip");
        var started = Signal();
        var converter = new DelegateConverter(async call =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return string.Empty;
        });
        var persistence = new MemoryPersistence();
        using var session = new ArchiveSession(converter, persistence, commitReceipts: new MemoryReceipts());
        await session.RestoreAsync();
        session.OutputDirectory = directory.Path;
        session.AddInputs([first, second], new(ArchiveFormat.SevenZip, OutputPassword: "temporary-output-secret"));
        var run = session.StartAsync(NoPassword);
        await started.Task.WaitAsync(Deadline);

        await session.StopAsync().WaitAsync(Deadline);
        await session.StopAsync(preserveForRestart: true).WaitAsync(Deadline);
        await run.WaitAsync(Deadline);

        Assert.All(session.Tasks, task =>
        {
            Assert.Equal(ArchiveTaskState.Cancelled, task.State);
            Assert.Null(task.Options.OutputPassword);
            Assert.True(task.HasEncryption);
        });
        Assert.All(persistence.Current.Tasks, task => Assert.Equal(ArchiveTaskState.Cancelled, task.State));
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task CancellingCurrentItemAllowsNextItemToComplete()
    {
        using var directory = new TemporaryDirectory();
        var first = await CreateSourceAsync(directory.Path, "first.zip");
        var second = await CreateSourceAsync(directory.Path, "second.zip");
        var started = Signal();
        var converter = new DelegateConverter(async call =>
        {
            if (call.SourcePath == first)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.Token);
            }
            return await PublishAsync(call);
        });
        using var session = new ArchiveSession(converter);
        session.OutputDirectory = directory.Path;
        session.AddInputs([first, second], new(ArchiveFormat.Zip));
        var firstTask = session.Tasks[0];
        var run = session.StartAsync(NoPassword);
        await started.Task.WaitAsync(Deadline);

        session.CancelOrRemove(firstTask);
        await run.WaitAsync(Deadline);

        Assert.Equal(2, session.Tasks.Count);
        Assert.Equal(ArchiveTaskState.Cancelled, firstTask.State);
        Assert.Equal(ArchiveTaskState.Succeeded, session.Tasks[1].State);
        Assert.True(File.Exists(session.Tasks[1].OutputPath));
        Assert.Equal(2, converter.Calls);
        Assert.False(session.IsRunning);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task CancellingPasswordRequestUsesHostDispatchAndCannotReviveCancelledTask()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateSourceAsync(directory.Path, "encrypted.zip");
        var promptOpened = Signal();
        var dispatchActive = new AsyncLocal<bool>();
        var converter = new DelegateConverter(async call =>
        {
            await call.RequestPassword(new(call.SourcePath, false, 1), call.Token);
            return await PublishAsync(call);
        });
        using var session = new ArchiveSession(converter, dispatch: async action =>
        {
            var previous = dispatchActive.Value;
            try { dispatchActive.Value = true; await action(); }
            finally { dispatchActive.Value = previous; }
        });
        session.OutputDirectory = directory.Path;
        session.AddInputs([source], new(ArchiveFormat.Zip));
        var task = Assert.Single(session.Tasks);
        var states = new ConcurrentQueue<ArchiveTaskState>();
        task.PropertyChanged += (_, _) => states.Enqueue(task.State);
        var run = session.StartAsync(async (requestTask, _, token) =>
        {
            Assert.True(dispatchActive.Value);
            Assert.Same(task, requestTask);
            Assert.Equal(ArchiveTaskState.AwaitingPassword, task.State);
            promptOpened.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "never-returned-source-secret";
        });
        await promptOpened.Task.WaitAsync(Deadline);

        await session.StopAsync().WaitAsync(Deadline);
        await run.WaitAsync(Deadline);

        Assert.Equal(ArchiveTaskState.Cancelled, task.State);
        var afterPrompt = states.SkipWhile(state => state != ArchiveTaskState.AwaitingPassword).ToArray();
        Assert.Contains(ArchiveTaskState.AwaitingPassword, afterPrompt);
        Assert.DoesNotContain(ArchiveTaskState.Running, afterPrompt);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.staged"));
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task LateProgressFromCancelledAttemptCannotChangeRetryOrCompletedResult()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateSourceAsync(directory.Path, "source.zip");
        var firstStarted = Signal();
        var retryStarted = Signal();
        var finishRetry = Signal();
        IProgress<ArchiveProgress>? staleProgress = null;
        var attempts = 0;
        var converter = new DelegateConverter(async call =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                staleProgress = call.Progress;
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.Token);
            }
            retryStarted.TrySetResult();
            await finishRetry.Task.WaitAsync(call.Token);
            return await PublishAsync(call);
        });
        using var session = new ArchiveSession(converter);
        session.OutputDirectory = directory.Path;
        session.AddInputs([source], new(ArchiveFormat.Zip));
        var task = Assert.Single(session.Tasks);
        var firstRun = StartWithSynchronousProgress(session);
        await firstStarted.Task.WaitAsync(Deadline);
        session.CancelOrRemove(task);
        await firstRun.WaitAsync(Deadline);
        Assert.Equal(ArchiveTaskState.Cancelled, task.State);
        task.SetOptions(new(ArchiveFormat.Zip));
        var retry = StartWithSynchronousProgress(session);
        await retryStarted.Task.WaitAsync(Deadline);

        Assert.NotNull(staleProgress);
        staleProgress.Report(new(0.97, "stale-cancelled-attempt"));
        Assert.Equal(ArchiveTaskState.Running, task.State);
        Assert.Equal(0, task.Progress);
        Assert.NotEqual("stale-cancelled-attempt", task.Phase);
        finishRetry.TrySetResult();
        await retry.WaitAsync(Deadline);
        staleProgress.Report(new(0.11, "late-after-success"));

        Assert.Equal(ArchiveTaskState.Succeeded, task.State);
        Assert.Equal(100, task.Progress);
        Assert.Null(task.Phase);
        Assert.Equal(2, converter.Calls);
    }

    [Fact]
    public async Task QueuedSaveCapturesCurrentStateAfterAnEarlierWriterFinishes()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateSourceAsync(directory.Path, "source.zip");
        var entered = Signal();
        var release = Signal();
        var writes = 0;
        var persistence = new MemoryPersistence
        {
            BeforeSave = async (_, token) =>
            {
                if (Interlocked.Increment(ref writes) != 1) return;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        using var session = new ArchiveSession(new DelegateConverter(PublishAsync), persistence,
            commitReceipts: new MemoryReceipts());
        await session.RestoreAsync();
        session.OutputDirectory = directory.Path;
        session.AddInputs([source], new(ArchiveFormat.Zip));
        var task = Assert.Single(session.Tasks);
        var firstSave = session.FlushAsync();
        await entered.Task.WaitAsync(Deadline);
        task.SetState(ArchiveTaskState.Failed, "ArchiveErrorInsufficientSpace");
        var queuedSave = session.FlushAsync();
        task.SetState(ArchiveTaskState.Cancelled);
        release.TrySetResult();

        await Task.WhenAll(firstSave, queuedSave).WaitAsync(Deadline);

        Assert.Equal(ArchiveTaskState.Cancelled, Assert.Single(persistence.Current.Tasks).State);
        Assert.Equal(ArchiveTaskState.Waiting, Assert.Single(persistence.Writes.First().Tasks).State);
        Assert.DoesNotContain(persistence.Writes, snapshot => snapshot.Tasks.Any(saved => saved.State == ArchiveTaskState.Failed));
    }

    [Fact]
    public async Task PublishedReceiptRestoresSuccessAndHistoryWithoutStartingConverter()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateSourceAsync(directory.Path, "source.zip");
        var published = Path.Combine(directory.Path, "published.7z");
        await File.WriteAllTextAsync(published, "Synthetic already published archive.");
        var checkpoint = new ArchiveTaskCheckpoint
        {
            SourcePath = source, OutputDirectory = directory.Path, TargetFormat = ArchiveFormat.SevenZip,
            StrictVerification = true, EncryptOutput = true, State = ArchiveTaskState.Running
        };
        var persistence = new MemoryPersistence(new() { OutputDirectory = directory.Path, Tasks = [checkpoint] });
        var receipts = new MemoryReceipts();
        await receipts.PrepareAsync(checkpoint, "unused-staging-path", published);
        var history = new List<ConversionHistoryItem>();
        var converter = new DelegateConverter(_ => throw new InvalidOperationException("Recovered outputs must not be converted again."));
        using var session = new ArchiveSession(converter, persistence, (item, _) =>
        {
            history.Add(item);
            return Task.CompletedTask;
        }, receipts);

        await session.RestoreAsync();
        await session.FlushAsync();

        var task = Assert.Single(session.Tasks);
        Assert.Equal(checkpoint.Id, task.Id);
        Assert.Equal(ArchiveTaskState.Succeeded, task.State);
        Assert.Equal(published, task.OutputPath);
        Assert.True(task.HistoryRecorded);
        Assert.True(task.HasEncryption);
        Assert.Null(task.Options.OutputPassword);
        Assert.Equal(checkpoint.Id, Assert.Single(history).TaskId);
        Assert.Equal(0, converter.Calls);
        Assert.False(session.IsRunning);
        Assert.Equal(ArchiveTaskState.Succeeded, Assert.Single(persistence.Current.Tasks).State);
    }

    private static Task<string?> NoPassword(ArchiveTask _, ArchivePasswordRequest __, CancellationToken ___) =>
        Task.FromResult<string?>(null);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<string> CreateSourceAsync(string root, string name)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path, "Synthetic source archive bytes must remain unchanged.");
        return path;
    }

    private static async Task<string> PublishAsync(ConversionCall call)
    {
        call.Token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(call.OutputDirectory);
        var output = Path.Combine(call.OutputDirectory, Guid.NewGuid().ToString("N") + ArchiveFormats.Extension(call.Options.TargetFormat));
        var staged = output + ".staged";
        await File.WriteAllTextAsync(staged, "Synthetic validated archive output.", call.Token);
        if (call.PrepareCommit is not null) await call.PrepareCommit(staged, output, call.Token);
        call.Token.ThrowIfCancellationRequested();
        File.Move(staged, output);
        return output;
    }

    private static Task StartWithSynchronousProgress(ArchiveSession session)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronousProgressContext());
            return session.StartAsync(NoPassword);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    // Progress<T> captures this context. Reports finish before assertions, so the
    // stale-callback check does not depend on thread-pool timing or a sleep.
    private sealed class SynchronousProgressContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            try { SetSynchronizationContext(this); callback(state); }
            finally { SetSynchronizationContext(previous); }
        }
    }

    private sealed record ConversionCall(string SourcePath, string OutputDirectory, ArchiveConversionOptions Options,
        Func<ArchivePasswordRequest, CancellationToken, Task<string?>> RequestPassword,
        IProgress<ArchiveProgress>? Progress, CancellationToken Token,
        Func<string, string, CancellationToken, Task>? PrepareCommit);

    private sealed class DelegateConverter(Func<ConversionCall, Task<string>> convert) : IArchiveConverter
    {
        private int _calls;
        public bool IsAvailable => true;
        public int Calls => Volatile.Read(ref _calls);
        public Task<string> ConvertAsync(string sourcePath, string outputDirectory, ArchiveConversionOptions options,
            Func<ArchivePasswordRequest, CancellationToken, Task<string?>> requestPassword,
            IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default,
            Func<string, string, CancellationToken, Task>? prepareCommit = null)
        {
            Interlocked.Increment(ref _calls);
            return convert(new(sourcePath, outputDirectory, options, requestPassword, progress, cancellationToken, prepareCommit));
        }
    }

    private sealed class MemoryPersistence(ArchiveTaskSnapshot? initial = null) : IArchiveTaskPersistence
    {
        private readonly object _gate = new();
        private ArchiveTaskSnapshot _current = initial ?? new();
        public ArchiveTaskSnapshot Current { get { lock (_gate) return _current; } }
        public ConcurrentQueue<ArchiveTaskSnapshot> Writes { get; } = new();
        public Exception? LoadFailure { get; init; }
        public Func<ArchiveTaskSnapshot, Exception?>? SaveFailure { get; set; }
        public Func<ArchiveTaskSnapshot, CancellationToken, Task>? BeforeSave { get; init; }

        public Task<ArchiveTaskSnapshot> LoadAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return LoadFailure is null ? Task.FromResult(Current) : Task.FromException<ArchiveTaskSnapshot>(LoadFailure);
        }

        public async Task SaveAsync(ArchiveTaskSnapshot snapshot, CancellationToken token = default)
        {
            snapshot = snapshot with { Tasks = snapshot.Tasks.ToArray() };
            Writes.Enqueue(snapshot);
            if (BeforeSave is not null) await BeforeSave(snapshot, token);
            if (SaveFailure?.Invoke(snapshot) is { } failure) throw failure;
            token.ThrowIfCancellationRequested();
            lock (_gate) _current = snapshot;
        }
    }

    private sealed class MemoryReceipts : IArchiveCommitReceiptStore
    {
        public ConcurrentDictionary<Guid, ArchiveTaskCheckpoint> Prepared { get; } = new();
        public ConcurrentQueue<Guid> Forgotten { get; } = new();
        private readonly ConcurrentDictionary<Guid, string> _outputs = new();

        public Task PrepareAsync(ArchiveTaskCheckpoint task, string stagedPath, string finalPath, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Prepared[task.Id] = task;
            _outputs[task.Id] = finalPath;
            return Task.CompletedTask;
        }

        public Task<ArchiveTaskCheckpoint?> TryRecoverAsync(ArchiveTaskCheckpoint task, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            ArchiveTaskCheckpoint? recovered = Prepared.ContainsKey(task.Id) && _outputs.TryGetValue(task.Id, out var output) && File.Exists(output)
                ? task with { State = ArchiveTaskState.Succeeded, OutputPath = output, CompletedAtUtc = DateTimeOffset.UtcNow,
                    ErrorKey = null, HistoryRecorded = false } : null;
            return Task.FromResult(recovered);
        }

        public Task ForgetAsync(Guid taskId, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Forgotten.Enqueue(taskId);
            Prepared.TryRemove(taskId, out _);
            _outputs.TryRemove(taskId, out _);
            return Task.CompletedTask;
        }
    }
}
