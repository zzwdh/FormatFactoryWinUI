using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task RunDesktopUpgradeChecksAsync()
    {
        Check("Desktop commands are registered once and unavailable during initialization", () =>
        {
            var root = (Grid)Field("RootGrid")!;
            Require(root.KeyboardAccelerators.Count == 12, "The expected desktop commands were not registered.");
            Require(root.KeyboardAccelerators.Select(item => (item.Key, item.Modifiers)).Distinct().Count() == 12,
                "Duplicate desktop commands were registered.");
            Invoke("InitializeViews");
            Require(root.KeyboardAccelerators.Count == 12, "Reinitializing views duplicated keyboard commands.");
            Require((bool)typeof(MainWindow).GetProperty("DesktopShortcutsAvailable", InstanceMembers)!.GetValue(_window)! == false,
                "The uninitialized or unloaded window accepted keyboard commands.");
        });
        Check("Batch, output and history actions reach production window handlers", () =>
        {
            var queue = (Views.QueueView)Field("ConverterView")!;
            var history = (Views.HistoryView)Field("HistoryView")!;
            Require(queue.TaskList.SelectionMode == ListViewSelectionMode.Extended, "Queue multi-selection is unavailable.");
            RequireUpgradeEvent(queue, "OutputSettings_ClickRequested", "OutputSettings_Click");
            RequireUpgradeEvent(queue, "KeyboardShortcuts_ClickRequested", "KeyboardShortcuts_Click");
            RequireUpgradeEvent(queue, "BatchOptions_ClickRequested", "BatchOptions_Click");
            RequireUpgradeEvent(queue, "RetryFailed_ClickRequested", "RetryFailedTasks_Click");
            RequireUpgradeEvent(history, "RepeatHistoryTaskButton_ClickRequested", "RepeatHistoryTaskButton_Click");
            RequireUpgradeEvent(history, "OpenHistoryFileButton_ClickRequested", "OpenHistoryFileButton_Click");
        });
        await CheckAsync("Invalid output names cannot be saved and editing leaves current settings unchanged", async () =>
        {
            var settings = (AppSettings)Field("_settings")!;
            var original = settings.OutputFileNameTemplate;
            var dialog = (ContentDialog)Invoke("CreateOutputSettingsDialog")!;
            var content = (StackPanel)dialog.Content;
            var input = content.Children.OfType<TextBox>().Single();
            input.Text = "../outside";
            await Task.Delay(50);
            Require(!dialog.IsPrimaryButtonEnabled, "A path escaping the output folder could be saved.");
            Require(content.Children.OfType<InfoBar>().Single().IsOpen, "An invalid name lacked an explanation.");
            input.Text = "{name}-{format}-{date}";
            await Task.Delay(50);
            Require(dialog.IsPrimaryButtonEnabled, "A valid output name was rejected.");
            Require(settings.OutputFileNameTemplate == original, "Editing the dialog changed persisted settings before save.");
        });
        Check("Desktop accessibility and dialogs follow language and reading direction", () =>
        {
            try
            {
                foreach (var language in new[] { "zh-Hans", "en-US", "ar-SA", "he-IL", "fa-IR", "ur-PK", "pa-Arab-PK", "ps-AF", "th-TH", "hi-IN", "ta-IN" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    var queue = (Views.QueueView)Field("ConverterView")!;
                    var history = (Views.HistoryView)Field("HistoryView")!;
                    Require(AutomationProperties.GetName(queue.TaskList) == LocalizationService.T("QueueTitle"),
                        "Queue accessibility name was not localized.");
                    Require(AutomationProperties.GetName(history.HistorySearchBox) == LocalizationService.T("HistorySearchPlaceholder"),
                        "History search accessibility name was not localized.");
                    Require(AutomationProperties.GetLiveSetting(queue.AppInfoBar) == AutomationLiveSetting.Polite,
                        "Queue feedback was not exposed to assistive technology.");
                    var dialog = (ContentDialog)Invoke("CreateKeyboardShortcutsDialog")!;
                    Require((string)dialog.Title == LocalizationService.T("DesktopUpgradeShortcuts"), "Shortcut help was not localized.");
                    Require(dialog.FlowDirection == ((Grid)Field("RootGrid")!).FlowDirection,
                        "Shortcut help lost its reading direction.");
                    Require(((TextBlock)dialog.Content).Text.Contains("Ctrl+Enter", StringComparison.Ordinal),
                        "Shortcut help did not describe the queue command.");
                }
            }
            finally
            {
                LocalizationService.SetLanguage("zh-Hans");
                Invoke("ApplyLocalization");
            }
        });
    }

    private static void RequireUpgradeEvent(object view, string eventName, string handlerName)
    {
        var field = view.GetType().GetField(eventName, InstanceMembers);
        var handlers = (field?.GetValue(view) as Delegate)?.GetInvocationList();
        Require(handlers?.Any(handler => handler.Method.Name == handlerName) == true,
            $"{eventName} did not reach {handlerName}.");
    }
}
