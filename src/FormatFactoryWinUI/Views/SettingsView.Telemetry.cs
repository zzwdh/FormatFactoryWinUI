using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace FormatFactoryWinUI.Views;

public sealed partial class SettingsView
{
    internal void ApplyTelemetryPreferences(AppSettings settings)
    {
        UsageTelemetryToggle.IsOn = settings.EnableUsageTelemetry;
        AutomaticErrorReportingToggle.IsOn = settings.EnableAutomaticErrorReporting;
    }

    private void ApplyTelemetryLocalization()
    {
        TelemetryTitleText.Text = LocalizationService.T("TelemetryTitle");
        TelemetryDescriptionText.Text = LocalizationService.T("TelemetryDescription");
        UsageTelemetryToggle.Header = LocalizationService.T("TelemetryUsageTitle");
        UsageTelemetryDescriptionText.Text = LocalizationService.T("TelemetryUsageDescription");
        AutomaticErrorReportingToggle.Header = LocalizationService.T("TelemetryErrorsTitle");
        AutomaticErrorReportingDescriptionText.Text = LocalizationService.T("TelemetryErrorsDescription");
        AutomationProperties.SetName(TelemetryCard, TelemetryTitleText.Text);
        AutomationProperties.SetName(UsageTelemetryToggle, LocalizationService.T("TelemetryUsageTitle"));
        AutomationProperties.SetHelpText(UsageTelemetryToggle, UsageTelemetryDescriptionText.Text);
        AutomationProperties.SetName(AutomaticErrorReportingToggle, LocalizationService.T("TelemetryErrorsTitle"));
        AutomationProperties.SetHelpText(AutomaticErrorReportingToggle, AutomaticErrorReportingDescriptionText.Text);
    }

    internal event RoutedEventHandler? UsageTelemetryToggle_ToggledRequested;
    private void UsageTelemetryToggle_Toggled(object sender, RoutedEventArgs args) =>
        UsageTelemetryToggle_ToggledRequested?.Invoke(sender, args);

    internal event RoutedEventHandler? AutomaticErrorReportingToggle_ToggledRequested;
    private void AutomaticErrorReportingToggle_Toggled(object sender, RoutedEventArgs args) =>
        AutomaticErrorReportingToggle_ToggledRequested?.Invoke(sender, args);
}
