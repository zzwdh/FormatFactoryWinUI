using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Windows.Services.Store;

namespace FormatFactoryWinUI.Services;

/// <summary>Store owns package verification and deployment; this client never installs during a check.</summary>
internal sealed class WindowsStoreUpdateClient(IntPtr windowHandle, DispatcherQueue dispatcher) : IStoreUpdateClient
{
    private StoreContext? _context;
    private IReadOnlyList<StorePackageUpdate> _updates = [];
    private bool _downloaded;

    public bool IsAvailable
    {
        get
        {
            if (windowHandle == IntPtr.Zero || !StoreReviewService.IsStoreInstalled) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private StoreContext Context
    {
        get
        {
            if (_context is not null) return _context;
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, windowHandle);
            return _context = context;
        }
    }

    public Task<StoreUpdateCheckResult> CheckAsync(CancellationToken cancellationToken) =>
        RunOnUiAsync(() => CheckOnUiAsync(cancellationToken), cancellationToken);

    private async Task<StoreUpdateCheckResult> CheckOnUiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var updates = await Context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _updates = updates;
        _downloaded = false;
        return new(updates.Count > 0, Context.CanSilentlyDownloadStorePackageUpdates);
    }

    public Task<bool> DownloadAsync(CancellationToken cancellationToken) =>
        RunOnUiAsync(() => DownloadOnUiAsync(cancellationToken), cancellationToken);

    private async Task<bool> DownloadOnUiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updates.Count == 0 || !IsAvailable || !Context.CanSilentlyDownloadStorePackageUpdates) return false;
        var result = await Context.TrySilentDownloadStorePackageUpdatesAsync(_updates).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _downloaded = Completed(result);
        return _downloaded;
    }

    public Task<bool> InstallAsync(CancellationToken cancellationToken) =>
        RunOnUiAsync(() => InstallOnUiAsync(cancellationToken), cancellationToken);

    private async Task<bool> InstallOnUiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_downloaded || !IsAvailable || !Context.CanSilentlyDownloadStorePackageUpdates) return false;
        // This can terminate the process. The caller must have stopped and saved the session first.
        // Once handed to Store, deployment may need this process to release its files. Canceling
        // our bounded exit wait or disposing the window must not cancel that submitted deployment.
        var result = await Context.TrySilentDownloadAndInstallStorePackageUpdatesAsync(_updates).AsTask();
        return Completed(result);
    }

    private static bool Completed(StorePackageUpdateResult result) =>
        result.OverallState == StorePackageUpdateState.Completed
        && result.StorePackageUpdateStatuses.All(status => status.PackageUpdateState == StorePackageUpdateState.Completed);

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await action());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new InvalidOperationException("The update dispatcher is unavailable."));
        return completion.Task;
    }
}
