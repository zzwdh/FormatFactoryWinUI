using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Composition;

/// <summary>Dependencies for a primary window. The window orders subscription, shutdown and disposal.</summary>
internal sealed class MainWindowServices
{
    public MainWindowServices(SettingsService settings, IQueuePersistence? queuePersistence = null,
        IImportStorageAccess? importStorageAccess = null, ReviewReminderService? reviewReminders = null,
        bool enableRemoteTelemetry = false, Func<FeedbackDeliveryService>? feedbackDeliveryFactory = null,
        WindowShellServices? shell = null)
    {
        Settings = settings;
        Conversions = new WindowConversionServices(queuePersistence, importStorageAccess);
        Shell = shell ?? new WindowShellServices();
        ReviewReminders = reviewReminders ?? new ReviewReminderService();
        EnableRemoteTelemetry = enableRemoteTelemetry;
        CreateFeedbackDelivery = feedbackDeliveryFactory ?? (() => new FeedbackDeliveryService());
    }

    public SettingsService Settings { get; }
    public WindowConversionServices Conversions { get; }
    public WindowShellServices Shell { get; }
    public ReviewReminderService ReviewReminders { get; }
    public bool EnableRemoteTelemetry { get; }
    public Func<FeedbackDeliveryService> CreateFeedbackDelivery { get; }
    public AutomationStore AutomationStore { get; } = new();
    public AutomationService CreateAutomationService(
        Func<AutomationRule, string, CancellationToken, Task<AutomationExecutionResult>> execute) =>
        new(AutomationStore, execute);
    public HardwareAccelerationTestService HardwareAccelerationTest { get; } = new();
    public WindowsFilterTextExtractor FilterTextExtractor { get; } = new();
    public OpticalDeviceWatcher OpticalDeviceWatcher { get; } = new();
    public SystemPowerStateService PowerState { get; } = new();
    public WindowsAccessibilityService Accessibility { get; } = new();
}
