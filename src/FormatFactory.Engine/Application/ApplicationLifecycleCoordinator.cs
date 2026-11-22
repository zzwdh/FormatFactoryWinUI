namespace FormatFactoryWinUI.ApplicationServices;

public enum ApplicationStartupPhase
{
    Created, PreparingWindow, LoadingSettings, DiscoveringEngines, RestoringQueue,
    RestoringArchives, Interactive, StartingBackgroundServices, Completed, Interrupted, Failed
}

public enum ApplicationShutdownPhase
{
    None, StoppingArchives, StoppingImports, ClosingDialogs, StoppingAutomation,
    SavingPreferences, SavingQueue, WaitingForStartup, ConfirmingUnsavedExit,
    Resuming, PersistingPlacement, InstallingUpdate, Closing, Completed, Failed
}

public sealed class ApplicationStartupOperations
{
    public required Action PrepareWindow { get; init; }
    public required Func<Task> LoadSettings { get; init; }
    public required Func<Task> DiscoverDocuments { get; init; }
    public required Func<Task> DiscoverOptical { get; init; }
    public required Func<Task> LoadHardware { get; init; }
    public required Action StartObservers { get; init; }
    public required Func<Task> DiscoverMedia { get; init; }
    public required Func<Task> RestoreQueue { get; init; }
    public required Func<Task> RestoreArchives { get; init; }
    public required Action PublishInteractive { get; init; }
    public required Func<Task> StartAutomation { get; init; }
    public required Func<Task> ReconcileAutomation { get; init; }
    public required Action ConfigureWatchFolders { get; init; }
    public required Action ScheduleSupplementaryServices { get; init; }
    public required Action Complete { get; init; }
    public required Func<bool> IsQueueStopping { get; init; }
}

public sealed class ApplicationShutdownOperations
{
    public required Func<Task> StopArchives { get; init; }
    public required Func<Task> StopImports { get; init; }
    public required Func<Task> CloseDialogs { get; init; }
    public required Action CancelHistoryIndexing { get; init; }
    public required Func<Task> StopAutomation { get; init; }
    public required Func<Task> SaveUpdatePreferences { get; init; }
    public required Func<bool, Task<QueueShutdownResult>> ShutdownQueue { get; init; }
    public required Func<Exception, Task<bool>> ConfirmUnsavedExit { get; init; }
    public required Action ResumeQueue { get; init; }
    public required Func<Task> RestartAutomation { get; init; }
    public required Action ConfigureWatchFolders { get; init; }
    public required Func<Task> PersistWindowPlacement { get; init; }
    public required Func<Task> InstallUpdate { get; init; }
    public required Action CloseWindow { get; init; }
    public Action<Exception>? ObserveStartupFailure { get; init; }
    public Action<Exception>? ObservePlacementFailure { get; init; }
}

/// <summary>
/// Orders application startup and shutdown on the host context. Interactive readiness
/// is independent of background-service completion, and failed saves can resume the app.
/// </summary>
public sealed class ApplicationLifecycleCoordinator
{
    private int _closed;
    private Task<bool>? _shutdownTask;
    private ApplicationStartupOperations? _startup;
    private bool _recoveryPending;
    private bool _backgroundPending;
    private bool _supplementaryPending;
    internal TaskCompletionSource<bool> ActivationCompletion { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ActivationReady => ActivationCompletion.Task;
    public Task? StartupTask { get; private set; }
    public ApplicationStartupPhase StartupPhase { get; private set; }
    public ApplicationShutdownPhase ShutdownPhase { get; private set; }
    public bool IsInitializing { get; internal set; } = true;
    public bool IsClosing { get; internal set; }
    public bool CanClose { get; internal set; }
    public bool IsClosed { get => Volatile.Read(ref _closed) != 0; internal set => Volatile.Write(ref _closed, value ? 1 : 0); }
    public bool IsInteractive => !IsInitializing && !IsClosing && !IsClosed;

    public Task StartAsync(ApplicationStartupOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (IsClosed) return Task.CompletedTask;
        if (StartupTask is not null) return StartupTask;
        _startup = operations;
        return StartupTask = StartCoreAsync(operations);
    }

    private async Task StartCoreAsync(ApplicationStartupOperations operations)
    {
        try
        {
            StartupPhase = ApplicationStartupPhase.PreparingWindow;
            operations.PrepareWindow();
            if (IsClosed) return;
            StartupPhase = ApplicationStartupPhase.LoadingSettings;
            await operations.LoadSettings();
            if (IsClosed) return;

            StartupPhase = ApplicationStartupPhase.DiscoveringEngines;
            var documents = operations.DiscoverDocuments();
            var optical = operations.DiscoverOptical();
            var hardware = operations.LoadHardware();
            operations.StartObservers();
            await hardware;
            if (IsClosed) return;
            await operations.DiscoverMedia();
            await Task.WhenAll(documents, optical);
            if (IsClosed) return;

            await RecoverAsync(operations);
            if (IsClosed) return;
            if (!IsClosing) await StartBackgroundAsync(operations);
        }
        catch (OperationCanceledException) when (operations.IsQueueStopping())
        {
            IsInitializing = false;
            StartupPhase = ApplicationStartupPhase.Interrupted;
            ActivationCompletion.TrySetResult(true);
            throw;
        }
        catch (Exception exception)
        {
            StartupPhase = ApplicationStartupPhase.Failed;
            ActivationCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task RecoverAsync(ApplicationStartupOperations operations)
    {
        _recoveryPending = true;
        StartupPhase = ApplicationStartupPhase.RestoringQueue;
        await operations.RestoreQueue();
        if (IsClosed) return;
        StartupPhase = ApplicationStartupPhase.RestoringArchives;
        await operations.RestoreArchives();
        if (IsClosed) return;
        IsInitializing = false;
        StartupPhase = ApplicationStartupPhase.Interactive;
        operations.PublishInteractive();
        ActivationCompletion.TrySetResult(true);
        _recoveryPending = false;
        _backgroundPending = true;
    }

    private async Task ResumeRecoveryAsync(ApplicationStartupOperations startup, Func<Task> restartAutomation)
    {
        try
        {
            // Keep admission at the host closed until the original durable rows have
            // finished probing. RestoreAsync resumes the same published models.
            IsInitializing = true;
            ActivationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await RecoverAsync(startup);
            if (IsClosed) return;
            IsClosing = false;
            await restartAutomation();
            await StartBackgroundAsync(startup);
        }
        catch (Exception exception)
        {
            StartupPhase = ApplicationStartupPhase.Failed;
            ActivationCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task StartBackgroundAsync(ApplicationStartupOperations operations)
    {
        _backgroundPending = false;
        _supplementaryPending = true;
        StartupPhase = ApplicationStartupPhase.StartingBackgroundServices;
        await operations.StartAutomation();
        if (IsClosed) return;
        await operations.ReconcileAutomation();
        if (IsClosed) return;
        // A close may have begun while automation initialized. Shutdown awaits startup
        // and stops it again before permitting exit, so late work cannot outlive the app.
        if (!IsClosing)
        {
            operations.ConfigureWatchFolders();
            operations.ScheduleSupplementaryServices();
            _supplementaryPending = false;
        }
        operations.Complete();
        StartupPhase = ApplicationStartupPhase.Completed;
    }

    public Task<bool> ShutdownAsync(ApplicationShutdownOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (IsClosed) return Task.FromResult(true);
        if (_shutdownTask is { IsCompleted: false }) return _shutdownTask;
        return _shutdownTask = ShutdownCoreAsync(operations);
    }

    private async Task<bool> ShutdownCoreAsync(ApplicationShutdownOperations operations)
    {
        IsClosing = true;
        var startupWasRunning = StartupTask is { IsCompleted: false };
        try
        {
            ShutdownPhase = ApplicationShutdownPhase.StoppingArchives;
            await operations.StopArchives();
            ShutdownPhase = ApplicationShutdownPhase.StoppingImports;
            await operations.StopImports();
            ShutdownPhase = ApplicationShutdownPhase.ClosingDialogs;
            await operations.CloseDialogs();
            operations.CancelHistoryIndexing();
            ShutdownPhase = ApplicationShutdownPhase.StoppingAutomation;
            await operations.StopAutomation();
            ShutdownPhase = ApplicationShutdownPhase.SavingPreferences;
            await operations.SaveUpdatePreferences();
            ShutdownPhase = ApplicationShutdownPhase.SavingQueue;
            var saved = await operations.ShutdownQueue(true);
            ShutdownPhase = ApplicationShutdownPhase.WaitingForStartup;
            if (StartupTask is not null)
                try { await StartupTask; }
                catch (Exception exception) { operations.ObserveStartupFailure?.Invoke(exception); }
            if (startupWasRunning) await operations.StopAutomation();

            if (!saved.CanClose)
            {
                ShutdownPhase = ApplicationShutdownPhase.ConfirmingUnsavedExit;
                if (!await operations.ConfirmUnsavedExit(saved.SaveError!))
                {
                    ShutdownPhase = ApplicationShutdownPhase.Resuming;
                    operations.ResumeQueue();
                    if (_recoveryPending && _startup is not null)
                        await (StartupTask = ResumeRecoveryAsync(_startup, operations.RestartAutomation));
                    else
                    {
                        IsClosing = false;
                        // Clear the host's stop flags even if initial automation was
                        // deferred: initializing the service does not clear them.
                        await operations.RestartAutomation();
                        if (_backgroundPending && _startup is not null) await StartBackgroundAsync(_startup);
                        else
                        {
                            operations.ConfigureWatchFolders();
                            if (_supplementaryPending && _startup is not null)
                            {
                                _startup.ScheduleSupplementaryServices();
                                _supplementaryPending = false;
                            }
                        }
                    }
                    ShutdownPhase = ApplicationShutdownPhase.None;
                    return false;
                }
                await operations.ShutdownQueue(false);
            }
            ShutdownPhase = ApplicationShutdownPhase.PersistingPlacement;
            try { await operations.PersistWindowPlacement(); }
            catch (Exception exception) { operations.ObservePlacementFailure?.Invoke(exception); }
            if (saved.CanClose)
            {
                ShutdownPhase = ApplicationShutdownPhase.InstallingUpdate;
                await operations.InstallUpdate();
            }
            ShutdownPhase = ApplicationShutdownPhase.Closing;
            CanClose = true;
            operations.CloseWindow();
            ShutdownPhase = ApplicationShutdownPhase.Completed;
            return true;
        }
        catch
        {
            CanClose = false;
            ShutdownPhase = ApplicationShutdownPhase.Failed;
            throw;
        }
        finally { IsClosing = false; }
    }

    public void MarkClosed()
    {
        IsClosed = true;
        ShutdownPhase = ApplicationShutdownPhase.Completed;
        ActivationCompletion.TrySetCanceled();
    }
}
