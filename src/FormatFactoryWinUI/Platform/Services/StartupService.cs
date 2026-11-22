using Microsoft.Win32;
using Windows.ApplicationModel;

namespace FormatFactoryWinUI.Services;

internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FormatFactoryWinUI";
    public const string StartupTaskId = "FormatFactoryWinUIStartup";

    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Package.Current.Id;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task SetEnabledAsync(bool enabled, bool startInTray)
    {
        if (IsPackaged)
        {
            var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (enabled)
            {
                var state = await startupTask.RequestEnableAsync();
                if (state != Windows.ApplicationModel.StartupTaskState.Enabled)
                {
                    throw new InvalidOperationException(LocalizationService.T("GeneralServiceStartupTaskNotEnabled", state));
                }
            }
            else
            {
                startupTask.Disable();
            }

            return;
        }

        SetRegistryEnabled(enabled, startInTray);
    }

    private static void SetRegistryEnabled(bool enabled, bool startInTray)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException(LocalizationService.T("GeneralServiceStartupRegistryUnavailable"));
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(LocalizationService.T("GeneralServiceAppPathUnavailable"));
        }

        var launchMode = startInTray ? " --startup-tray" : " --startup";
        key.SetValue(ValueName, $"\"{executablePath}\"{launchMode}", RegistryValueKind.String);
    }
}
