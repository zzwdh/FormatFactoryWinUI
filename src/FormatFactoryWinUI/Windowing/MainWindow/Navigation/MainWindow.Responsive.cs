namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private void ApplyResponsiveLocalization()
    {
        ConverterView.ApplyResponsiveLocalization();
        if (SettingsView.ViewModel.IsInitialized) SettingsView.ApplyResponsiveLocalization();
    }
}
