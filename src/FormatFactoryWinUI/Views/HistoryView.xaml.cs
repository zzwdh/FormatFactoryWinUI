using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FormatFactoryWinUI.Views;

using FormatFactoryWinUI.Motion;

public sealed partial class HistoryView : UserControl, IDisposable
{
    public HistoryPageViewModel ViewModel { get; } = new();
    private Func<string?, CancellationToken, Task<IReadOnlyList<ConversionHistoryItem>>>? _search;
    private Func<Func<Task>, Task>? _dispatch;
    private Func<bool>? _canAnimate;
    private bool _disposed;
    public HistoryView() => InitializeComponent();

    internal event EventHandler<Exception>? SearchFailed;
    internal string SearchText => HistorySearchBox.Text;

    internal void Initialize(Func<string?, CancellationToken, Task<IReadOnlyList<ConversionHistoryItem>>> search,
        Func<Func<Task>, Task> dispatch, Func<bool> canAnimate)
    {
        _search = search;
        _dispatch = dispatch;
        _canAnimate = canAnimate;
    }

    internal void ApplyLocalization()
    {
        HistoryTitleText.Text = LocalizationService.T("HistoryTitle");
        HistorySearchBox.PlaceholderText = LocalizationService.T("HistorySearchPlaceholder");
        HistoryEmptyText.Text = LocalizationService.T("HistoryEmpty");
        AutomationProperties.SetName(HistorySearchBox, LocalizationService.T("HistorySearchPlaceholder"));
        AutomationProperties.SetName(HistoryList, LocalizationService.T("HistoryTitle"));
        RefreshHistoryItemLabels(HistoryList);
    }

    private async void HistorySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            await RefreshAsync(sender.Text, debounce: true);
    }

    internal async Task RefreshAsync(string? query = null, bool debounce = false)
    {
        if (_disposed || _search is null || _dispatch is null) return;
        try
        {
            await ViewModel.RefreshAsync(query, debounce, _search, _dispatch);
            if (!_disposed) UpdateContentVisibility();
        }
        catch (Exception exception)
        {
            if (!_disposed) SearchFailed?.Invoke(this, exception);
        }
    }

    internal void ShowInfo(string message, InfoBarSeverity severity)
    {
        if (_disposed) return;
        HistoryInfoBar.Message = message;
        HistoryInfoBar.Severity = severity;
        HistoryInfoBar.IsOpen = true;
    }

    internal void UpdateContentVisibility()
    {
        var hasItems = ViewModel.Items.Count > 0;
        var changed = HistoryList.Visibility != (hasItems ? Visibility.Visible : Visibility.Collapsed);
        HistoryList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        if (changed && _canAnimate?.Invoke() == true && Visibility == Visibility.Visible)
            FluentMotion.Entrance(hasItems ? HistoryList : HistoryEmptyText, 167, 6);
    }

    public void Dispose()
    {
        _disposed = true;
        ViewModel.Dispose();
        _search = null;
        _dispatch = null;
        _canAnimate = null;
    }

    private void HistoryCard_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border card) ApplyHistoryCardLabels(card, card.DataContext as ConversionHistoryItem);
    }

    private void HistoryCard_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // Virtualized cards can be reused without another Loaded event. The new
        // context is authoritative because the Tag binding may update later.
        if (sender is Border card) ApplyHistoryCardLabels(card, args.NewValue as ConversionHistoryItem);
    }

    private static void ApplyHistoryCardLabels(Border card, ConversionHistoryItem? item)
    {
        if (card.FindName("HistoryResultSummaryText") is TextBlock resultSummary)
        {
            resultSummary.Text = item is null ? string.Empty : HistoryPageViewModel.DescribeResult(item);
            resultSummary.Visibility = string.IsNullOrEmpty(resultSummary.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (card.FindName("HistoryDetailsText") is TextBlock details)
            details.Text = item is null ? string.Empty
                : HistoryPageViewModel.DescribeParameters(item) + " · " + HistoryPageViewModel.DescribeCompletedAt(item);
        foreach (var (name, key) in new[]
        {
            ("RepeatHistoryButton", "HistoryRepeatTask"),
            ("OpenHistoryFileButton", "DesktopOpenDefault"),
            ("OpenHistoryLocationButton", "OpenOutput")
        })
        {
            if (card.FindName(name) is not Button button) continue;
            var label = LocalizationService.T(key);
            if (name == "RepeatHistoryButton") button.Content = label;
            ToolTipService.SetToolTip(button, label);
            AutomationProperties.SetName(button, item is null ? label : label + " · " + item.DisplayName);
        }
    }

    private static void RefreshHistoryItemLabels(DependencyObject parent)
    {
        if (parent is Border { DataContext: ConversionHistoryItem item } card) ApplyHistoryCardLabels(card, item);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            RefreshHistoryItemLabels(VisualTreeHelper.GetChild(parent, index));
    }

    private void HistoryCardActions_Opening(object sender, object args)
    {
        if (sender is not MenuFlyout { Target: Border card } menu) return;
        menu.Items.Clear();
        AddAction("OpenHistoryFileButton", "\uE8E5", OpenHistoryFileButton_Click);
        AddAction("OpenHistoryLocationButton", "\uE838", OpenHistoryOutputButton_Click);
        AddAction("RepeatHistoryButton", "\uE72C", RepeatHistoryTaskButton_Click);

        void AddAction(string name, string glyph, RoutedEventHandler handler)
        {
            if (card.FindName(name) is not Button button || ToolTipService.GetToolTip(button) is not string label) return;
            var action = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph }, IsEnabled = button.IsEnabled };
            action.Click += (_, clickArgs) => handler(button, clickArgs);
            menu.Items.Add(action);
        }
    }

    internal event RoutedEventHandler? RepeatHistoryTaskButton_ClickRequested;
    private void RepeatHistoryTaskButton_Click(object sender, RoutedEventArgs args) => RepeatHistoryTaskButton_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? OpenHistoryFileButton_ClickRequested;
    private void OpenHistoryFileButton_Click(object sender, RoutedEventArgs args) => OpenHistoryFileButton_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? OpenHistoryOutputButton_ClickRequested;
    private void OpenHistoryOutputButton_Click(object sender, RoutedEventArgs args) => OpenHistoryOutputButton_ClickRequested?.Invoke(sender, args);
}
