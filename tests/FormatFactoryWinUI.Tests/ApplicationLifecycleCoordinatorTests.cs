using System.Collections.Concurrent;
using FormatFactoryWinUI.ApplicationServices;

namespace FormatFactoryWinUI.Tests;

public sealed class ApplicationLifecycleCoordinatorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DiscoveryOverlapsButRecoveryAndActivationWaitForTheirActualDependencies()
    {
        var documents = Signal();
        var optical = Signal();
        var hardware = Signal();
        var media = Signal();
        var queue = Signal();
        var archives = Signal();
        var automation = Signal();
        var fixture = new LifecycleFixture
        {
            DiscoverDocuments = () => documents.Task,
            DiscoverOptical = () => optical.Task,
            LoadHardware = () => hardware.Task,
            DiscoverMedia = () => media.Task,
            RestoreQueue = () => queue.Task,
            RestoreArchives = () => archives.Task,
            StartAutomation = () => automation.Task
        };

        var startup = fixture.Coordinator.StartAsync(fixture.Startup());

        Assert.Contains("discover-documents", fixture.Events);
        Assert.Contains("discover-optical", fixture.Events);
        Assert.Contains("load-hardware", fixture.Events);
        Assert.Contains("start-observers", fixture.Events);
        Assert.DoesNotContain("discover-media", fixture.Events);
        Assert.DoesNotContain("restore-queue", fixture.Events);
        hardware.SetResult();
        await fixture.ReachedAsync("discover-media");
        media.SetResult();
        documents.SetResult();
        Assert.DoesNotContain("restore-queue", fixture.Events);
        optical.SetResult();
        await fixture.ReachedAsync("restore-queue");
        Assert.False(fixture.Coordinator.ActivationReady.IsCompleted);
        Assert.DoesNotContain("restore-archives", fixture.Events);
        queue.SetResult();
        await fixture.ReachedAsync("restore-archives");
        Assert.False(fixture.Coordinator.ActivationReady.IsCompleted);
        Assert.DoesNotContain("publish-interactive", fixture.Events);
        archives.SetResult();

        await fixture.Coordinator.ActivationReady.WaitAsync(Deadline);
        await fixture.ReachedAsync("start-automation");

        Assert.True(fixture.Coordinator.IsInteractive);
        Assert.False(startup.IsCompleted);
        Assert.DoesNotContain("reconcile-automation", fixture.Events);
        Assert.DoesNotContain("schedule-supplementary", fixture.Events);
        automation.SetResult();
        await startup.WaitAsync(Deadline);
        Before(fixture, "restore-archives", "publish-interactive");
        Before(fixture, "publish-interactive", "start-automation");
        Before(fixture, "reconcile-automation", "startup-watch-folders");
    }

    [Fact]
    public async Task DecliningAnUnsavedExitResumesQueueAndAutomationWithoutInstallingOrClosing()
    {
        var saveError = new IOException("Synthetic unavailable queue store.");
        var fixture = new LifecycleFixture
        {
            ShutdownQueue = _ => Task.FromResult(new QueueShutdownResult(false, saveError)),
            ConfirmUnsavedExit = error =>
            {
                Assert.Same(saveError, error);
                return Task.FromResult(false);
            }
        };
        await fixture.Coordinator.StartAsync(fixture.Startup());
        fixture.Events.Clear();

        var closed = await fixture.Coordinator.ShutdownAsync(fixture.Shutdown()).WaitAsync(Deadline);

        Assert.False(closed);
        Assert.True(fixture.Coordinator.IsInteractive);
        Assert.False(fixture.Coordinator.CanClose);
        Assert.Equal(new[] { true }, fixture.QueueSaveRequests.ToArray());
        Before(fixture, "confirm-unsaved", "resume-queue");
        Before(fixture, "resume-queue", "restart-automation");
        Before(fixture, "restart-automation", "shutdown-watch-folders");
        Assert.DoesNotContain("persist-placement", fixture.Events);
        Assert.DoesNotContain("install-update", fixture.Events);
        Assert.DoesNotContain("close-window", fixture.Events);
    }

    [Fact]
    public async Task AcceptingAnUnsavedExitSkipsTheUpdateEvenIfTheForcedShutdownSucceeds()
    {
        var fixture = new LifecycleFixture
        {
            ShutdownQueue = save => Task.FromResult(save
                ? new QueueShutdownResult(false, new IOException("Synthetic save failure."))
                : new QueueShutdownResult(true)),
            ConfirmUnsavedExit = _ => Task.FromResult(true)
        };
        await fixture.Coordinator.StartAsync(fixture.Startup());
        fixture.Events.Clear();

        Assert.True(await fixture.Coordinator.ShutdownAsync(fixture.Shutdown()).WaitAsync(Deadline));

        Assert.Equal(new[] { true, false }, fixture.QueueSaveRequests.ToArray());
        Before(fixture, "confirm-unsaved", "save-queue:false");
        Before(fixture, "save-queue:false", "persist-placement");
        Assert.DoesNotContain("install-update", fixture.Events);
        Assert.DoesNotContain("resume-queue", fixture.Events);
        Assert.Equal("close-window", fixture.Events.Last());
        Assert.True(fixture.Coordinator.IsClosed);
    }

    [Fact]
    public async Task SuccessfulShutdownWaitsForTheSaveAndInstallsBeforeClosingLast()
    {
        var queueSaved = new TaskCompletionSource<QueueShutdownResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new LifecycleFixture { ShutdownQueue = _ => queueSaved.Task };
        await fixture.Coordinator.StartAsync(fixture.Startup());
        fixture.Events.Clear();

        var shutdown = fixture.Coordinator.ShutdownAsync(fixture.Shutdown());
        await fixture.ReachedAsync("save-queue:true");

        Assert.True(fixture.Coordinator.IsClosing);
        Assert.False(shutdown.IsCompleted);
        Assert.DoesNotContain("persist-placement", fixture.Events);
        Assert.DoesNotContain("install-update", fixture.Events);
        Assert.DoesNotContain("close-window", fixture.Events);
        queueSaved.SetResult(new(true));
        Assert.True(await shutdown.WaitAsync(Deadline));
        Before(fixture, "stop-archives", "stop-imports");
        Before(fixture, "stop-imports", "close-dialogs");
        Before(fixture, "stop-automation", "save-queue:true");
        Before(fixture, "save-queue:true", "persist-placement");
        Before(fixture, "persist-placement", "install-update");
        Before(fixture, "install-update", "close-window");
        Assert.Equal("close-window", fixture.Events.Last());
    }

    [Fact]
    public async Task ClosingDuringRecoveryDefersBackgroundStartupUntilTheUserDeclinesExit()
    {
        var archives = Signal();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new LifecycleFixture
        {
            RestoreArchives = () => archives.Task,
            ShutdownQueue = _ => Task.FromResult(new QueueShutdownResult(false, new IOException("Synthetic save failure."))),
            ConfirmUnsavedExit = _ => confirmation.Task
        };
        var startup = fixture.Coordinator.StartAsync(fixture.Startup());
        await fixture.ReachedAsync("restore-archives");
        var shutdown = fixture.Coordinator.ShutdownAsync(fixture.Shutdown());
        await fixture.ReachedAsync("save-queue:true");
        archives.SetResult();
        await startup.WaitAsync(Deadline);
        await fixture.ReachedAsync("confirm-unsaved");

        Assert.True(fixture.Coordinator.ActivationReady.IsCompletedSuccessfully);
        Assert.DoesNotContain("start-automation", fixture.Events);
        Assert.DoesNotContain("schedule-supplementary", fixture.Events);
        Assert.True(fixture.AutomationStopping);
        confirmation.SetResult(false);

        Assert.False(await shutdown.WaitAsync(Deadline));
        Assert.Equal(1, fixture.Events.Count(item => item == "start-automation"));
        Assert.Equal(1, fixture.Events.Count(item => item == "reconcile-automation"));
        Assert.Equal(1, fixture.Events.Count(item => item == "schedule-supplementary"));
        Assert.Equal(new[] { false }, fixture.AutomationStoppingAtStart.ToArray());
        Assert.False(fixture.AutomationStopping);
        Before(fixture, "resume-queue", "restart-automation");
        Before(fixture, "restart-automation", "start-automation");
        Assert.DoesNotContain("install-update", fixture.Events);
        Assert.True(fixture.Coordinator.IsInteractive);
    }

    [Fact]
    public async Task ClosingDuringAutomationStartupStopsAgainAfterItsLateCompletion()
    {
        var automation = Signal();
        var fixture = new LifecycleFixture { StartAutomation = () => automation.Task };
        var startup = fixture.Coordinator.StartAsync(fixture.Startup());
        await fixture.ReachedAsync("start-automation");
        await fixture.Coordinator.ActivationReady.WaitAsync(Deadline);
        var shutdown = fixture.Coordinator.ShutdownAsync(fixture.Shutdown());
        await fixture.ReachedAsync("save-queue:true");

        Assert.Equal(1, fixture.Events.Count(item => item == "stop-automation"));
        Assert.False(shutdown.IsCompleted);
        Assert.DoesNotContain("install-update", fixture.Events);
        automation.SetResult();
        await startup.WaitAsync(Deadline);
        Assert.True(await shutdown.WaitAsync(Deadline));

        var events = fixture.Events.ToArray();
        var stops = events.Select((name, index) => (name, index))
            .Where(item => item.name == "stop-automation").Select(item => item.index).ToArray();
        Assert.Equal(2, stops.Length);
        var reconciled = Array.IndexOf(events, "reconcile-automation");
        Assert.True(stops[0] < reconciled && reconciled < stops[1]);
        Assert.True(stops[1] < Array.IndexOf(events, "close-window"));
        Assert.DoesNotContain("startup-watch-folders", fixture.Events);
        Assert.DoesNotContain("schedule-supplementary", fixture.Events);
        Assert.Equal("close-window", events.Last());
    }

    [Fact]
    public async Task DecliningExitAfterLateAutomationCompletionRestartsItAndSchedulesDeferredServicesOnce()
    {
        var automation = Signal();
        var fixture = new LifecycleFixture
        {
            StartAutomation = () => automation.Task,
            ShutdownQueue = _ => Task.FromResult(new QueueShutdownResult(false, new IOException("Synthetic save failure.")))
        };
        var startup = fixture.Coordinator.StartAsync(fixture.Startup());
        await fixture.ReachedAsync("start-automation");
        var shutdown = fixture.Coordinator.ShutdownAsync(fixture.Shutdown());
        await fixture.ReachedAsync("save-queue:true");
        automation.SetResult();
        await startup.WaitAsync(Deadline);

        Assert.False(await shutdown.WaitAsync(Deadline));

        Assert.Equal(2, fixture.Events.Count(item => item == "stop-automation"));
        Assert.Equal(1, fixture.Events.Count(item => item == "start-automation"));
        Assert.Equal(1, fixture.Events.Count(item => item == "restart-automation"));
        Assert.Equal(1, fixture.Events.Count(item => item == "schedule-supplementary"));
        Assert.DoesNotContain("startup-watch-folders", fixture.Events);
        Before(fixture, "resume-queue", "restart-automation");
        Before(fixture, "restart-automation", "shutdown-watch-folders");
        Before(fixture, "shutdown-watch-folders", "schedule-supplementary");
        Assert.DoesNotContain("install-update", fixture.Events);
        Assert.DoesNotContain("close-window", fixture.Events);
        Assert.True(fixture.Coordinator.IsInteractive);
    }

    [Fact]
    public async Task StartupFailureFaultsActivationAndIsObservedWithoutPreventingShutdown()
    {
        var failure = new IOException("Synthetic media discovery failure.");
        var fixture = new LifecycleFixture { DiscoverMedia = () => Task.FromException(failure) };

        var startup = fixture.Coordinator.StartAsync(fixture.Startup());

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => startup.WaitAsync(Deadline)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.ActivationReady.WaitAsync(Deadline)));
        Assert.DoesNotContain("publish-interactive", fixture.Events);
        Assert.DoesNotContain("start-automation", fixture.Events);
        Assert.True(await fixture.Coordinator.ShutdownAsync(fixture.Shutdown()).WaitAsync(Deadline));
        Assert.Same(failure, Assert.Single(fixture.ObservedStartupFailures));
        Assert.True(fixture.Coordinator.IsClosed);
    }

    [Fact]
    public async Task QueueStoppingCancellationReleasesActivationAndCanFinishShutdown()
    {
        var fixture = new LifecycleFixture
        {
            QueueStopping = true,
            RestoreQueue = () => Task.FromCanceled(new CancellationToken(true))
        };

        var startup = fixture.Coordinator.StartAsync(fixture.Startup());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(Deadline));
        await fixture.Coordinator.ActivationReady.WaitAsync(Deadline);
        Assert.True(fixture.Coordinator.ActivationReady.IsCompletedSuccessfully);
        Assert.False(fixture.Coordinator.IsInitializing);
        Assert.DoesNotContain("restore-archives", fixture.Events);
        Assert.DoesNotContain("publish-interactive", fixture.Events);
        Assert.DoesNotContain("start-automation", fixture.Events);
        Assert.True(await fixture.Coordinator.ShutdownAsync(fixture.Shutdown()).WaitAsync(Deadline));
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(fixture.ObservedStartupFailures));
    }

    [Fact]
    public async Task DecliningExitAfterCancelledQueueRecoveryWaitsForRecoveryBeforeStartingBackgroundServices()
    {
        var resumedQueue = Signal();
        var resumedArchives = Signal();
        var restoreCalls = 0;
        var fixture = new LifecycleFixture
        {
            QueueStopping = true,
            RestoreQueue = () => ++restoreCalls == 1
                ? Task.FromCanceled(new CancellationToken(true)) : resumedQueue.Task,
            RestoreArchives = () => resumedArchives.Task,
            ShutdownQueue = _ => Task.FromResult(new QueueShutdownResult(false, new IOException("Synthetic save failure.")))
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.StartAsync(fixture.Startup()));
        var interruptedReadiness = fixture.Coordinator.ActivationReady;

        var shutdown = fixture.Coordinator.ShutdownAsync(fixture.Shutdown());
        Assert.Equal(2, restoreCalls);
        Assert.True(fixture.Coordinator.IsInitializing);
        Assert.True(fixture.Coordinator.IsClosing);
        Assert.False(fixture.Coordinator.IsInteractive);
        Assert.NotSame(interruptedReadiness, fixture.Coordinator.ActivationReady);
        Assert.False(fixture.Coordinator.ActivationReady.IsCompleted);
        Assert.DoesNotContain("restart-automation", fixture.Events);
        Assert.DoesNotContain("start-automation", fixture.Events);
        resumedQueue.SetResult();
        await fixture.ReachedAsync("restore-archives");
        Assert.False(fixture.Coordinator.ActivationReady.IsCompleted);
        resumedArchives.SetResult();

        Assert.False(await shutdown.WaitAsync(Deadline));
        await fixture.Coordinator.ActivationReady.WaitAsync(Deadline);
        Assert.True(fixture.Coordinator.IsInteractive);
        Assert.True(fixture.Coordinator.StartupTask!.IsCompletedSuccessfully);
        Assert.Single(fixture.Events, item => item == "publish-interactive");
        Assert.Equal(1, fixture.Events.Count(item => item == "start-automation"));
        Assert.Equal(1, fixture.Events.Count(item => item == "schedule-supplementary"));
        Assert.Equal(new[] { false }, fixture.AutomationStoppingAtStart.ToArray());
        Before(fixture, "restore-archives", "publish-interactive");
        Before(fixture, "publish-interactive", "restart-automation");
        Before(fixture, "restart-automation", "start-automation");
        Assert.DoesNotContain("close-window", fixture.Events);
    }

    [Fact]
    public async Task OverlappingShutdownRequestsShareOneOperationAndDoNotRepeatSideEffects()
    {
        var archivesStopped = Signal();
        var fixture = new LifecycleFixture { StopArchives = () => archivesStopped.Task };
        await fixture.Coordinator.StartAsync(fixture.Startup());
        fixture.Events.Clear();
        var operations = fixture.Shutdown();

        var first = fixture.Coordinator.ShutdownAsync(operations);
        var second = fixture.Coordinator.ShutdownAsync(operations);

        Assert.Same(first, second);
        Assert.Equal(1, fixture.Events.Count(item => item == "stop-archives"));
        archivesStopped.SetResult();
        Assert.True(await first.WaitAsync(Deadline));
        Assert.True(await second.WaitAsync(Deadline));
        Assert.True(await fixture.Coordinator.ShutdownAsync(operations).WaitAsync(Deadline));
        Assert.Equal(1, fixture.Events.Count(item => item == "stop-archives"));
        Assert.Equal(1, fixture.Events.Count(item => item == "stop-automation"));
        Assert.Single(fixture.QueueSaveRequests);
        Assert.Equal(1, fixture.Events.Count(item => item == "install-update"));
        Assert.Equal(1, fixture.Events.Count(item => item == "close-window"));
    }

    [Fact]
    public async Task StartingAnAlreadyClosedCoordinatorDoesNotPrepareNativeResources()
    {
        var fixture = new LifecycleFixture();
        fixture.Coordinator.MarkClosed();

        await fixture.Coordinator.StartAsync(fixture.Startup()).WaitAsync(Deadline);

        Assert.Empty(fixture.Events);
        Assert.True(fixture.Coordinator.ActivationReady.IsCanceled);
        Assert.True(fixture.Coordinator.IsClosed);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Before(LifecycleFixture fixture, string earlier, string later)
    {
        var events = fixture.Events.ToArray();
        Assert.Contains(earlier, events);
        Assert.Contains(later, events);
        Assert.True(Array.IndexOf(events, earlier) < Array.IndexOf(events, later),
            $"Expected {earlier} before {later}: {string.Join(", ", events)}");
    }

    private sealed class LifecycleFixture
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _reached = new(StringComparer.Ordinal);
        public ApplicationLifecycleCoordinator Coordinator { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<bool> QueueSaveRequests { get; } = new();
        public ConcurrentQueue<bool> AutomationStoppingAtStart { get; } = new();
        public ConcurrentQueue<Exception> ObservedStartupFailures { get; } = new();
        public bool AutomationStopping { get; private set; }
        public Func<Task> DiscoverDocuments { get; init; } = Completed;
        public Func<Task> DiscoverOptical { get; init; } = Completed;
        public Func<Task> LoadHardware { get; init; } = Completed;
        public Func<Task> DiscoverMedia { get; init; } = Completed;
        public Func<Task> RestoreQueue { get; init; } = Completed;
        public Func<Task> RestoreArchives { get; init; } = Completed;
        public Func<Task> StartAutomation { get; init; } = Completed;
        public Func<Task> StopArchives { get; init; } = Completed;
        public Func<bool, Task<QueueShutdownResult>> ShutdownQueue { get; init; } = _ => Task.FromResult(new QueueShutdownResult(true));
        public Func<Exception, Task<bool>> ConfirmUnsavedExit { get; init; } = _ => Task.FromResult(false);
        public bool QueueStopping { get; init; }

        public ApplicationStartupOperations Startup() => new()
        {
            PrepareWindow = () => Record("prepare-window"),
            LoadSettings = () => Run("load-settings", Completed),
            DiscoverDocuments = () => Run("discover-documents", DiscoverDocuments),
            DiscoverOptical = () => Run("discover-optical", DiscoverOptical),
            LoadHardware = () => Run("load-hardware", LoadHardware),
            StartObservers = () => Record("start-observers"),
            DiscoverMedia = () => Run("discover-media", DiscoverMedia),
            RestoreQueue = () => Run("restore-queue", RestoreQueue),
            RestoreArchives = () => Run("restore-archives", RestoreArchives),
            PublishInteractive = () => Record("publish-interactive"),
            StartAutomation = () =>
            {
                AutomationStoppingAtStart.Enqueue(AutomationStopping);
                return Run("start-automation", StartAutomation);
            },
            ReconcileAutomation = () => Run("reconcile-automation", Completed),
            ConfigureWatchFolders = () => Record("startup-watch-folders"),
            ScheduleSupplementaryServices = () => Record("schedule-supplementary"),
            Complete = () => Record("startup-complete"),
            IsQueueStopping = () => QueueStopping
        };

        public ApplicationShutdownOperations Shutdown() => new()
        {
            StopArchives = () => Run("stop-archives", StopArchives),
            StopImports = () => Run("stop-imports", Completed),
            CloseDialogs = () => Run("close-dialogs", Completed),
            CancelHistoryIndexing = () => Record("cancel-history-indexing"),
            StopAutomation = () =>
            {
                AutomationStopping = true;
                return Run("stop-automation", Completed);
            },
            SaveUpdatePreferences = () => Run("save-update-preferences", Completed),
            ShutdownQueue = save =>
            {
                Record(save ? "save-queue:true" : "save-queue:false");
                QueueSaveRequests.Enqueue(save);
                return ShutdownQueue(save);
            },
            ConfirmUnsavedExit = exception =>
            {
                Record("confirm-unsaved");
                return ConfirmUnsavedExit(exception);
            },
            ResumeQueue = () => Record("resume-queue"),
            RestartAutomation = () =>
            {
                AutomationStopping = false;
                return Run("restart-automation", Completed);
            },
            ConfigureWatchFolders = () => Record("shutdown-watch-folders"),
            PersistWindowPlacement = () => Run("persist-placement", Completed),
            InstallUpdate = () => Run("install-update", Completed),
            CloseWindow = () =>
            {
                Record("close-window");
                Coordinator.MarkClosed();
            },
            ObserveStartupFailure = ObservedStartupFailures.Enqueue
        };

        public Task ReachedAsync(string name) => _reached.GetOrAdd(name, _ => Signal()).Task.WaitAsync(Deadline);

        private void Record(string name)
        {
            Events.Enqueue(name);
            _reached.GetOrAdd(name, _ => Signal()).TrySetResult();
        }

        private Task Run(string name, Func<Task> operation)
        {
            Record(name);
            return operation();
        }

        private static Task Completed() => Task.CompletedTask;
    }
}
