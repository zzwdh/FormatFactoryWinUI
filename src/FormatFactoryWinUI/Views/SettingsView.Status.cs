using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.ConversionEngines;

namespace FormatFactoryWinUI.Views;

public sealed partial class SettingsView
{
    internal void RenderDocumentEngineStatus(IEnumerable<ExternalToolInfo> tools,
        bool autoDetect = false, string? libreOfficePath = null, string? calibrePath = null, string? cajPath = null,
        bool preserveConfiguredPaths = false)
    {
        if (!ViewModel.IsInitialized) return;
        foreach (var tool in tools)
        {
            var status = tool.IsAvailable
                ? string.IsNullOrWhiteSpace(tool.Version)
                    ? LocalizationService.T("EngineAvailable")
                    : LocalizationService.T("EngineAvailableVersion", tool.Version)
                : tool.StatusMessage;
            switch (tool.Kind)
            {
                case ExternalToolKind.MicrosoftWord:
                    WordEngineStatusText.Text = status;
                    break;
                case ExternalToolKind.LibreOffice:
                    if (autoDetect && tool.IsAvailable && LibreOfficePathTextBox.Text == libreOfficePath
                        && (!preserveConfiguredPaths || string.IsNullOrWhiteSpace(libreOfficePath)))
                    {
                        LibreOfficePathTextBox.Text = PersistedEnginePath(tool.ExecutablePath);
                    }
                    LibreOfficeEngineStatusText.Text = status;
                    break;
                case ExternalToolKind.Calibre:
                    if (autoDetect && tool.IsAvailable && CalibrePathTextBox.Text == calibrePath
                        && (!preserveConfiguredPaths || string.IsNullOrWhiteSpace(calibrePath)))
                    {
                        CalibrePathTextBox.Text = PersistedEnginePath(tool.ExecutablePath);
                    }
                    CalibreEngineStatusText.Text = status;
                    break;
                case ExternalToolKind.Caj2Pdf:
                    if (autoDetect && tool.IsAvailable && Caj2PdfPathTextBox.Text == cajPath
                        && (!preserveConfiguredPaths || string.IsNullOrWhiteSpace(cajPath)))
                    {
                        Caj2PdfPathTextBox.Text = PersistedEnginePath(tool.ExecutablePath);
                    }
                    CajEngineStatusText.Text = status;
                    break;
            }
        }
    }

    internal void RenderOpticalEngineStatus(IEnumerable<OpticalDiscToolInfo> tools,
        bool autoDetect = false, string? makeMkvPath = null, string? audioCdPath = null, string? readomPath = null,
        bool preserveConfiguredPaths = false)
    {
        if (!ViewModel.IsInitialized) return;
        foreach (var tool in tools)
        {
            var status = tool.IsAvailable
                ? string.IsNullOrWhiteSpace(tool.Version)
                    ? LocalizationService.T("EngineAvailable")
                    : LocalizationService.T("EngineAvailableVersion", tool.Version)
                : tool.StatusMessage;
            switch (tool.Kind)
            {
                case OpticalDiscToolKind.MakeMkv:
                    if (autoDetect && tool.IsAvailable && MakeMkvPathTextBox.Text == makeMkvPath
                        && (!preserveConfiguredPaths || string.IsNullOrWhiteSpace(makeMkvPath)))
                    {
                        MakeMkvPathTextBox.Text = PersistedEnginePath(tool.ExecutablePath);
                    }
                    MakeMkvEngineStatusText.Text = status;
                    break;
                case OpticalDiscToolKind.AudioCdRipper:
                    if (autoDetect && tool.IsAvailable && AudioCdRipperPathTextBox.Text == audioCdPath
                        && (!preserveConfiguredPaths || string.IsNullOrWhiteSpace(audioCdPath)))
                    {
                        AudioCdRipperPathTextBox.Text = PersistedEnginePath(tool.ExecutablePath);
                    }
                    AudioCdRipperStatusText.Text = status;
                    break;
                case OpticalDiscToolKind.Readom:
                    if (autoDetect && tool.IsAvailable && ReadomPathTextBox.Text == readomPath
                        && (!preserveConfiguredPaths || string.IsNullOrWhiteSpace(readomPath)))
                    {
                        ReadomPathTextBox.Text = PersistedEnginePath(tool.ExecutablePath);
                    }
                    ReadomEngineStatusText.Text = status;
                    break;
            }
        }
    }

    private static string PersistedEnginePath(string? path) =>
        BundledEngineLocator.IsBundledExecutable(path) ? string.Empty : path ?? string.Empty;
}
