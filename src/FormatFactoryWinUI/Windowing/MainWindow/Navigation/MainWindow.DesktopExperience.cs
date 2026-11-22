using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly List<KeyboardAccelerator> _desktopAccelerators = [];
    private bool _desktopCommandInProgress;

    private void InitializeDesktopExperience()
    {
        ConverterView.KeyboardShortcuts_ClickRequested += KeyboardShortcuts_Click;
        AddDesktopAccelerator(VirtualKey.O, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        AddDesktopAccelerator(VirtualKey.V, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.A, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.Enter, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.F, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Control);
        AddDesktopAccelerator(VirtualKey.F5, VirtualKeyModifiers.None);
        AddDesktopAccelerator(VirtualKey.F1, VirtualKeyModifiers.None);
    }

    private void AddDesktopAccelerator(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += DesktopAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(accelerator);
        _desktopAccelerators.Add(accelerator);
    }

    private bool DesktopShortcutsAvailable => !_windowClosed && !_initializing && !_closeSaveInProgress
        && !_settingsResetInProgress && !_queueSession.IsStopping
        && !_desktopCommandInProgress && !_dropImportInProgress && _activeImport is null
        && !_feedbackInProgress && _reviewBusinessDialogs == 0 && _reviewReminderDialog is null
        && RootGrid.XamlRoot is not null
        && !VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(popup => popup.IsOpen);

    private bool IsTextInputFocused()
    {
        if (RootGrid.XamlRoot is null) return false;
        var element = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is TextBox or PasswordBox or AutoSuggestBox or NumberBox or RichEditBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void DesktopAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!DesktopShortcutsAvailable) return;
        var queueVisible = ConverterView.Visibility == Visibility.Visible;
        if (sender.Key is VirtualKey.A or VirtualKey.V && (!queueVisible || IsTextInputFocused())) return;
        if (sender.Key == VirtualKey.Enter && !queueVisible) return;
        if (sender.Key == VirtualKey.F5 && HistoryView.Visibility != Visibility.Visible) return;
        args.Handled = true;
        _desktopCommandInProgress = true;
        try
        {
            switch (sender.Key)
            {
                case VirtualKey.O:
                    NavView.SelectedItem = NavAllItem;
                    if (sender.Modifiers.HasFlag(VirtualKeyModifiers.Shift)) await PickAndAddFolderAsync();
                    else await PickAndAddFilesAsync();
                    break;
                case VirtualKey.V:
                    await PasteFilesAsync();
                    break;
                case VirtualKey.A:
                    SelectEditableTasks_Click(this, new RoutedEventArgs());
                    break;
                case VirtualKey.Enter:
                    StartAllButton_Click(this, new RoutedEventArgs());
                    break;
                case VirtualKey.F:
                    NavView.SelectedItem = NavHistoryItem;
                    await _navigationTransition;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_windowClosed && HistoryView.Visibility == Visibility.Visible)
                            HistoryView.HistorySearchBox.Focus(FocusState.Programmatic);
                    });
                    break;
                case VirtualKey.Number1: NavView.SelectedItem = NavAllItem; break;
                case VirtualKey.Number2: NavView.SelectedItem = NavHistoryItem; break;
                case VirtualKey.Number3: NavView.SelectedItem = NavSettingsItem; break;
                case VirtualKey.F5: await RefreshHistoryAsync(HistoryView.SearchText); break;
                case VirtualKey.F1: await ShowContentDialogAsync(CreateKeyboardShortcutsDialog()); break;
            }
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
        finally { _desktopCommandInProgress = false; }
    }

    private ContentDialog CreateKeyboardShortcutsDialog() => new()
    {
        XamlRoot = RootGrid.XamlRoot,
        RequestedTheme = RootGrid.ActualTheme,
        FlowDirection = RootGrid.FlowDirection,
        Title = LocalizationService.T("DesktopUpgradeShortcuts"),
        Content = new TextBlock
        {
            Text = string.Join(Environment.NewLine,
                "Ctrl+O  ·  " + LocalizationService.T("AddFiles"),
                "Ctrl+Shift+O  ·  " + LocalizationService.T("AddFolder"),
                "Ctrl+V  ·  " + LocalizationService.T("PasteFiles"),
                "Ctrl+A  ·  " + LocalizationService.T("DesktopUpgradeSelectAll"),
                "Ctrl+Z  ·  " + LocalizationService.T("QueueUndoAction"),
                "Ctrl+Enter  ·  " + LocalizationService.T("StartAll"),
                "Ctrl+F  ·  " + LocalizationService.T("HistorySearchPlaceholder"),
                "Ctrl+1  ·  " + LocalizationService.T("NavAll"),
                "Ctrl+2  ·  " + LocalizationService.T("NavHistory"),
                "Ctrl+3  ·  " + LocalizationService.T("NavSettings"),
                "F5  ·  " + LocalizationService.T("DesktopUpgradeRefreshHistory")),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        },
        CloseButtonText = LocalizationService.T("MediaInfoClose")
    };

    private async void KeyboardShortcuts_Click(object sender, RoutedEventArgs args)
    {
        if (_windowClosed || _initializing || _closeSaveInProgress || _settingsResetInProgress
            || _queueSession.IsStopping || _desktopCommandInProgress || _reviewBusinessDialogs > 0) return;
        _desktopCommandInProgress = true;
        try { await ShowContentDialogAsync(CreateKeyboardShortcutsDialog()); }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
        finally { _desktopCommandInProgress = false; }
    }

    private void LocalizeDesktopExperience()
    {
        AutomationProperties.SetName(ConverterView.AddFilesButton, LocalizationService.T("AddFiles"));
        AutomationProperties.SetAcceleratorKey(ConverterView.AddFilesButton, "Ctrl+O");
        ToolTipService.SetToolTip(ConverterView.AddFilesButton, LocalizationService.T("AddFiles") + " (Ctrl+O)");
        AutomationProperties.SetAcceleratorKey(ConverterView.AddFolderMenuItem, "Ctrl+Shift+O");
        AutomationProperties.SetAcceleratorKey(ConverterView.PasteFilesMenuItem, "Ctrl+V");
        AutomationProperties.SetName(ConverterView.TaskList, LocalizationService.T("QueueTitle"));
        AutomationProperties.SetName(HistoryView.HistoryList, LocalizationService.T("HistoryTitle"));
        AutomationProperties.SetName(HistoryView.HistorySearchBox, LocalizationService.T("HistorySearchPlaceholder"));
        AutomationProperties.SetName(ConverterView.OutputDirectoryButton, LocalizationService.T("OutputTo"));
        AutomationProperties.SetHelpText(ConverterView.TaskList, LocalizationService.T("DesktopUpgradeSelectionHelp"));
        AutomationProperties.SetAcceleratorKey(HistoryView.HistorySearchBox, "Ctrl+F");
        AutomationProperties.SetAcceleratorKey(NavAllItem, "Ctrl+1");
        AutomationProperties.SetAcceleratorKey(NavHistoryItem, "Ctrl+2");
        AutomationProperties.SetAcceleratorKey(NavSettingsItem, "Ctrl+3");
        AutomationProperties.SetLiveSetting(ConverterView.AppInfoBar, AutomationLiveSetting.Polite);
        ToolTipService.SetToolTip(NavAllItem, LocalizationService.T("NavAll") + " (Ctrl+1)");
        ToolTipService.SetToolTip(NavHistoryItem, LocalizationService.T("NavHistory") + " (Ctrl+2)");
        ToolTipService.SetToolTip(NavSettingsItem, LocalizationService.T("NavSettings") + " (Ctrl+3)");
        AutomationProperties.SetHelpText(RootGrid, LocalizationService.T("DesktopUpgradeShortcutHint"));
    }

    private void DisposeDesktopExperience()
    {
        ConverterView.KeyboardShortcuts_ClickRequested -= KeyboardShortcuts_Click;
        foreach (var accelerator in _desktopAccelerators)
        {
            accelerator.Invoked -= DesktopAccelerator_Invoked;
            RootGrid.KeyboardAccelerators.Remove(accelerator);
        }
        _desktopAccelerators.Clear();
    }
}
