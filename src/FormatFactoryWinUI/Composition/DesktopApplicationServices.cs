using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Composition;

/// <summary>Desktop composition root. Construct window-scoped services only for the primary instance.</summary>
internal sealed class DesktopApplicationServices
{
    private DesktopApplicationServices()
    {
        Settings = new SettingsService();
    }

    public SettingsService Settings { get; }
    public ReviewReminderService ReviewReminders { get; } = new();

    public static DesktopApplicationServices Create()
    {
        WindowsAppDataStorage.Initialize();
        return new DesktopApplicationServices();
    }

    public MainWindow CreateMainWindow(bool startInTray, Task<bool>? reviewReminderRegistration = null,
        StartupReadinessMetrics? startupMetrics = null) =>
        new(new MainWindowServices(Settings, reviewReminders: ReviewReminders, enableRemoteTelemetry: true),
            startInTray, reviewReminderRegistration, startupMetrics);
}
