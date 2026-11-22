using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace FormatFactoryWinUI.Views;

public sealed partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        InitializeCompactCommands();
    }

    private ObservableCollection<MediaTaskViewModel>? _boundTasks;
    private QueuePageViewModel? _pageViewModel;
    private readonly QueueOutputGroups _outputGroups = [];
    private int _outputGroupRefreshDeferrals;
    private bool _outputGroupRefreshPending;
    private CollectionViewSource? _groupedSource;
    private bool _usingGroupedSource;
    internal void BindPage(QueuePageViewModel page)
    {
        _pageViewModel = page;
        BindTasks(page.VisibleTasks);
        UpdateSelectionSummary();
    }
    internal void BindTasks(ObservableCollection<MediaTaskViewModel> tasks)
    {
        if (_boundTasks is not null) _boundTasks.CollectionChanged -= BoundTasksChanged;
        _boundTasks = tasks;
        _boundTasks.CollectionChanged += BoundTasksChanged;
        RefreshOutputGroups();
    }
    internal IDisposable DeferOutputGroupRefresh()
    {
        _outputGroupRefreshDeferrals++;
        return new OutputGroupRefreshScope(this);
    }

    private sealed class OutputGroupRefreshScope(QueueView owner) : IDisposable
    {
        private QueueView? _owner = owner;
        public void Dispose()
        {
            var view = _owner;
            if (view is null) return;
            _owner = null;
            if (--view._outputGroupRefreshDeferrals == 0 && view._outputGroupRefreshPending)
                view.RefreshOutputGroups();
        }
    }

    private void BoundTasksChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_outputGroupRefreshDeferrals > 0) { _outputGroupRefreshPending = true; return; }
        if (_boundTasks is not null && _outputGroups.TryApplyIncrementalChange(args, _boundTasks.Count)) return;
        RefreshOutputGroups();
    }

    internal void RefreshOutputGroups()
    {
        if (_outputGroupRefreshDeferrals > 0) { _outputGroupRefreshPending = true; return; }
        _outputGroupRefreshPending = false;
        if (_boundTasks is null) return;
        var selected = SelectedTasks.Select(task => task.Id).ToHashSet();
        if (!_boundTasks.Any(task => task.Model.OutputGroupId.HasValue))
        {
            if (_usingGroupedSource || !ReferenceEquals(TaskList.ItemsSource, _boundTasks))
            {
                TaskList.ItemsSource = null;
                _usingGroupedSource = false;
                _outputGroups.ClearProjection();
                TaskList.ItemsSource = _boundTasks;
                RestoreSelectedTasks(selected);
            }
            return;
        }

        _outputGroups.Synchronize(_boundTasks);
        if (!_usingGroupedSource)
        {
            _groupedSource ??= new CollectionViewSource { IsSourceGrouped = true, Source = _outputGroups };
            _usingGroupedSource = true;
            TaskList.ItemsSource = _groupedSource.View;
        }
        RestoreSelectedTasks(selected);
    }

    private void RestoreSelectedTasks(HashSet<Guid> selected)
    {
        if (_boundTasks is null) return;
        foreach (var task in _boundTasks.Where(task => selected.Contains(task.Id)))
            if (!TaskList.SelectedItems.Contains(task)) TaskList.SelectedItems.Add(task);
    }

    private void ToggleOutputGroup_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        if (_outputGroups.FindOutputGroup(id) is not { } group) return;
        group.IsExpanded = !group.IsExpanded;
        foreach (var task in group)
            if (TaskList.ContainerFromItem(task) is ListViewItem item)
                item.Visibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }
    private void TaskList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null)
            args.ItemContainer.Visibility = args.Item is MediaTaskViewModel { OutputGroupExpanded: false }
                ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ApplyLocalization(AppSettings settings)
    {
        BrandTitleText.Text = LocalizationService.T("BrandTitle");
        QueueSummaryText.Text = LocalizationService.T("QueueHint");
        AddFilesText.Text = LocalizationService.T("AddFiles");
        ContextAddFilesMenuItem.Text = AddFilesText.Text;
        AddMoreText.Text = LocalizationService.T("ConversionUiLayoutMoreActions");
        QueueOperationsText.Text = LocalizationService.T("ConversionUiLayoutMoreActions");
        AutomationProperties.SetName(QueueOperationsButton,
            LocalizationService.T("QueueTitle") + " · " + QueueOperationsText.Text);
        AddFolderMenuItem.Text = LocalizationService.T("AddFolder");
        PasteFilesMenuItem.Text = LocalizationService.T("PasteFiles");
        ContextAddFolderMenuItem.Text = AddFolderMenuItem.Text;
        ContextPasteFilesMenuItem.Text = PasteFilesMenuItem.Text;
        AddOpticalDiscMenuItem.Text = LocalizationService.T("AddOpticalDisc");
        QueueTitleText.Text = LocalizationService.T("QueueTitle");
        PresetsSummaryText.Text = LocalizationService.T("PresetsSummary");
        HardwareFallbackText.Text = LocalizationService.T("HardwareFallback");
        OutputToText.Text = LocalizationService.T("OutputTo");
        PauseAllText.Text = LocalizationService.T("PauseAll");
        ClearPendingText.Text = LocalizationService.T("ClearPending");
        StartAllText.Text = LocalizationService.T("StartAll");
        OutputModeCombo.Items.Clear();
        OutputModeCombo.Items.Add(LocalizationService.T("OutputCustom"));
        OutputModeCombo.Items.Add(LocalizationService.T("OutputSource"));
        OutputModeCombo.SelectedIndex = settings.OutputMode == "source" ? 1 : 0;
        RefreshOutputLocationSummary();
        SelectEditableMenuItem.Text = LocalizationService.T("QueueBatchSelectIdle");
        ClearSelectionMenuItem.Text = LocalizationService.T("QueueBatchClearSelection");
        BatchOptionsMenuItem.Text = LocalizationService.T("QueueBatchOptions");
        BatchCustomSettingsMenuItem.Text = LocalizationService.T("MediaCustomSettings");
        BatchMultipleOutputsMenuItem.Text = LocalizationService.T("MultiOutputSettings");
        RetryFailedMenuItem.Text = LocalizationService.T("QueueBatchRetryFailed");
        SortPendingMenuItem.Text = LocalizationService.T("QueueBatchSort");
        SortNameMenuItem.Text = LocalizationService.T("QueueBatchSortName");
        SortSmallestMenuItem.Text = LocalizationService.T("QueueBatchSortSmallest");
        SortLargestMenuItem.Text = LocalizationService.T("QueueBatchSortLargest");
        OutputSettingsMenuItem.Text = LocalizationService.T("DesktopUpgradeOutputSettings");
        KeyboardShortcutsMenuItem.Text = LocalizationService.T("DesktopUpgradeShortcuts");
        ApplyQueueExperienceLocalization();
        UpdateSelectionSummary();
        ApplyResponsiveLocalization();
        foreach (var group in _outputGroups) group.RefreshLocalization();
    }

    internal void ApplyResponsiveLocalization()
    {
        AutomationProperties.SetName(OutputModeCombo, LocalizationService.T("OutputTo"));
        PresetsSummaryText.Visibility = Visibility.Collapsed;
        HardwareFallbackText.Visibility = Visibility.Collapsed;
        UpdateViewport();
    }

    private void TaskActions_Opening(object sender, object args)
    {
        if (sender is not MenuFlyout flyout || flyout.Target is not FrameworkElement owner) return;
        var task = (owner is ListViewItem row ? TaskList.ItemFromContainer(row) : owner.DataContext) as MediaTaskViewModel;
        if (task is null) return;
        task.RefreshQueueActions();
        foreach (var item in flyout.Items)
        {
            item.DataContext = task;
            switch (item.Name)
            {
                case "ContextStartTaskMenuItem":
                    item.IsEnabled = task.IsTargetAvailable && !task.Model.IsRecoveringOutput &&
                        task.State is not (ConversionState.Running or ConversionState.Paused or ConversionState.Probing);
                    break;
                case "ContextPauseTaskMenuItem":
                    item.IsEnabled = task.State is ConversionState.Running or ConversionState.Paused;
                    break;
            }
        }
    }

    private void ConverterView_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateViewport();
    private void ConverterLayout_LayoutUpdated(object sender, object args) => UpdateViewport();

    internal void UpdateViewport()
    {
        if (ConverterLayout is null || PageRoot.ActualHeight <= 0 || Visibility != Visibility.Visible) return;
        var chromeHeight = ConverterLayout.RowDefinitions.Where(row => row.Height.IsAuto).Sum(row => row.ActualHeight)
            + ConverterLayout.Padding.Top + ConverterLayout.Padding.Bottom
            + ConverterLayout.RowSpacing * (ConverterLayout.RowDefinitions.Count - 1);
        var height = Math.Max(PageRoot.ActualHeight, chromeHeight + 160);
        if (Math.Abs(ConverterLayout.Height - height) > 1) ConverterLayout.Height = height;
    }

    internal MediaTaskViewModel[] SelectedTasks
    {
        get
        {
            var selected = TaskList.SelectedItems.OfType<MediaTaskViewModel>().ToArray();
            // Native selection notifications may be queued after a programmatic edit.
            // Capture current input at command boundaries before consulting page state.
            _pageViewModel?.SetSelection(selected.Select(task => task.Id));
            return _pageViewModel?.SelectedTasks ?? selected;
        }
    }
    internal Guid[] SelectedTaskIds => SelectedTasks.Select(task => task.Id).ToArray();

    internal void SelectAllEditableTasks(Func<Guid, bool>? isScheduled = null)
    {
        ApplySelection(QueuePageViewModel.SelectEditableRows(_boundTasks ?? [], isScheduled ?? (_ => false)));
    }

    internal void ApplySelection(IEnumerable<MediaTaskViewModel> tasks)
    {
        var selected = tasks.ToArray();
        TaskList.SelectedItems.Clear();
        foreach (var task in selected)
            if (_boundTasks?.Contains(task) == true) TaskList.SelectedItems.Add(task);
    }

    private void SelectEditableTasks_Click(object sender, RoutedEventArgs args) =>
        SelectEditableRequested?.Invoke(sender, args);

    private void ClearSelection_Click(object sender, RoutedEventArgs args) => TaskList.SelectedItems.Clear();

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelectionSummary();

    private void UpdateSelectionSummary()
    {
        if (BatchSelectionText is null) return;
        _pageViewModel?.SetSelection(TaskList.SelectedItems.OfType<MediaTaskViewModel>().Select(task => task.Id));
        BatchSelectionText.Text = _pageViewModel?.SelectionSummary ?? QueuePageViewModel.FormatSelectionSummary(TaskList.SelectedItems.Count);
    }

    internal event RoutedEventHandler? SelectEditableRequested;
    internal event RoutedEventHandler? BatchOptions_ClickRequested;
    internal event RoutedEventHandler? CustomSettingsRequested;
    internal event RoutedEventHandler? MultipleOutputsRequested;
    internal event RoutedEventHandler? BatchCustomSettingsRequested;
    internal event RoutedEventHandler? BatchMultipleOutputsRequested;
    internal event RoutedEventHandler? SaveOutputGroupRequested;
    private void CustomSettings_Click(object sender, RoutedEventArgs args) => CustomSettingsRequested?.Invoke(sender, args);
    private void MultipleOutputs_Click(object sender, RoutedEventArgs args) => MultipleOutputsRequested?.Invoke(sender, args);
    private void BatchCustomSettings_Click(object sender, RoutedEventArgs args) => BatchCustomSettingsRequested?.Invoke(sender, args);
    private void BatchMultipleOutputs_Click(object sender, RoutedEventArgs args) => BatchMultipleOutputsRequested?.Invoke(sender, args);
    private void SaveOutputGroup_Click(object sender, RoutedEventArgs args) => SaveOutputGroupRequested?.Invoke(sender, args);
    private void BatchOptions_Click(object sender, RoutedEventArgs args) => BatchOptions_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? RetryFailed_ClickRequested;
    private void RetryFailed_Click(object sender, RoutedEventArgs args) => RetryFailed_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? SortPendingByName_ClickRequested;
    private void SortPendingByName_Click(object sender, RoutedEventArgs args) => SortPendingByName_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? SortPendingSmallest_ClickRequested;
    private void SortPendingSmallest_Click(object sender, RoutedEventArgs args) => SortPendingSmallest_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? SortPendingLargest_ClickRequested;
    private void SortPendingLargest_Click(object sender, RoutedEventArgs args) => SortPendingLargest_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? OutputSettings_ClickRequested;
    private void OutputSettings_Click(object sender, RoutedEventArgs args) => OutputSettings_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? KeyboardShortcuts_ClickRequested;
    private void KeyboardShortcuts_Click(object sender, RoutedEventArgs args) => KeyboardShortcuts_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? AddFilesButton_ClickRequested;
    private void AddFilesButton_Click(object sender, RoutedEventArgs args) => AddFilesButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? AddFolderButton_ClickRequested;
    private void AddFolderButton_Click(object sender, RoutedEventArgs args) => AddFolderButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? PasteFilesButton_ClickRequested;
    private void PasteFilesButton_Click(object sender, RoutedEventArgs args) => PasteFilesButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? AddOpticalDiscButton_ClickRequested;
    private void AddOpticalDiscButton_Click(object sender, RoutedEventArgs args) => AddOpticalDiscButton_ClickRequested?.Invoke(sender, args);

    internal event DragItemsStartingEventHandler? TaskList_DragItemsStartingRequested;
    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs args) => TaskList_DragItemsStartingRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? StartTaskButton_ClickRequested;
    internal event RoutedEventHandler? SaveTaskPresetButton_ClickRequested;
    private void SaveTaskPresetButton_Click(object sender, RoutedEventArgs args) => SaveTaskPresetButton_ClickRequested?.Invoke(sender, args);
    private void StartTaskButton_Click(object sender, RoutedEventArgs args) => StartTaskButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? PauseTaskButton_ClickRequested;
    private void PauseTaskButton_Click(object sender, RoutedEventArgs args) => PauseTaskButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? PreviewTaskButton_ClickRequested;
    private void PreviewTaskButton_Click(object sender, RoutedEventArgs args) => PreviewTaskButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? OpenOutputFileButton_ClickRequested;
    private void OpenOutputFileButton_Click(object sender, RoutedEventArgs args) => OpenOutputFileButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? CopyOutputButton_ClickRequested;
    private void CopyOutputButton_Click(object sender, RoutedEventArgs args) => CopyOutputButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? RecoverOutputButton_ClickRequested;
    private void RecoverOutputButton_Click(object sender, RoutedEventArgs args) => RecoverOutputButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? ImageColorButton_ClickRequested;
    private void ImageColorButton_Click(object sender, RoutedEventArgs args) => ImageColorButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? OpenOutputButton_ClickRequested;
    private void OpenOutputButton_Click(object sender, RoutedEventArgs args) => OpenOutputButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? ShareOutputButton_ClickRequested;
    private void ShareOutputButton_Click(object sender, RoutedEventArgs args) => ShareOutputButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? MediaInfoButton_ClickRequested;
    private void MediaInfoButton_Click(object sender, RoutedEventArgs args) => MediaInfoButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? CopyErrorButton_ClickRequested;
    private void CopyErrorButton_Click(object sender, RoutedEventArgs args) => CopyErrorButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? CancelOrRemoveButton_ClickRequested;
    private void CancelOrRemoveButton_Click(object sender, RoutedEventArgs args) => CancelOrRemoveButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? PdfPasswordBox_LoadedRequested;
    private void PdfPasswordBox_Loaded(object sender, RoutedEventArgs args) => PdfPasswordBox_LoadedRequested?.Invoke(sender, args);

    internal event Action<FrameworkElement, DataContextChangedEventArgs>? PdfPasswordBox_DataContextChangedRequested;
    private void PdfPasswordBox_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        PdfPasswordBox_DataContextChangedRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? PdfPasswordBox_PasswordChangedRequested;
    private void PdfPasswordBox_PasswordChanged(object sender, RoutedEventArgs args) => PdfPasswordBox_PasswordChangedRequested?.Invoke(sender, args);

    internal event SelectionChangedEventHandler? OutputModeCombo_SelectionChangedRequested;
    private void OutputModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        RefreshOutputLocationSummary();
        OutputModeCombo_SelectionChangedRequested?.Invoke(sender, args);
    }

    internal event RoutedEventHandler? ChooseOutputDirectoryButton_ClickRequested;
    private void ChooseOutputDirectoryButton_Click(object sender, RoutedEventArgs args)
    {
        OutputLocationButton.Flyout?.Hide();
        ChooseOutputDirectoryButton_ClickRequested?.Invoke(sender, args);
    }

    internal event RoutedEventHandler? PauseAllButton_ClickRequested;
    private void PauseAllButton_Click(object sender, RoutedEventArgs args) => PauseAllButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? ClearPendingButton_ClickRequested;
    private void ClearPendingButton_Click(object sender, RoutedEventArgs args)
    {
        QueueOperationsButton.Flyout?.Hide();
        ClearPendingButton_ClickRequested?.Invoke(sender, args);
    }

    internal event RoutedEventHandler? CancelAllButton_ClickRequested;
    private void CancelAllButton_Click(object sender, RoutedEventArgs args)
    {
        QueueOperationsButton.Flyout?.Hide();
        CancelAllButton_ClickRequested?.Invoke(sender, args);
    }

    internal event RoutedEventHandler? StartAllButton_ClickRequested;
    private void StartAllButton_Click(object sender, RoutedEventArgs args) => StartAllButton_ClickRequested?.Invoke(sender, args);
}
