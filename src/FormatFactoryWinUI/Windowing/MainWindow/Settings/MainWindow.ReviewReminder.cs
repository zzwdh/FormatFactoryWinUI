using FormatFactoryWinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly Task<bool>? _reviewReminderRegistration;
    private DispatcherQueueTimer? _reviewReminderTimer;
    private ContentDialog? _reviewReminderDialog;
    private TaskCompletionSource<bool>? _reviewDialogClosed;
    private bool _reviewWindowActive;
    private bool _reviewPromptPending;
    private bool _reviewAttemptInProgress;
    private bool _reviewShownThisSession;
    private int _reviewActivationInProgress;
    private int _reviewBusinessDialogs;

    private async Task InitializeReviewReminderAsync()
    {
        if (_reviewReminderRegistration is null) return;
        try
        {
            _reviewPromptPending = await _reviewReminderRegistration;
            if (_windowClosed || !_reviewPromptPending) return;
            _reviewReminderTimer = DispatcherQueue.CreateTimer();
            _reviewReminderTimer.Interval = TimeSpan.FromSeconds(2);
            _reviewReminderTimer.IsRepeating = false;
            _reviewReminderTimer.Tick += ReviewReminderTimer_Tick;
            ScheduleReviewReminder();
        }
        catch (Exception exception) { SafeDiagnostics.Report(exception); }
    }

    private void ReviewReminder_WindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _reviewWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_reviewWindowActive) ScheduleReviewReminder();
    }

    private void ScheduleReviewReminder()
    {
        if (!_windowClosed && _reviewPromptPending && !_reviewShownThisSession && !_reviewAttemptInProgress)
            _reviewReminderTimer?.Start();
    }

    private async void ReviewReminderTimer_Tick(DispatcherQueueTimer sender, object args)
        => await TryShowReviewReminderAsync();

    private bool ReviewWindowIsReady => !_windowClosed && !_initializing && !_closeSaveInProgress
        && !_dropImportInProgress
        && !_feedbackInProgress
        && _reviewWindowActive && _reviewActivationInProgress == 0 && _reviewBusinessDialogs == 0
        && _appWindow?.IsVisible == true
        && _appWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Minimized }
        && RootGrid.XamlRoot is not null
        && !VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(popup => popup.IsOpen);

    private async Task TryShowReviewReminderAsync()
    {
        if (_windowClosed || !_reviewPromptPending || _reviewShownThisSession || _reviewAttemptInProgress) return;
        if (!ReviewWindowIsReady)
        {
            if (_reviewWindowActive && _appWindow?.IsVisible == true && !_closeSaveInProgress)
                ScheduleReviewReminder();
            return;
        }

        _reviewAttemptInProgress = true;
        try
        {
            if (!StoreReviewService.CanRequestReview)
            {
                _reviewPromptPending = false;
                return; // Keep the persisted reminder due for a supported future launch.
            }
            if (!await _services.ReviewReminders.IsPromptDueAsync(DateTimeOffset.Now))
            {
                _reviewPromptPending = false;
                return;
            }
            // File activations or other dialogs may have arrived during the disk read.
            if (!ReviewWindowIsReady) return;

            var dialog = CreateReviewReminderDialog();
            ApplyDialogVisuals(dialog);
            _reviewReminderDialog = dialog;
            _reviewDialogClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var shownRecording = Task.CompletedTask;
            dialog.Opened += (_, _) =>
            {
                _reviewShownThisSession = true;
                _reviewPromptPending = false;
                shownRecording = _services.ReviewReminders.RecordPromptShownAsync(DateTimeOffset.Now);
            };
            var choice = await dialog.ShowAsync();
            _reviewReminderDialog = null;
            _reviewDialogClosed?.TrySetResult(true);
            try { await shownRecording; }
            catch (Exception exception) { SafeDiagnostics.Report(exception); }
            // A failure saving the display time must not swallow an explicit opt-out.
            if (choice == ContentDialogResult.Secondary)
                await _services.ReviewReminders.StopRemindersAsync();
            else if (choice == ContentDialogResult.Primary && ReviewWindowIsReady)
            {
                var result = await StoreReviewService.RequestAsync(_windowHandle);
                if (result == StoreReviewResult.Succeeded)
                {
                    await _services.ReviewReminders.StopRemindersAsync();
                    if (!_windowClosed) ShowInfo(LocalizationService.T("ReviewReminderThanks"), InfoBarSeverity.Success);
                }
                else if (result == StoreReviewResult.Failed && !_windowClosed)
                    ShowInfo(LocalizationService.T("ReviewReminderFailed"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            // A failed prompt/storage operation must not keep interrupting this session.
            _reviewPromptPending = false;
        }
        finally
        {
            _reviewReminderDialog = null;
            _reviewDialogClosed?.TrySetResult(true);
            _reviewAttemptInProgress = false;
            ScheduleReviewReminder();
        }
    }

    private ContentDialog CreateReviewReminderDialog() => new()
    {
        XamlRoot = RootGrid.XamlRoot,
        RequestedTheme = RootGrid.ActualTheme,
        FlowDirection = RootGrid.FlowDirection,
        Title = LocalizationService.T("ReviewReminderTitle"),
        Content = LocalizationService.T("ReviewReminderMessage"),
        PrimaryButtonText = LocalizationService.T("ReviewReminderRate"),
        SecondaryButtonText = LocalizationService.T("ReviewReminderNever"),
        CloseButtonText = LocalizationService.T("ReviewReminderLater"),
        DefaultButton = ContentDialogButton.Close
    };

    private async Task HandleActivationWithReviewPriorityAsync(ActivationRequest activation)
    {
        _reviewActivationInProgress++;
        try
        {
            await DismissReviewReminderAsync();
            if (!_windowClosed) await HandleActivationAsync(activation);
        }
        finally
        {
            _reviewActivationInProgress--;
            ScheduleReviewReminder();
        }
    }

    private void DismissReviewReminder() => _reviewReminderDialog?.Hide();

    private async Task DismissReviewReminderAsync()
    {
        var closed = _reviewDialogClosed?.Task;
        DismissReviewReminder();
        if (closed is not null) await closed;
    }

    // Business dialogs may finish asynchronous preparation after the rating prompt opened.
    // Close the prompt fully before showing them, including its dismissal animation.
    private readonly SemaphoreSlim _contentDialogGate = new(1, 1);

    private Task<ContentDialogResult> ShowContentDialogAsync(ContentDialog dialog) =>
        ShowContentDialogWithCancellationAsync(dialog, CancellationToken.None);

    private async Task<ContentDialogResult> ShowContentDialogWithCancellationAsync(ContentDialog dialog, CancellationToken cancellationToken)
    {
        await _contentDialogGate.WaitAsync(cancellationToken);
        _reviewBusinessDialogs++;
        try
        {
            await DismissReviewReminderAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (_windowClosed) return ContentDialogResult.None;
            ApplyDialogVisuals(dialog);
            var dispatcher = DispatcherQueue;
            using var cancellation = cancellationToken.Register(() => dispatcher.TryEnqueue(() => dialog.Hide()));
            return await dialog.ShowAsync();
        }
        finally
        {
            _reviewBusinessDialogs--;
            _contentDialogGate.Release();
            ScheduleReviewReminder();
        }
    }

    private void ApplyDialogVisuals(ContentDialog dialog)
    {
        // Keep the caller's explicit style and default-button/confirmation behavior.
        dialog.Style ??= (Style)ConverterView.Resources["FFContentDialogStyle"];
        if (!dialog.Resources.ContainsKey(typeof(ComboBox)))
            dialog.Resources[typeof(ComboBox)] = ConverterView.Resources["FFSelectorStyle"];
        if (!dialog.Resources.ContainsKey("ContentDialogTitleMargin"))
            dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0, 0, 0, 16);
    }

    private void DisposeReviewReminder()
    {
        _reviewPromptPending = false;
        _reviewReminderTimer?.Stop();
        if (_reviewReminderTimer is not null) _reviewReminderTimer.Tick -= ReviewReminderTimer_Tick;
        Activated -= ReviewReminder_WindowActivated;
        DismissReviewReminder();
        _reviewDialogClosed?.TrySetResult(true);
    }
}
