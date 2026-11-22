using System.Runtime.InteropServices;
using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckOutputFolderPickerAsync()
    {
        foreach (var scenario in new[] { "initialization", "waiting", "closed-failure", "closed-success", "cancel-and-success", "save-failure" })
            await CheckOutputFolderPickerActionAsync(scenario);
        Check("Output-folder picker failure warning is translated in every supported language", () =>
        {
            var language = LocalizationService.CurrentUICulture.Name;
            try
            {
                LocalizationService.SetLanguage("en-US");
                var english = LocalizationService.T("DesktopChooseOutputFolderFailed");
                foreach (var option in LocalizationService.LanguageOptions.Where(option => option.Key != "auto"))
                {
                    LocalizationService.SetLanguage(option.Key);
                    var message = LocalizationService.T("DesktopChooseOutputFolderFailed");
                    Require(message != "DesktopChooseOutputFolderFailed" && !string.IsNullOrWhiteSpace(message),
                        "The output-folder picker warning is missing for " + option.Key);
                    Require(option.Key == "en-US" || message != english,
                        "The output-folder picker warning fell back to English for " + option.Key);
                }
            }
            finally { LocalizationService.SetLanguage(language); Invoke("ApplyLocalization"); }
        });
    }

    private async Task CheckOutputFolderPickerActionAsync(string scenario)
    {
        await CheckAsync("Real output-folder button " + scenario + " contains errors and preserves unrelated state", async () =>
        {
            using var motion = new SettingsActionsMotionScope(_window);
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Output-folder picker checks must use the isolated run data root.");
            var fixture = Path.Combine(_runRoot, "output-folder-picker", scenario, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var sourcePath = Path.Combine(fixture, "pending.txt");
            var selectedPath = Path.Combine(fixture, "chosen-output");
            var oldOutput = Path.Combine(fixture, "previous-output");
            Directory.CreateDirectory(selectedPath);
            await File.WriteAllTextAsync(sourcePath, "Synthetic pending source for the isolated output-folder picker.");
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
            var settingsPath = Path.Combine(fixture, "settings.json");
            var queuePath = Path.Combine(fixture, "queue.json");
            var settingsService = new SettingsService(settingsPath);
            await settingsService.SaveAsync(new AppSettings
            {
                CustomOutputDirectory = oldOutput, WatchFolderEnabled = false, ParallelCount = 2
            });
            var baseline = await settingsService.LoadAsync();
            var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
            var language = LocalizationService.CurrentUICulture.Name;
            var originalWindow = _window;
            var reported = new List<Exception>();
            var handles = new List<IntPtr>();
            var failure = new COMException("Synthetic native output-folder picker failure; private details stay out of the warning.",
                unchecked((int)0x80004005));
            var saveFailure = new IOException("Synthetic isolated settings persistence failure.");
            Action<Exception> observe = reported.Add;
            var waiting = scenario is "waiting" or "closed-failure" or "closed-success";
            var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = true;
            MainWindow? window = null;
            QueueSession? session = null;
            var closed = false;
            var feedbackBefore = Volatile.Read(ref _feedbackHttpRequests);
            try
            {
                var services = new MainWindowServices(settingsService, new QueueStore(queuePath, () => []),
                    new LocalImportStorageAccess(),
                    feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)),
                    shell: new WindowShellServices(chooseOutputDirectory: handle =>
                    {
                        handles.Add(handle);
                        if (!first) return Task.FromResult<string?>(selectedPath);
                        first = false;
                        if (waiting) return gate.Task;
                        if (scenario == "initialization") throw failure;
                        return Task.FromResult<string?>(scenario == "cancel-and-success" ? null : selectedPath);
                    }));
                var activeWindow = new MainWindow(services);
                window = activeWindow;
                DetachSmokeWindowStartup(activeWindow);
                _window = activeWindow;
                LocalizationService.SetLanguage("en-US");
                SetField("_settings", baseline.Snapshot());
                SetField("_settingsAvailable", true);
                Invoke("EnsureSettingsViewLoaded");
                SetField("_initializing", false);
                var settings = (SettingsView)Field("SettingsView")!;
                settings.ParallelCountBox.Value = 1;
                var draft = settings.ViewModel.DraftSession;
                var edited = settings.CaptureSettingsDraft();
                var saveState = settings.ViewModel.SaveStateKey;
                Require(draft is { IsDirty: true }, "The fixture did not establish an edited settings draft.");
                var runtimeSettings = JsonSerializer.Serialize((AppSettings)Field("_settings")!);
                var queue = (QueueView)Field("ConverterView")!;
                queue.OutputDirectoryText.Text = oldOutput;
                queue.AppInfoBar.IsOpen = false;
                queue.AppInfoBar.Message = "Synthetic untouched message.";
                var originalWarning = queue.AppInfoBar.Message;
                var pending = new MediaTask
                {
                    InputPath = sourcePath, Kind = MediaKind.Document, SourceFormatId = "txt",
                    TargetFormatId = "pdf", State = ConversionState.Waiting
                };
                var activeSession = (QueueSession)Field("_queueSession")!;
                session = activeSession;
                Require(activeSession.AddTask(pending), "The synthetic pending task was not accepted.");
                await activeSession.SaveAsync();
                var queueBytes = await File.ReadAllBytesAsync(queuePath);
                var handlers = typeof(QueueView).GetField("ChooseOutputDirectoryButton_ClickRequested", InstanceMembers)!
                    .GetValue(queue) as Delegate;
                Require(handlers?.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, activeWindow)
                    && handler.Method.Name == "ChooseOutputDirectoryButton_Click") == true,
                    "The real output-folder button is not subscribed to the window handler.");
                if (scenario == "save-failure")
                    SetField("_settingsCommits", new SettingsCommitCoordinator(
                        _ => Task.FromException(saveFailure), (_, _) => Task.CompletedTask));
                SafeDiagnostics.ErrorReported += observe;

                Click();
                Require(handles.Count == 1 && handles[0] == (IntPtr)Field("_windowHandle")!,
                    "The output-folder picker did not receive the real window handle exactly once.");
                if (waiting)
                {
                    Require(reported.Count == 0, "The pending native selection already reported an error.");
                    if (scenario.StartsWith("closed-", StringComparison.Ordinal))
                    {
                        await activeSession.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                        activeWindow.Close();
                        closed = true;
                        Require(Field("_windowClosed") is true, "The hidden fixture window did not close.");
                    }
                    if (scenario == "closed-success") gate.SetResult(selectedPath);
                    else gate.SetException(failure);
                }

                if (scenario is "initialization" or "waiting" or "closed-failure")
                {
                    await WaitUntilAsync(() => reported.Count == 1);
                    Require(ReferenceEquals(reported[0], failure), "The original native picker error was hidden or duplicated.");
                    var pickerFacts = RuntimeFailureDiagnostics.Get(failure);
                    Require(pickerFacts is { Stage: RuntimeFailureStage.Unknown, Attempt: 1 }
                        && pickerFacts.Operation != Guid.Empty && pickerFacts.Recovered == false,
                        "The injected picker failure has no operation facts or invents a native substage.");
                    Require(Field("_diagnosticPickerCount") is 0,
                        "The output-folder failure retained a concurrent picker observation.");
                    if (closed)
                        Require(!queue.AppInfoBar.IsOpen && queue.AppInfoBar.Message == originalWarning,
                            "A late picker failure updated the closed window.");
                    else
                        Require(queue.AppInfoBar.IsOpen && queue.AppInfoBar.Severity == InfoBarSeverity.Warning
                            && queue.AppInfoBar.Message == LocalizationService.T("DesktopChooseOutputFolderFailed")
                            && !queue.AppInfoBar.Message.Contains(failure.Message, StringComparison.Ordinal),
                            "The native picker failure did not leave a safe visible warning.");
                    await RequireRetainedAsync();
                }
                else if (scenario == "save-failure")
                {
                    await WaitUntilAsync(() => reported.Count == 1);
                    Require(ReferenceEquals(reported[0], saveFailure)
                        && settings.SettingsInfoBar.IsOpen && settings.SettingsInfoBar.Severity == InfoBarSeverity.Error
                        && settings.ViewModel.SaveStateKey == "ConversionUiSettingsSaveFailed"
                        && !queue.AppInfoBar.IsOpen,
                        "The existing settings-save failure policy changed or the outer picker catch reported it again.");
                    await RequireRetainedAsync(allowSaveStatusChange: true);
                }
                else
                {
                    await Task.Delay(30);
                    Require(reported.Count == 0 && !queue.AppInfoBar.IsOpen && queue.AppInfoBar.Message == originalWarning,
                        "A cancelled or closed output-folder result produced an error or updated the warning.");
                    await RequireRetainedAsync();
                }

                if (!closed && scenario != "save-failure")
                {
                    Click();
                    await WaitUntilAsync(() => queue.OutputDirectoryText.Text == selectedPath);
                    Require(Field("_diagnosticPickerCount") is 0,
                        "The output-folder success retained a concurrent picker observation.");
                    Require(handles.Count == 2 && reported.Count == (scenario == "cancel-and-success" ? 0 : 1),
                        "A successful retry or selection generated another diagnostic.");
                    var expected = baseline.Snapshot();
                    expected.CustomOutputDirectory = selectedPath;
                    Require(JsonSerializer.Serialize((AppSettings)Field("_settings")!) == JsonSerializer.Serialize(expected),
                        "Output-folder selection changed unrelated runtime settings.");
                    var saved = await settingsService.LoadAsync();
                    Require(saved.CustomOutputDirectory == selectedPath && saved.ParallelCount == baseline.ParallelCount,
                        "The successful output selection saved unrelated dirty settings.");
                    await RequireRetainedAsync(outputChanged: true);
                }

                void Click() => typeof(QueueView).GetMethod("ChooseOutputDirectoryButton_Click", InstanceMembers)!
                    .Invoke(queue, new object[] { queue.OutputDirectoryButton, new RoutedEventArgs() });

                async Task RequireRetainedAsync(bool outputChanged = false, bool allowSaveStatusChange = false)
                {
                    Require(ReferenceEquals(draft, settings.ViewModel.DraftSession)
                        && draft is { IsDirty: true } && draft.Current == edited && settings.CaptureSettingsDraft() == edited,
                        "The output-folder action replaced or committed the dirty settings draft.");
                    if (!allowSaveStatusChange)
                        Require(settings.ViewModel.SaveStateKey == saveState, "The output-folder action changed the dirty draft save status.");
                    if (!outputChanged)
                        Require(JsonSerializer.Serialize((AppSettings)Field("_settings")!) == runtimeSettings
                            && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(settingsBytes)
                            && queue.OutputDirectoryText.Text == oldOutput,
                            "A failed, cancelled or closed picker result changed the output or settings.");
                    Require(activeSession.Tasks.Count == 1 && ReferenceEquals(activeSession.Tasks[0], pending)
                        && activeWindow.Tasks.Count == 1 && ReferenceEquals(activeWindow.Tasks[0].Model, pending)
                        && pending.State == ConversionState.Waiting
                        && (await File.ReadAllBytesAsync(queuePath)).SequenceEqual(queueBytes)
                        && (await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(sourceBytes),
                        "The output-folder action changed the queue or source bytes.");
                    Require(Volatile.Read(ref _feedbackHttpRequests) == feedbackBefore,
                        "The isolated output-folder action sent remote feedback.");
                }
            }
            finally
            {
                SafeDiagnostics.ErrorReported -= observe;
                try
                {
                    if (!closed)
                    {
                        if (session is not null) await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                        window?.Close();
                    }
                }
                finally
                {
                    _window = originalWindow;
                    LocalizationService.SetLanguage(language);
                    if (_window is not null) Invoke("ApplyLocalization");
                }
            }
        });

        static async Task WaitUntilAsync(Func<bool> complete)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!complete())
            {
                if (DateTime.UtcNow >= until) throw new TimeoutException("The isolated output-folder action did not complete.");
                await Task.Delay(10);
            }
        }
    }
}
