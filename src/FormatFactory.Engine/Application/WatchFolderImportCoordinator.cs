using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>
/// Coordinates the watch-folder lifecycle with the same import pipeline used by
/// pickers, drag/drop and activation.
/// </summary>
public sealed class WatchFolderImportCoordinator : IDisposable
{
    private readonly MediaImportCoordinator _importCoordinator;
    private readonly WatchFolderService _watchFolderService;
    private readonly object _gate = new();
    private Func<string> _ffprobePathAccessor = static () => string.Empty;
    private string? _sourceRoot;
    private string? _sourceRootAccessToken;
    private Func<string, bool>? _shouldImport;
    private CancellationTokenSource _configurationCancellation = new();
    private bool _disposed;

    public WatchFolderImportCoordinator(MediaImportCoordinator importCoordinator)
        : this(importCoordinator, new WatchFolderService())
    {
    }

    internal WatchFolderImportCoordinator(
        MediaImportCoordinator importCoordinator,
        WatchFolderService watchFolderService)
    {
        _importCoordinator = importCoordinator ?? throw new ArgumentNullException(nameof(importCoordinator));
        _watchFolderService = watchFolderService ?? throw new ArgumentNullException(nameof(watchFolderService));
        _watchFolderService.FileDiscovered += WatchFolderService_FileDiscovered;
    }

    public event EventHandler<MediaImportBatchResult>? ImportCompleted;

    public bool IsWatching => _watchFolderService.IsWatching;

    public void Configure(AppSettings settings, Func<string> ffprobePathAccessor, Func<string, bool>? shouldImport = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ffprobePathAccessor);

        _watchFolderService.Stop();
        CancellationTokenSource previousCancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previousCancellation = _configurationCancellation;
            _configurationCancellation = new CancellationTokenSource();
            _ffprobePathAccessor = ffprobePathAccessor;
            _sourceRoot = settings.WatchFolderPath;
            _sourceRootAccessToken = settings.WatchFolderAccessToken;
            _shouldImport = shouldImport;
        }
        previousCancellation.Cancel();
        previousCancellation.Dispose();
        if (settings.WatchFolderEnabled && Directory.Exists(settings.WatchFolderPath))
        {
            _watchFolderService.Start(settings.WatchFolderPath);
            LogService.WriteApp(LocalizationService.T("OperationalLogWatchFolderStarted", settings.WatchFolderPath));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _configurationCancellation;
        }

        cancellation.Cancel();
        _watchFolderService.FileDiscovered -= WatchFolderService_FileDiscovered;
        _watchFolderService.Dispose();
        cancellation.Dispose();
    }

    private void WatchFolderService_FileDiscovered(object? sender, string path) =>
        _ = ImportDiscoveredFileAsync(path);

    private async Task ImportDiscoveredFileAsync(string path)
    {
        try
        {
            Func<string> ffprobePathAccessor;
            string? sourceRoot;
            string? sourceRootAccessToken;
            CancellationToken cancellationToken;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                if (_shouldImport?.Invoke(path) == false) return;

                ffprobePathAccessor = _ffprobePathAccessor;
                sourceRoot = _sourceRoot;
                sourceRootAccessToken = _sourceRootAccessToken;
                cancellationToken = _configurationCancellation.Token;
            }

            var ffprobePath = ffprobePathAccessor();

            var result = await _importCoordinator.ImportPathsAsync(
                ffprobePath,
                [path],
                sourceRoot,
                sourceRootAccessToken,
                cancellationToken);
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested ||
                    cancellationToken != _configurationCancellation.Token)
                {
                    foreach (var task in result.AddedTasks)
                    {
                        _importCoordinator.Forget(task);
                    }
                }
                else if (result.Items.Count > 0)
                {
                    ImportCompleted?.Invoke(this, result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Reconfiguration and shutdown intentionally cancel an in-flight import.
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogWatchFolderImportFailed", path, ex.Message));
        }
    }
}
