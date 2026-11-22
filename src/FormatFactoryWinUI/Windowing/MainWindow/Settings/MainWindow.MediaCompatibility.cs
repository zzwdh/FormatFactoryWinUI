using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private async void BrowseCompatibilityFfmpegButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = await PickExternalToolAsync(new[] { ".exe" });
            if (string.IsNullOrWhiteSpace(path)) return;
            SettingsView.CompatibilityFfmpegPathTextBox.Text = path;
            var valid = await _ffmpegLocator.ValidatePairAsync(path);
            ShowSettingsInfo(LocalizationService.T(valid ? "FfmpegAvailable" : "MediaCompatibilityEngineUnavailable", path),
                valid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowSettingsInfo(exception.Message, InfoBarSeverity.Error);
        }
    }
}
