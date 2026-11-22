using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI.Views;

public sealed partial class QueueView
{
    private bool _compactCommandsReady;
    private long? _outputPathCallbackToken;

    private void InitializeCompactCommands()
    {
        _compactCommandsReady = true;
        Loaded += CompactCommandsLoaded;
        Unloaded += CompactCommandsUnloaded;
        RegisterOutputPathCallback();
        RefreshOutputLocationSummary();
    }

    private void CompactCommandsLoaded(object sender, RoutedEventArgs args)
    {
        RegisterOutputPathCallback();
        RefreshOutputLocationSummary();
    }

    private void CompactCommandsUnloaded(object sender, RoutedEventArgs args)
    {
        if (_outputPathCallbackToken is not { } token) return;
        OutputDirectoryText.UnregisterPropertyChangedCallback(TextBlock.TextProperty, token);
        _outputPathCallbackToken = null;
    }

    private void RegisterOutputPathCallback()
    {
        if (_outputPathCallbackToken.HasValue) return;
        _outputPathCallbackToken = OutputDirectoryText.RegisterPropertyChangedCallback(TextBlock.TextProperty,
            (_, _) => RefreshOutputLocationSummary());
    }

    private void RefreshOutputLocationSummary()
    {
        if (!_compactCommandsReady) return;
        var sourceFolder = OutputModeCombo.SelectedIndex == 1;
        var path = OutputDirectoryText.Text ?? string.Empty;
        var folder = sourceFolder ? LocalizationService.T("OutputSource") : FolderDisplayName(path);
        var prefix = LocalizationService.T("OutputTo");
        OutputFolderNameText.Text = prefix + " · " + folder;
        var description = prefix + " · " + (sourceFolder ? folder : path);
        ToolTipService.SetToolTip(OutputLocationButton, description);
        ToolTipService.SetToolTip(OutputDirectoryButton, path);
        AutomationProperties.SetName(OutputLocationButton, description);
        AutomationProperties.SetHelpText(OutputDirectoryButton, path);
    }

    private static string FolderDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return LocalizationService.T("OutputCustom");
        try
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
