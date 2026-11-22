using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class StoreUpdateCoordinatorTests
{
    [Fact]
    public async Task AutomaticUpdatesAreDisabledByDefaultAndDoNotQueryTheStore()
    {
        var client = new FakeStoreUpdateClient();
        using var coordinator = new StoreUpdateCoordinator(client);

        await coordinator.CheckAsync(manual: false);
        Assert.False(await coordinator.TryInstallOnExitAsync());

        Assert.False(coordinator.EnableAutomaticUpdates);
        Assert.Equal(StoreUpdateStatus.Disabled, coordinator.Status);
        Assert.False(coordinator.IsBusy);
        Assert.Equal(0, client.CheckCalls);
        Assert.Equal(0, client.DownloadCalls);
        Assert.Equal(0, client.InstallCalls);
    }

    [Fact]
    public async Task CompletedDownloadIsInstalledOnlyWhenExiting()
    {
        var client = new FakeStoreUpdateClient();
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);

        Assert.False(await coordinator.TryInstallOnExitAsync());
        await coordinator.CheckAsync(manual: false);

        Assert.Equal(StoreUpdateStatus.Ready, coordinator.Status);
        Assert.Equal(1, client.DownloadCalls);
        Assert.Equal(0, client.InstallCalls);

        Assert.True(await coordinator.TryInstallOnExitAsync());
        Assert.Equal(StoreUpdateStatus.UpToDate, coordinator.Status);
        Assert.Equal(1, client.InstallCalls);
        Assert.False(await coordinator.TryInstallOnExitAsync());
        Assert.Equal(1, client.InstallCalls);
    }

    [Fact]
    public async Task PartialDownloadNeverBecomesReadyOrInstalls()
    {
        var client = new FakeStoreUpdateClient { DownloadHandler = _ => Task.FromResult(false) };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);

        await coordinator.CheckAsync(manual: false);

        Assert.Equal(StoreUpdateStatus.Failed, coordinator.Status);
        Assert.False(await coordinator.TryInstallOnExitAsync());
        Assert.Equal(0, client.InstallCalls);
    }

    [Fact]
    public async Task ManualCheckWorksWithAutomaticUpdatesDisabledAndRequiresTheStoreForDownload()
    {
        var client = new FakeStoreUpdateClient();
        using var coordinator = new StoreUpdateCoordinator(client);

        await coordinator.CheckAsync(manual: true);

        Assert.Equal(1, client.CheckCalls);
        Assert.Equal(0, client.DownloadCalls);
        Assert.Equal(StoreUpdateStatus.NeedsStore, coordinator.Status);
        Assert.False(coordinator.EnableAutomaticUpdates);
        Assert.False(await coordinator.TryInstallOnExitAsync());
        Assert.Equal(0, client.InstallCalls);
    }

    [Fact]
    public async Task DisablingDuringManualQueryAllowsItsResultButPreventsDownload()
    {
        var release = new TaskCompletionSource<StoreUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = token => { observedToken = token; return release.Task; }
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var checking = coordinator.CheckAsync(manual: true);

        coordinator.Configure(false);
        Assert.False(observedToken.IsCancellationRequested);
        release.SetResult(new StoreUpdateCheckResult(true, true));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreUpdateStatus.NeedsStore, coordinator.Status);
        Assert.Equal(0, client.DownloadCalls);
        Assert.False(await coordinator.TryInstallOnExitAsync());
    }

    [Fact]
    public async Task DisablingDuringManualDownloadCancelsItJustLikeAnAutomaticDownload()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var client = new FakeStoreUpdateClient
        {
            DownloadHandler = token => { observedToken = token; return release.Task; }
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var checking = coordinator.CheckAsync(manual: true);

        coordinator.Configure(false);
        Assert.True(observedToken.IsCancellationRequested);
        release.SetResult(true);
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreUpdateStatus.Disabled, coordinator.Status);
        Assert.False(await coordinator.TryInstallOnExitAsync());
    }

    [Fact]
    public async Task StoreUnavailableDoesNotQueryOrDownload()
    {
        var client = new FakeStoreUpdateClient { IsAvailable = false };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);

        await coordinator.CheckAsync(manual: false);

        Assert.Equal(StoreUpdateStatus.Unavailable, coordinator.Status);
        Assert.False(coordinator.IsBusy);
        Assert.Equal(0, client.CheckCalls);
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public async Task MissingSilentDownloadPermissionRequiresTheStoreWithoutOpeningAPrompt()
    {
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = _ => Task.FromResult(new StoreUpdateCheckResult(true, false))
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);

        await coordinator.CheckAsync(manual: false);

        Assert.Equal(StoreUpdateStatus.NeedsStore, coordinator.Status);
        Assert.Equal(0, client.DownloadCalls);
        Assert.Equal(0, client.InstallCalls);
    }

    [Fact]
    public async Task NoAvailableUpdateCompletesWithoutDownloading()
    {
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = _ => Task.FromResult(new StoreUpdateCheckResult(false, false))
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);

        await coordinator.CheckAsync(manual: false);

        Assert.Equal(StoreUpdateStatus.UpToDate, coordinator.Status);
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public async Task NetworkFailureIsReportedWithoutEscapingTheCoordinator()
    {
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = _ => throw new IOException("network unavailable")
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);

        await coordinator.CheckAsync(manual: false);

        Assert.Equal(StoreUpdateStatus.Failed, coordinator.Status);
        Assert.False(coordinator.IsBusy);
        Assert.False(await coordinator.TryInstallOnExitAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInstallationDoesNotPreventClosing(bool throws)
    {
        var client = new FakeStoreUpdateClient
        {
            InstallHandler = _ => throws ? throw new IOException("store offline") : Task.FromResult(false)
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        await coordinator.CheckAsync(manual: false);

        Assert.False(await coordinator.TryInstallOnExitAsync());

        Assert.Equal(StoreUpdateStatus.Failed, coordinator.Status);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task DisablingDuringCheckCancelsItAndPreventsDownload()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<StoreUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = token => { observedToken = token; entered.SetResult(); return release.Task; }
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var checking = coordinator.CheckAsync(manual: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Configure(false);
        release.SetResult(new StoreUpdateCheckResult(true, true));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Equal(StoreUpdateStatus.Disabled, coordinator.Status);
        Assert.Equal(0, client.DownloadCalls);
        Assert.False(await coordinator.TryInstallOnExitAsync());
    }

    [Fact]
    public async Task DisablingDuringDownloadDiscardsLateSuccessAndPreventsInstallation()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var client = new FakeStoreUpdateClient
        {
            DownloadHandler = token => { observedToken = token; entered.SetResult(); return release.Task; }
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var checking = coordinator.CheckAsync(manual: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Configure(false);
        release.SetResult(true);
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Equal(StoreUpdateStatus.Disabled, coordinator.Status);
        Assert.False(await coordinator.TryInstallOnExitAsync());
        coordinator.Configure(true);
        Assert.False(await coordinator.TryInstallOnExitAsync());
        Assert.Equal(0, client.InstallCalls);
    }

    [Fact]
    public async Task DisablingAfterDownloadPreventsExitInstallAndRetainsTheSessionDownload()
    {
        var client = new FakeStoreUpdateClient();
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        await coordinator.CheckAsync(manual: false);

        coordinator.Configure(false);
        Assert.Equal(StoreUpdateStatus.Disabled, coordinator.Status);
        Assert.False(await coordinator.TryInstallOnExitAsync());

        coordinator.Configure(true);
        await coordinator.CheckAsync(manual: false);
        Assert.Equal(StoreUpdateStatus.Ready, coordinator.Status);
        Assert.Equal(1, client.CheckCalls);
        Assert.Equal(1, client.DownloadCalls);
        Assert.True(await coordinator.TryInstallOnExitAsync());
    }

    [Fact]
    public async Task ConcurrentManualAutomaticAndExitRequestsNeverOverlapClientWork()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<StoreUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = _ => { entered.SetResult(); return release.Task; }
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var checking = coordinator.CheckAsync(manual: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(coordinator.IsBusy);
        Assert.True(coordinator.CheckAsync(manual: true).IsCompletedSuccessfully);
        Assert.True(coordinator.CheckAsync(manual: false).IsCompletedSuccessfully);
        Assert.False(await coordinator.TryInstallOnExitAsync());
        Assert.Equal(1, client.CheckCalls);

        release.SetResult(new StoreUpdateCheckResult(true, true));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.IsBusy);
        Assert.Equal(1, client.DownloadCalls);
    }

    [Fact]
    public async Task CancelledCheckReturnsWithoutFailureAndAllowsRetry()
    {
        var entered = Signal();
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new StoreUpdateCheckResult(true, true);
            }
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        using var cancellation = new CancellationTokenSource();
        coordinator.Configure(true);
        var checking = coordinator.CheckAsync(manual: false, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreUpdateStatus.Idle, coordinator.Status);
        Assert.False(coordinator.IsBusy);
        client.CheckHandler = _ => Task.FromResult(new StoreUpdateCheckResult(false, true));
        await coordinator.CheckAsync(manual: false);
        Assert.Equal(StoreUpdateStatus.UpToDate, coordinator.Status);
        Assert.Equal(2, client.CheckCalls);
    }

    [Fact]
    public async Task CancelledInstallationReturnsFalseAndKeepsDownloadAvailable()
    {
        var entered = Signal();
        var client = new FakeStoreUpdateClient
        {
            InstallHandler = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            }
        };
        using var coordinator = new StoreUpdateCoordinator(client);
        using var cancellation = new CancellationTokenSource();
        coordinator.Configure(true);
        await coordinator.CheckAsync(manual: false);
        var installing = coordinator.TryInstallOnExitAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        Assert.False(await installing.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreUpdateStatus.Ready, coordinator.Status);
        Assert.False(coordinator.IsBusy);
        client.InstallHandler = _ => Task.FromResult(true);
        Assert.True(await coordinator.TryInstallOnExitAsync());
    }

    [Fact]
    public async Task RepeatedExitWhileInstallingDoesNotStartASecondInstallation()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeStoreUpdateClient { InstallHandler = _ => release.Task };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        await coordinator.CheckAsync(manual: false);
        var installing = coordinator.TryInstallOnExitAsync();

        Assert.Equal(StoreUpdateStatus.Installing, coordinator.Status);
        Assert.True(coordinator.IsBusy);
        Assert.False(await coordinator.TryInstallOnExitAsync());
        await coordinator.CheckAsync(manual: true);
        Assert.Equal(1, client.InstallCalls);
        Assert.Equal(1, client.CheckCalls);

        release.SetResult(true);
        Assert.True(await installing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task RapidPreferenceChangesCannotPublishOldAutomaticSuccess()
    {
        var release = new TaskCompletionSource<StoreUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeStoreUpdateClient { CheckHandler = _ => release.Task };
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var checking = coordinator.CheckAsync(manual: false);

        coordinator.Configure(false);
        coordinator.Configure(true);
        release.SetResult(new StoreUpdateCheckResult(true, true));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreUpdateStatus.Idle, coordinator.Status);
        Assert.Equal(0, client.DownloadCalls);
        Assert.False(await coordinator.TryInstallOnExitAsync());
    }

    [Fact]
    public async Task DisablingFromStatusCallbackPreventsTheNextAutomaticClientCall()
    {
        var client = new FakeStoreUpdateClient();
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        coordinator.StatusChanged += status =>
        {
            if (status == StoreUpdateStatus.Downloading)
                coordinator.Configure(false);
        };

        await coordinator.CheckAsync(manual: false);

        Assert.Equal(StoreUpdateStatus.Disabled, coordinator.Status);
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public async Task CompletionPublishesANonBusyStatusEvenIfASubscriberThrows()
    {
        var client = new FakeStoreUpdateClient();
        using var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var statuses = new List<(StoreUpdateStatus Status, bool Busy)>();
        coordinator.StatusChanged += _ => throw new InvalidOperationException("UI callback");
        coordinator.StatusChanged += status => statuses.Add((status, coordinator.IsBusy));

        await coordinator.CheckAsync(manual: false);

        Assert.Equal((StoreUpdateStatus.Checking, true), statuses[0]);
        Assert.Equal((StoreUpdateStatus.Ready, false), statuses[^1]);
        Assert.Equal(StoreUpdateStatus.Ready, coordinator.Status);
    }

    [Fact]
    public async Task DisposeCancelsWorkAndSuppressesSubsequentCallbacks()
    {
        var release = new TaskCompletionSource<StoreUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var client = new FakeStoreUpdateClient
        {
            CheckHandler = token => { observedToken = token; return release.Task; }
        };
        var coordinator = new StoreUpdateCoordinator(client);
        coordinator.Configure(true);
        var statuses = new List<StoreUpdateStatus>();
        coordinator.StatusChanged += statuses.Add;
        var checking = coordinator.CheckAsync(manual: false);
        coordinator.Dispose();
        var callbackCount = statuses.Count;

        release.SetResult(new StoreUpdateCheckResult(true, true));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Configure(true);
        await coordinator.CheckAsync(manual: true);

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Equal(callbackCount, statuses.Count);
        Assert.Equal(0, client.DownloadCalls);
        Assert.False(await coordinator.TryInstallOnExitAsync());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeStoreUpdateClient : IStoreUpdateClient
    {
        public bool IsAvailable { get; set; } = true;
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public Func<CancellationToken, Task<StoreUpdateCheckResult>> CheckHandler { get; set; } =
            _ => Task.FromResult(new StoreUpdateCheckResult(true, true));
        public Func<CancellationToken, Task<bool>> DownloadHandler { get; set; } = _ => Task.FromResult(true);
        public Func<CancellationToken, Task<bool>> InstallHandler { get; set; } = _ => Task.FromResult(true);

        public Task<StoreUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            return CheckHandler(cancellationToken);
        }

        public Task<bool> DownloadAsync(CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return DownloadHandler(cancellationToken);
        }

        public Task<bool> InstallAsync(CancellationToken cancellationToken)
        {
            InstallCalls++;
            return InstallHandler(cancellationToken);
        }
    }
}
