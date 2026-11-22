using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace FormatFactoryWinUI.Views;

public sealed partial class SettingsView
{
    private string _updateStatusKey = "StoreUpdateIdle";
    private bool _updateBusy;
    private bool _storeUpdatesAvailable;
    private bool _applyingUpdatePreferences;

    internal void ApplyUpdatePreferences(AppSettings settings)
    {
        _applyingUpdatePreferences = true;
        try
        {
            AutomaticUpdatesToggle.IsOn = settings.EnableAutomaticUpdates;
        }
        finally
        {
            _applyingUpdatePreferences = false;
        }
    }

    private void ApplyUpdateLocalization()
    {
        UpdatesTitleText.Text = LocalizationService.T("StoreUpdatesTitle");
        UpdatesDescriptionText.Text = LocalizationService.T("StoreUpdatesDescription");
        AutomaticUpdatesToggle.Header = LocalizationService.T("StoreUpdatesAutomaticTitle");
        AutomaticUpdatesToggle.OnContent = LocalizationService.T("HardwareOn");
        AutomaticUpdatesToggle.OffContent = LocalizationService.T("HardwareOff");
        UpdatesPreferenceHelpText.Text = LocalizationService.T("StoreUpdatesPreferenceHelp");
        CheckForUpdatesButton.Content = LocalizationService.T("StoreUpdatesCheckButton");
        OpenStoreUpdatesButton.Content = LocalizationService.T("StoreUpdatesOpenStoreButton");
        AutomationProperties.SetName(UpdatesCard, UpdatesTitleText.Text);
        AutomationProperties.SetName(AutomaticUpdatesToggle, LocalizationService.T("StoreUpdatesAutomaticTitle"));
        AutomationProperties.SetHelpText(AutomaticUpdatesToggle, UpdatesPreferenceHelpText.Text);
        AutomationProperties.SetName(CheckForUpdatesButton, LocalizationService.T("StoreUpdatesCheckButton"));
        AutomationProperties.SetName(OpenStoreUpdatesButton, LocalizationService.T("StoreUpdatesOpenStoreButton"));
        SetUpdateState(_updateStatusKey, _updateBusy, _storeUpdatesAvailable);
    }

    internal void SetUpdateState(string statusKey, bool busy, bool storeAvailable)
    {
        _updateStatusKey = statusKey;
        _updateBusy = busy;
        _storeUpdatesAvailable = storeAvailable;
        UpdateStatusText.Text = LocalizationService.T(statusKey);
        AutomaticUpdatesToggle.IsEnabled = storeAvailable;
        CheckForUpdatesButton.IsEnabled = storeAvailable && !busy;
    }

    internal event RoutedEventHandler? AutomaticUpdatesToggle_ToggledRequested;
    private void AutomaticUpdatesToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_applyingUpdatePreferences)
            AutomaticUpdatesToggle_ToggledRequested?.Invoke(sender, args);
    }

    internal event RoutedEventHandler? CheckForUpdatesButton_ClickRequested;
    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs args) =>
        CheckForUpdatesButton_ClickRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? OpenStoreUpdatesButton_ClickRequested;
    private void OpenStoreUpdatesButton_Click(object sender, RoutedEventArgs args) =>
        OpenStoreUpdatesButton_ClickRequested?.Invoke(sender, args);
}
