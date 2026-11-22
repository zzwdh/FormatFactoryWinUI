using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private bool _batchOptionsDialogOpen;

    private void InitializeBatchActions()
    {
        ConverterView.SelectEditableRequested += SelectEditableTasks_Click;
        ConverterView.SaveTaskPresetButton_ClickRequested += SaveTaskPresetButton_Click;
        ConverterView.BatchOptions_ClickRequested += BatchOptions_Click;
        ConverterView.RetryFailed_ClickRequested += RetryFailedTasks_Click;
        ConverterView.SortPendingByName_ClickRequested += SortPendingByName_Click;
        ConverterView.SortPendingSmallest_ClickRequested += SortPendingSmallest_Click;
        ConverterView.SortPendingLargest_ClickRequested += SortPendingLargest_Click;
    }

    private void DisposeBatchActions()
    {
        ConverterView.SelectEditableRequested -= SelectEditableTasks_Click;
        ConverterView.SaveTaskPresetButton_ClickRequested -= SaveTaskPresetButton_Click;
        ConverterView.BatchOptions_ClickRequested -= BatchOptions_Click;
        ConverterView.RetryFailed_ClickRequested -= RetryFailedTasks_Click;
        ConverterView.SortPendingByName_ClickRequested -= SortPendingByName_Click;
        ConverterView.SortPendingSmallest_ClickRequested -= SortPendingSmallest_Click;
        ConverterView.SortPendingLargest_ClickRequested -= SortPendingLargest_Click;
    }

    private void SelectEditableTasks_Click(object sender, RoutedEventArgs args)
    {
        var editable = _queueOperations.PrepareBatchEdit(VisibleTasks.Select(task => task.Id)).EditableTasks;
        ConverterView.ApplySelection(_queuePage.SelectEditable(editable.Select(task => task.Id)));
    }

    private void RetryFailedTasks_Click(object sender, RoutedEventArgs args)
    {
        if (_settingsResetInProgress || _queueSession.IsStopping) return;
        var started = _queueOperations.RetryFailed(IsQueueTaskReady, StartQueueModel);
        ShowInfo(LocalizationService.T(started == 0 ? "QueueBatchNoRetry" : "QueueBatchRetried", started),
            started == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
    }

    private async void BatchOptions_Click(object sender, RoutedEventArgs args)
    {
        if (_windowClosed || _initializing || _closeSaveInProgress || _batchOptionsDialogOpen
            || _settingsResetInProgress || _queueSession.IsStopping || _reviewBusinessDialogs > 0) return;
        var plan = _queueOperations.PrepareBatchEdit(ConverterView.SelectedTaskIds);
        var selectedIds = plan.SelectedIds;
        var editable = plan.EditableTasks;
        if (editable.Length == 0)
        {
            ShowInfo(LocalizationService.T("QueueBatchSelectHint"), InfoBarSeverity.Informational);
            return;
        }
        _batchOptionsDialogOpen = true;
        try
        {
            var commonFormats = plan.CommonFormats;
            var formatOptions = new List<LocalizedOption> { new(string.Empty, LocalizationService.T("QueueBatchKeepCurrent")) };
            formatOptions.AddRange(commonFormats.Select(format => new LocalizedOption(format, format)));
            var presetOptions = new List<LocalizedOption> { new(string.Empty, LocalizationService.T("QueueBatchKeepCurrent")) };
            presetOptions.AddRange(FormatCatalog.ConversionModes.Select(mode => new LocalizedOption(mode, LocalizationService.ModeLabel(mode))));
            var formatCombo = new ComboBox
            {
                Header = LocalizationService.T("FormatHeader"), ItemsSource = formatOptions,
                DisplayMemberPath = nameof(LocalizedOption.DisplayName), SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var presetCombo = new ComboBox
            {
                Header = LocalizationService.T("PresetHeader"), ItemsSource = presetOptions,
                DisplayMemberPath = nameof(LocalizedOption.DisplayName), SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(formatCombo, LocalizationService.T("FormatHeader"));
            AutomationProperties.SetName(presetCombo, LocalizationService.T("PresetHeader"));
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = LocalizationService.T("QueueBatchEditHint", editable.Length), TextWrapping = TextWrapping.Wrap
            });
            if (commonFormats.Length == 0)
                content.Children.Add(new TextBlock
                {
                    Text = LocalizationService.T("QueueBatchNoCommonFormat"), TextWrapping = TextWrapping.Wrap
                });
            content.Children.Add(formatCombo);
            content.Children.Add(presetCombo);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                FlowDirection = RootGrid.FlowDirection, Title = LocalizationService.T("QueueBatchOptions"),
                Content = content, PrimaryButtonText = LocalizationService.T("QueueBatchApply"),
                CloseButtonText = LocalizationService.T("NativeUiCancel"),
                DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
            };
            void UpdateApply(object? _, SelectionChangedEventArgs __) =>
                dialog.IsPrimaryButtonEnabled = formatCombo.SelectedIndex > 0 || presetCombo.SelectedIndex > 0;
            formatCombo.SelectionChanged += UpdateApply;
            presetCombo.SelectionChanged += UpdateApply;
            if (await ShowContentDialogAsync(dialog) != ContentDialogResult.Primary || _windowClosed) return;
            var target = (formatCombo.SelectedItem as LocalizedOption)?.Key;
            var preset = (presetCombo.SelectedItem as LocalizedOption)?.Key;
            var changed = _queueSession.ApplyBatchOptionsWithUndo(selectedIds,
                string.IsNullOrEmpty(target) ? null : target, string.IsNullOrEmpty(preset) ? null : preset);
            ScheduleQueueRefresh();
            ShowInfo(LocalizationService.T("QueueBatchUpdated", changed),
                changed == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally { _batchOptionsDialogOpen = false; }
    }

    private void SortPendingByName_Click(object sender, RoutedEventArgs args) => SortPendingTasks(QueueSortOrder.FileName);
    private void SortPendingSmallest_Click(object sender, RoutedEventArgs args) => SortPendingTasks(QueueSortOrder.SmallestFirst);
    private void SortPendingLargest_Click(object sender, RoutedEventArgs args) => SortPendingTasks(QueueSortOrder.LargestFirst);

    private void SortPendingTasks(QueueSortOrder order)
    {
        if (_settingsResetInProgress || _queueSession.IsStopping) return;
        var changed = _queueSession.ReorderPendingTasks(order);
        if (changed > 0)
        {
            _queuePage.SynchronizeOrder(_queueSession.Tasks.Select(task => task.Id));
            ScheduleQueueRefresh();
        }
        ShowInfo(LocalizationService.T("QueueBatchSorted", changed), InfoBarSeverity.Informational);
    }
}
