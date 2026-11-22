using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private ImportUiSession? _activeImport;
    private long _importCancellationVersion;

    private sealed class ImportUiSession : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public int Added { get; set; }
        public int Ignored { get; set; }
        public int Failed { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }

    private void CancelImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImport is { } session)
        {
            _importCancellationVersion++;
            session.Cancellation.Cancel();
        }
        CancelImportButton.IsEnabled = false;
    }

    private async Task StopImportAsync()
    {
        _activeImport?.Cancellation.Cancel();
        await _importGate.WaitAsync();
        _importGate.Release();
    }

    private async Task RunImportSessionAsync(Func<ImportUiSession, IProgress<MediaImportProgress>, Task> import)
    {
        if (_initializing) await _activationReady.Task;
        await _importGate.WaitAsync();
        try
        {
            if (_windowClosed || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping) return;
            using var session = new ImportUiSession();
            _activeImport = session;
            CancelImportButton.Content = LocalizationService.T("Cancel");
            CancelImportButton.IsEnabled = true;
            ImportStatusBar.Message = LocalizationService.T("StateProbing");
            ImportStatusBar.IsOpen = true;
            var progress = new InlineProgress<MediaImportProgress>(value => DispatcherQueue.TryEnqueue(() =>
            {
                if (_windowClosed || !ReferenceEquals(_activeImport, session)) return;
                ImportStatusBar.Message = value.IsScanning
                    ? $"{LocalizationService.T("FolderScanning")} {value.Discovered:N0}"
                    : $"{LocalizationService.T("StateProbing")} {value.Analyzed:N0} / {value.Discovered:N0} · {LocalizationService.T("StateFailed")} {value.Failed:N0}";
            }));
            try
            {
                await import(session, progress);
                if (!_windowClosed) ShowInfo(DescribeImport(session), session.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
            catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
            {
                if (!_windowClosed && !_closeSaveInProgress)
                    ShowInfo($"{LocalizationService.T("StateCancelled")} · {DescribeImport(session)}", InfoBarSeverity.Informational);
            }
            catch (Exception exception)
            {
                SafeDiagnostics.Report(exception);
                if (!_windowClosed) ShowInfo($"{exception.Message}\n{DescribeImport(session)}", InfoBarSeverity.Error);
            }
            finally
            {
                _activeImport = null;
                if (!_windowClosed) ImportStatusBar.IsOpen = false;
            }
        }
        finally { _importGate.Release(); }
    }

    private static string DescribeImport(ImportUiSession session) =>
        LocalizationService.T("FilesAdded", session.Added,
            session.Ignored > 0 ? LocalizationService.T("IgnoredFiles", session.Ignored) : string.Empty)
        + (session.Failed > 0 ? $" · {LocalizationService.T("StateFailed")} {session.Failed:N0}" : string.Empty);

    private async Task PublishImportBatchAsync(ImportUiSession session, MediaImportBatchResult batch)
    {
        var published = false;
        await RunOnUiAsync(() =>
        {
            session.Cancellation.Token.ThrowIfCancellationRequested();
            session.Added += ApplyImportResult(batch, showSummary: false).Count;
            session.Ignored += batch.IgnoredCount;
            session.Failed += batch.Failures.Count;
            published = true;
            return Task.CompletedTask;
        });
        if (!published) throw new OperationCanceledException(session.Cancellation.Token);
    }

    private async Task ImportOrderedPathsAsync(ImportUiSession session, IProgress<MediaImportProgress> progress,
        IReadOnlyList<string> paths, string? sourceRoot, string? accessToken, bool combineImages)
    {
        var token = session.Cancellation.Token;
        if (combineImages && paths.Count > 1 && paths.All(path => FormatCatalog.FindByExtension(path)?.ContentKind == ContentKind.Image))
        {
            progress.Report(new(false, paths.Count, 0, 0));
            var item = await _mediaImportCoordinator.ImportImageCollectionAsync(_ffprobePath, paths, sourceRoot, accessToken, token);
            try { await PublishImportBatchAsync(session, new(new[] { item })); }
            catch { if (item.Task is { } task) _mediaImportCoordinator.Forget(task); throw; }
            return;
        }
        await _mediaImportCoordinator.ImportPathsProgressivelyAsync(_ffprobePath, paths,
            batch => PublishImportBatchAsync(session, batch), sourceRoot, accessToken, progress, token);
    }

    private Task AddFolderAsync(string folderPath, string? sourceRootAccessToken = null) =>
        RunImportSessionAsync(async (session, progress) =>
        {
            IReadOnlyList<string> files;
            try { files = await _mediaImportCoordinator.DiscoverFolderAsync(folderPath, progress, session.Cancellation.Token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new IOException(LocalizationService.T("FolderReadFailed", exception.Message), exception);
            }
            await ImportOrderedPathsAsync(session, progress, files, folderPath, sourceRootAccessToken, combineImages: true);
        });

    private Task AddPathsAsync(IEnumerable<string> paths, string? sourceRoot, string? sourceRootAccessToken = null) =>
        RunImportSessionAsync((session, progress) => ImportOrderedPathsAsync(session, progress,
            paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(), sourceRoot, sourceRootAccessToken, combineImages: false));

    private Task AddIncomingPathsAsync(IEnumerable<string> paths, string? sourceRoot, string? sourceRootAccessToken = null) =>
        RunImportSessionAsync((session, progress) => ImportOrderedPathsAsync(session, progress,
            paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(), sourceRoot, sourceRootAccessToken, combineImages: true));
}
