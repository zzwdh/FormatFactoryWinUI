using System.Reflection;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly FeedbackReportService _feedbackReports = new();
    private readonly CancellationTokenSource _feedbackCancellation = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _feedbackTaskLogs = new();
    private bool _feedbackInProgress;
    private ContentDialog? _feedbackDialog;
    private CancellationTokenSource? _feedbackOperationCancellation;
    private TaskCompletionSource<bool>? _feedbackOperationClosed;
    private int _manualFeedbackSending;

    internal void RefreshFeedbackState()
    {
        if (!_windowClosed) SettingsView.SetFeedbackState(_feedbackReports.HasPendingError, _feedbackInProgress);
    }

    internal void RecordFeedbackError(string message, string? logPath = null, Exception? exception = null,
        ErrorCorrelationFacts? correlation = null)
    {
        if (_telemetry is { } telemetry) telemetry.ReportError(message, logPath, exception, correlation);
        else if (exception is not null) _feedbackReports.RecordException(exception, logPath, correlation);
        else _feedbackReports.RecordError(message, logPath, correlation);
        if (DispatcherQueue.HasThreadAccess) RefreshFeedbackState();
        else DispatcherQueue.TryEnqueue(RefreshFeedbackState);
    }

    private async void SendFeedbackButton_Click(object sender, RoutedEventArgs args)
        => await OpenFeedbackAsync(includeLogs: false);

    private void ReportErrorButton_Click(object sender, RoutedEventArgs args)
        => QueueManualFeedback(string.Empty, includeLogs: true);

    private async void ExportErrorReportButton_Click(object sender, RoutedEventArgs args)
        => await OpenFeedbackAsync(includeLogs: true, exportOnly: true);

    private async Task OpenFeedbackAsync(bool includeLogs, bool exportOnly = false)
    {
        if (_windowClosed || _feedbackInProgress || _settingsResetInProgress || _settingsResetRequested
            || _closeSaveInProgress || _queueSession.IsStopping
            || _reviewAttemptInProgress || _reviewBusinessDialogs > 0 || RootGrid.XamlRoot is null) return;
        _feedbackInProgress = true;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_feedbackCancellation.Token);
        _feedbackOperationCancellation = operationCancellation;
        _feedbackOperationClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RefreshFeedbackState();
        var token = operationCancellation.Token;
        var failureKey = "FeedbackPrepareFailed";
        try
        {
            var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(
                Path.Combine(AppContext.BaseDirectory, "feedback-config.json"), token);
            if (_windowClosed || _closeSaveInProgress || token.IsCancellationRequested) return;
            if (!includeLogs && !exportOnly && configuration.FeedbackUri is { } feedbackUri)
            {
                if (!await Launcher.LaunchUriAsync(feedbackUri) && !_windowClosed)
                    ShowSettingsInfo(LocalizationService.T("FeedbackOpenFailed"), InfoBarSeverity.Warning);
                return;
            }

            var description = new TextBox
            {
                Header = LocalizationService.T("FeedbackProblemLabel"),
                PlaceholderText = LocalizationService.T("FeedbackProblemPlaceholder"),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000,
                MinHeight = 120, MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var logs = new CheckBox
            {
                Content = LocalizationService.T("FeedbackIncludeLogs"), IsChecked = includeLogs
            };
            var form = new StackPanel { Spacing = 12, MinWidth = 280, MaxWidth = 520 };
            form.Children.Add(description);
            form.Children.Add(logs);
            form.Children.Add(FeedbackParagraph("FeedbackPrivacy"));
            if (!configuration.CanUpload)
                form.Children.Add(FeedbackParagraph("FeedbackNoEndpoint"));
            if (configuration.PrivacyUri is { } privacyUri)
                form.Children.Add(new HyperlinkButton
                {
                    Content = LocalizationService.T("FeedbackPrivacyLink"), NavigateUri = privacyUri,
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            var compose = FeedbackDialog(form, "FeedbackDialogTitle");
            compose.PrimaryButtonText = LocalizationService.T(!exportOnly && configuration.CanUpload
                ? "FeedbackSend" : "FeedbackExport");
            if (await ShowFeedbackDialogAsync(compose) != ContentDialogResult.Primary || _windowClosed) return;

            if (!exportOnly && configuration.CanUpload)
            {
                QueueManualFeedback(description.Text, logs.IsChecked == true, configuration);
                return;
            }
            await Task.Run(SafeDiagnostics.Flush, token);
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            using var report = await _feedbackReports.CreateAsync(description.Text, logs.IsChecked == true, version, token);
            if (!_windowClosed && !token.IsCancellationRequested) await SaveFeedbackReportAsync(report, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed)
                ShowSettingsInfo(LocalizationService.T(failureKey, exception.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _feedbackInProgress = false;
            _feedbackOperationCancellation = null;
            _feedbackOperationClosed?.TrySetResult(true);
            RefreshFeedbackState();
            ScheduleReviewReminder();
        }
    }

    private void QueueManualFeedback(string description, bool includeLogs, FeedbackConfiguration? configuration = null)
    {
        if (_windowClosed || Interlocked.CompareExchange(ref _manualFeedbackSending, 1, 0) != 0) return;
        var token = _feedbackCancellation.Token;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _ = Task.Run(async () =>
        {
            try
            {
                configuration ??= await FeedbackDeliveryService.LoadConfigurationAsync(
                    Path.Combine(AppContext.BaseDirectory, "feedback-config.json"), token);
                if (!configuration.CanUpload) return;
                SafeDiagnostics.Flush();
                using var report = await _feedbackReports.CreateAsync(description, includeLogs, version, token);
                using var delivery = _services.CreateFeedbackDelivery();
                await delivery.SendAsync(configuration, report, token);
                _feedbackReports.ClearPendingError(report);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Feedback delivery: {exception.GetType().Name}");
            }
            finally
            {
                Interlocked.Exchange(ref _manualFeedbackSending, 0);
                DispatcherQueue.TryEnqueue(RefreshFeedbackState);
            }
        });
    }

    private ContentDialog FeedbackDialog(object content, string titleKey) => new()
    {
        XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
        FlowDirection = RootGrid.FlowDirection, Title = LocalizationService.T(titleKey), Content = content,
        CloseButtonText = LocalizationService.T("FeedbackCancel"), DefaultButton = ContentDialogButton.Close
    };

    private async Task<ContentDialogResult> ShowFeedbackDialogAsync(ContentDialog dialog)
    {
        _feedbackDialog = dialog;
        try
        {
            await DismissReviewReminderAsync();
            if (_windowClosed || _closeSaveInProgress || _feedbackOperationCancellation?.IsCancellationRequested == true)
                return ContentDialogResult.None;
            return await ShowContentDialogAsync(dialog);
        }
        finally { _feedbackDialog = null; }
    }

    private async Task DismissFeedbackAsync()
    {
        var completion = _feedbackOperationClosed?.Task;
        _feedbackOperationCancellation?.Cancel();
        _feedbackDialog?.Hide();
        if (completion is not null) await completion;
    }

    private void DisposeFeedback()
    {
        _feedbackCancellation.Cancel();
        _feedbackDialog?.Hide();
        _feedbackTaskLogs.Clear();
    }

    private static TextBlock FeedbackParagraph(string key) => new()
    {
        Text = LocalizationService.T(key), TextWrapping = TextWrapping.Wrap, Opacity = 0.72
    };

    private async Task SaveFeedbackReportAsync(FeedbackReport report, CancellationToken token)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"FluentFormatConverter-report-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add(LocalizationService.T("FeedbackExportButton"), [".zip"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var file = await picker.PickSaveFileAsync().AsTask(token);
        if (file is null || _windowClosed) return;
        token.ThrowIfCancellationRequested();
        // StorageFile also supports destinations backed by cloud providers.
        var bytes = await File.ReadAllBytesAsync(report.ArchivePath, token);
        await FileIO.WriteBytesAsync(file, bytes);
        if (!_windowClosed)
            ShowSettingsInfo(LocalizationService.T("FeedbackExported", file.Path), InfoBarSeverity.Success);
    }
}
