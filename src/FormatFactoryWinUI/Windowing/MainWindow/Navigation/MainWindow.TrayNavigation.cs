using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private async void TrayIcon_NavigationRequested(object? sender, string target)
    {
        try { await NavigateFromTrayAsync(target); }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
    }

    private Task NavigateFromTrayAsync(string target)
    {
        if (_windowClosed || _closeSaveInProgress) return Task.CompletedTask;
        return DispatchActivationAsync(new ActivationRequest([], NavigationTarget: target));
    }
}
