using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;

namespace FormatFactoryWinUI.Views;

public sealed partial class SettingsView
{
    internal bool HasPendingFeedbackError { get; private set; }
    private bool _feedbackBusy;

    private void ApplyFeedbackLocalization()
    {
        FeedbackTitleText.Text = LocalizationService.T("FeedbackTitle");
        FeedbackDescriptionText.Text = LocalizationService.T("FeedbackDescription");
        SendFeedbackButton.Content = LocalizationService.T("FeedbackSendButton");
        ReportErrorButton.Content = LocalizationService.T("FeedbackReportButton");
        ExportErrorReportButton.Content = LocalizationService.T("FeedbackExportButton");
        SetFeedbackState(HasPendingFeedbackError, _feedbackBusy);
    }

    internal void SetFeedbackState(bool hasPendingError, bool busy = false)
    {
        HasPendingFeedbackError = hasPendingError;
        _feedbackBusy = busy;
        FeedbackStatusText.Text = LocalizationService.T(hasPendingError ? "FeedbackPendingError" : "FeedbackNoError");
        SendFeedbackButton.IsEnabled = ReportErrorButton.IsEnabled = ExportErrorReportButton.IsEnabled = !busy;
    }

    internal event RoutedEventHandler? SendFeedbackButton_ClickRequested;
    private void SendFeedbackButton_Click(object sender, RoutedEventArgs args) => SendFeedbackButton_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? ReportErrorButton_ClickRequested;
    private void ReportErrorButton_Click(object sender, RoutedEventArgs args) => ReportErrorButton_ClickRequested?.Invoke(sender, args);
    internal event RoutedEventHandler? ExportErrorReportButton_ClickRequested;
    private void ExportErrorReportButton_Click(object sender, RoutedEventArgs args) => ExportErrorReportButton_ClickRequested?.Invoke(sender, args);
}
