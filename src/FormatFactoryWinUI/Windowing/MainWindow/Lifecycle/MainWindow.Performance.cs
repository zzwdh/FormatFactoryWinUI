using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _startupReadinessTimer;
    private bool _startupReadinessScheduled;
    private bool _startupFileReadyRecorded;

    private void Startup_FirstRendering(object? sender, object args)
    {
        if (_windowClosed)
        {
            CompositionTarget.Rendering -= Startup_FirstRendering;
            return;
        }
        // Loaded/Activate alone do not establish that a visible frame was reached.
        // This is a first-render proxy; DWM presentation latency still requires ETW.
        if (!RootGrid.IsLoaded || _appWindow?.IsVisible != true) return;
        CompositionTarget.Rendering -= Startup_FirstRendering;
        _startupMetrics.Mark("window-visible");
        ScheduleAddFilesReadiness();
    }

    private void ScheduleAddFilesReadiness()
    {
        if (_windowClosed || _initializing || _startupReadinessScheduled || _startupFileReadyRecorded) return;
        // Tray startup can remain hidden indefinitely. FirstRendering schedules this
        // observation when a visible frame arrives; do not poll a never-shown window.
        if (!_startupMetrics.Snapshot().ContainsKey("window-visible")) return;
        _startupReadinessScheduled = true;
        // Return to the dispatcher after publishing the restored queue. A flag set
        // while the UI thread is still occupied is not an interactive milestone.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _startupReadinessScheduled = false;
            if (_windowClosed) return;
            if (_initializing || !RootGrid.IsLoaded || _appWindow?.IsVisible != true
                || !ConverterView.AddFilesButton.IsEnabled || !DesktopShortcutsAvailable
                || !_startupMetrics.Snapshot().ContainsKey("window-visible"))
            {
                // Activation imports or a startup dialog can temporarily occupy the
                // window. Observe until they finish rather than lose the milestone.
                if (_startupReadinessTimer is null)
                {
                    _startupReadinessTimer = DispatcherQueue.CreateTimer();
                    _startupReadinessTimer.Interval = TimeSpan.FromMilliseconds(50);
                    _startupReadinessTimer.Tick += StartupReadinessTimer_Tick;
                }
                _startupReadinessTimer.Start();
                return;
            }
            _startupFileReadyRecorded = true;
            StopStartupReadinessObservation();
            _startupMetrics.Mark("add-files-ready");
            PublishStartupReadyTelemetry();
        })) _startupReadinessScheduled = false;
    }

    private void StartupReadinessTimer_Tick(DispatcherQueueTimer sender, object args) => ScheduleAddFilesReadiness();

    private void StopStartupReadinessObservation()
    {
        _startupReadinessTimer?.Stop();
        if (_startupReadinessTimer is not null) _startupReadinessTimer.Tick -= StartupReadinessTimer_Tick;
        _startupReadinessTimer = null;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private DispatcherQueueTimer? _progressTimer;
    private readonly CancellationTokenSource _taskbarOutputLifetime = new();
    private bool _taskbarOutputCheckInProgress;

    private async Task RefreshTaskbarOutputAvailabilityAsync(OutputAvailabilityCheck[] checks)
    {
        _taskbarOutputCheckInProgress = true;
        try
        {
            var results = await OutputAvailabilityCache.ProbeAsync(checks, PathExists, _taskbarOutputLifetime.Token);
            if (!_windowClosed) _queuePage.ApplyOutputAvailabilityResults(results);
        }
        catch (OperationCanceledException) when (_taskbarOutputLifetime.IsCancellationRequested) { }
        finally
        {
            _taskbarOutputCheckInProgress = false;
            if (!_windowClosed) UpdateTaskbarProgress();
        }
    }

    private void ScheduleTaskbarOutputAvailabilityCheck()
    {
        if (_windowClosed || _taskbarOutputCheckInProgress) return;
        var checks = _queuePage.CaptureOutputAvailabilityChecks();
        if (checks.Length > 0) _ = RefreshTaskbarOutputAvailabilityAsync(checks);
    }
    private bool _queueRefreshScheduled;
    private bool _queueSaveRequested;
    private bool _windowClosed
    {
        get => _lifecycle.IsClosed;
        set { if (value) _lifecycle.MarkClosed(); else _lifecycle.IsClosed = false; }
    }
    private readonly (CancellationTokenSource Source, CancellationToken Token) _historyIndexLifetime = CreateHistoryIndexLifetime();

    private static (CancellationTokenSource Source, CancellationToken Token) CreateHistoryIndexLifetime()
    {
        var source = new CancellationTokenSource();
        return (source, source.Token);
    }

    private void CancelHistoryIndexing()
    {
        if (!_historyIndexLifetime.Source.IsCancellationRequested)
            _historyIndexLifetime.Source.Cancel();
    }

    private void DisposeHistoryIndexing()
    {
        CancelHistoryIndexing();
        _historyIndexLifetime.Source.Dispose();
    }

    private void ScheduleQueueRefresh(bool save = true)
    {
        if (_windowClosed) return;
        _queueSaveRequested |= save && !_initializing;
        if (_queueRefreshScheduled) return;
        _queueRefreshScheduled = true;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            _queueRefreshScheduled = false;
            var saveRequested = _queueSaveRequested;
            _queueSaveRequested = false;
            if (_windowClosed) return;
            RefreshVisibleTasks();
            UpdateQueueSummary();
            if (saveRequested)
            {
                try { await SaveQueueAsync(); }
                catch (Exception ex)
                {
                    // A full or inaccessible disk may also prevent writing the log.
                    try { SafeDiagnostics.Report(ex); }
                    catch (Exception logError) { System.Diagnostics.Debug.WriteLine(logError); }
                    if (!_windowClosed) ShowInfo(ex.Message, InfoBarSeverity.Warning);
                }
            }
        }))
        {
            _queueRefreshScheduled = false;
        }
    }

    private void FlushPendingProgress(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_windowClosed) { _queuePage.DiscardPendingProgress(); return; }
        var changed = _queuePage.DrainProgress();
        foreach (var task in changed) UpdateTaskNotification(task);
        if (changed.Length > 0) UpdateTaskbarProgress();
    }

    private async Task RecordCompletedHistoryAsync(ConversionHistoryItem item)
    {
        try
        {
            // The coordinator still tracks this callback, but the conversion slot is free.
            await Task.Run(async () =>
            {
                var recorded = await _historyStore.RecordCompletedAsync(item).ConfigureAwait(false);
                if (recorded is null) throw new InvalidDataException(LocalizationService.T("OutputRecoveryMissing"));
                if (recorded is not null)
                    await WindowsSearchService.IndexCompletedAsync(recorded, _historyIndexLifetime.Token).ConfigureAwait(false);
            });
        }
        catch (OperationCanceledException) when (_historyIndexLifetime.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SafeDiagnostics.Report(ex);
            await RunOnUiAsync(() =>
            {
                if (!_windowClosed) ShowInfo(ex.Message, InfoBarSeverity.Warning);
                return Task.CompletedTask;
            });
            throw;
        }
    }

    private async Task InitializeSupplementaryServicesAsync()
    {
        try
        {
            if (_windowClosed) return;
            await JumpListService.InitializeAsync();
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Report(ex);
        }
    }
}
