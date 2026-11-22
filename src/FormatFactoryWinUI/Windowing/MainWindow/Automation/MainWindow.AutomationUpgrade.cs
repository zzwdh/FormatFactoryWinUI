using System.Collections.Concurrent;
using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private readonly ConcurrentDictionary<Guid, byte> _automationTransientFailures = new();
    private readonly SemaphoreSlim _automationPresetChanges = new(1, 1);
    private AutomationPresetStore? _automationPresetStore;
    private IReadOnlyList<AutomationConversionPreset> _automationPresets = [];
    private bool _automationPreviewOpen;
    private readonly object _automationCancellationGate = new();
    private readonly HashSet<Task> _automationCancellationWrites = [];

    private async Task InitializeAutomationPresetsAsync()
    {
        AutomationView.PreviewRequested -= AutomationPreviewRequested;
        AutomationView.SavePresetRequested -= AutomationSavePresetRequested;
        AutomationView.DeletePresetRequested -= AutomationDeletePresetRequested;
        AutomationView.PreviewRequested += AutomationPreviewRequested;
        AutomationView.SavePresetRequested += AutomationSavePresetRequested;
        AutomationView.DeletePresetRequested += AutomationDeletePresetRequested;
        _automationPresetStore ??= new AutomationPresetStore();
        try
        {
            _automationPresets = await _automationPresetStore.LoadAsync();
            if (!_windowClosed) AutomationView.SetPresets(_automationPresets);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) AutomationView.ShowLocalizedStatus("AutomationPresetLoadFailed", true, exception.Message);
        }
    }

    private async void AutomationSavePresetRequested(AutomationConversionPreset preset)
    {
        try { await SaveAutomationPresetAsync(preset); }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) AutomationView.ShowLocalizedStatus("AutomationInvalidRule", true, exception.Message);
        }
    }

    private async Task SaveAutomationPresetAsync(AutomationConversionPreset preset)
    {
        await _automationPresetChanges.WaitAsync();
        try
        {
            if (_windowClosed || _queueSession.IsStopping) return;
            preset.Validate();
            _automationPresetStore ??= new AutomationPresetStore();
            var stored = await _automationPresetStore.LoadAsync();
            // Applying a preset copies its settings; edits never silently replace a library entry.
            var updated = stored.Where(item => item.Id != preset.Id).Append(preset.Snapshot()).ToArray();
            await _automationPresetStore.SaveAsync(updated);
            _automationPresets = updated;
            if (_windowClosed) return;
            AutomationView.SetPresets(updated);
            AutomationView.ShowLocalizedStatus("AutomationPresetSaved", false, preset.Name);
        }
        finally { _automationPresetChanges.Release(); }
    }

    private async void AutomationDeletePresetRequested(Guid id)
    {
        await _automationPresetChanges.WaitAsync();
        try
        {
            if (_windowClosed || _queueSession.IsStopping) return;
            _automationPresetStore ??= new AutomationPresetStore();
            var stored = await _automationPresetStore.LoadAsync();
            var updated = stored.Where(item => item.Id != id).ToArray();
            await _automationPresetStore.SaveAsync(updated);
            _automationPresets = updated;
            if (_windowClosed) return;
            AutomationView.SetPresets(updated);
            AutomationView.ShowLocalizedStatus("AutomationPresetRemoved");
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) AutomationView.ShowLocalizedStatus("AutomationInvalidRule", true, exception.Message);
        }
        finally { _automationPresetChanges.Release(); }
    }

    private async void AutomationPreviewRequested(AutomationRule draft)
    {
        if (_windowClosed || _automationPreviewOpen || _settingsResetInProgress || _queueSession.IsStopping) return;
        _automationPreviewOpen = true;
        try
        {
            var stored = await _services.AutomationStore.LoadAsync();
            var busy = _queueSession.Tasks.Where(task =>
                task.State is ConversionState.Waiting or ConversionState.Probing or ConversionState.Running or ConversionState.Paused or ConversionState.Blocked)
                .SelectMany(task => task.InputPaths).ToArray();
            var preview = await AutomationPreviewService.PreviewAsync(draft, _automationRules, stored, _settings, busy);
            if (_windowClosed || RootGrid.XamlRoot is null) return;
            var content = new StackPanel { Spacing = 12, MaxWidth = 720 };
            content.Children.Add(new TextBlock
            {
                Text = LocalizationService.T("AutomationPreviewHint"), TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = LocalizationService.T("AutomationPreviewSummary", preview.Items.Count, preview.Ready, preview.Skipped, preview.Waiting),
                TextWrapping = TextWrapping.Wrap
            });
            if (preview.Error is { } error) content.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap });
            if (preview.Truncated) content.Children.Add(new TextBlock
            {
                Text = LocalizationService.T("AutomationPreviewLimit", 500), TextWrapping = TextWrapping.Wrap
            });
            var lines = preview.Items.Select(item =>
                $"{LocalizationService.T("AutomationPreviewInput")}: {item.InputPath}\n" +
                (item.OutputPath is { } output ? $"{LocalizationService.T("AutomationPreviewOutput")}: {output}\n" : string.Empty) +
                $"{LocalizationService.T("AutomationPreviewReason")}: {item.Reason}").ToArray();
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 430,
                Content = new TextBlock
                {
                    Text = string.Join("\n\n", lines), TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true, LineHeight = 22
                }
            });
            await ShowContentDialogAsync(new ContentDialog
            {
                Title = LocalizationService.T("AutomationPreviewTitle"), Content = content,
                CloseButtonText = LocalizationService.T("MediaInfoClose"), XamlRoot = RootGrid.XamlRoot
            });
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) AutomationView.ShowLocalizedStatus("AutomationInvalidRule", true, exception.Message);
        }
        finally { _automationPreviewOpen = false; }
    }

    private async void SaveTaskPresetButton_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (task is null || _windowClosed || _queueSession.IsStopping || RootGrid.XamlRoot is null) return;
        var name = new TextBox { Header = LocalizationService.T("AutomationPresetName"), MaxLength = 128, Text = task.TargetFormat };
        var dialog = new ContentDialog
        {
            Title = LocalizationService.T("AutomationSaveTaskPreset"), Content = name,
            PrimaryButtonText = LocalizationService.T("AutomationSave"), CloseButtonText = LocalizationService.T("Cancel"),
            XamlRoot = RootGrid.XamlRoot, DefaultButton = ContentDialogButton.Primary
        };
        AutomationConversionPreset? preset = null;
        dialog.PrimaryButtonClick += (_, click) =>
        {
            try
            {
                preset = AutomationConversionPreset.FromTask(task.Model, name.Text.Trim());
                if (!preset.Options.ContainsKey("outputFileNameTemplate")) preset.Options["outputFileNameTemplate"] = _settings.OutputFileNameTemplate;
                if (!preset.Options.ContainsKey("preserveFolderStructure")) preset.Options["preserveFolderStructure"] = _settings.PreserveFolderStructure.ToString();
                if (FormatCatalog.FindById(task.Model.TargetFormatId)?.ContentKind is ContentKind.Document or ContentKind.Ebook)
                {
                    if (!preset.Options.ContainsKey("ocrLanguage")) preset.Options["ocrLanguage"] = _settings.OcrLanguage;
                    if (task.Model.TargetFormatId is "epub" or "mobi" or "azw3" && !preset.Options.ContainsKey("layoutMode"))
                        preset.Options["layoutMode"] = _settings.EbookLayoutMode;
                }
                preset.Validate();
            }
            catch (Exception exception)
            {
                click.Cancel = true;
                name.Header = exception.Message;
            }
        };
        try
        {
            if (await ShowContentDialogAsync(dialog) == ContentDialogResult.Primary && preset is not null)
                await SaveAutomationPresetAsync(preset);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
    }

    private MediaTask? FindReusableAutomationTask(AutomationRule rule, string path, AutomationFileFingerprint fingerprint) =>
        _queueSession.Tasks.FirstOrDefault(task =>
            (task.State is ConversionState.Waiting or ConversionState.Failed || task.OutputGroupId.HasValue && task.State == ConversionState.Completed && !task.OutputGroupFinalized) &&
            string.IsNullOrWhiteSpace(task.RecoverableOutputId) && !task.IsRecoveringOutput &&
            SameAutomationPath(task.InputPath, path) && HasMatchingAutomationSnapshot(task, rule, fingerprint));

    private static bool HasMatchingAutomationSnapshot(MediaTask task, AutomationRule rule, AutomationFileFingerprint fingerprint)
    {
        if (!task.Options.TryGetValue(AutomationRuleOption, out var json) ||
            !task.Options.TryGetValue(AutomationFingerprintOption, out var fingerprintJson)) return false;
        try
        {
            var captured = JsonSerializer.Deserialize<AutomationRule>(json);
            return captured?.Options is not null && captured.Id == rule.Id && AutomationService.SameConversion(captured, rule) &&
                SameAutomationPath(captured.OutputDirectory, rule.OutputDirectory) &&
                captured.ArchiveSource == rule.ArchiveSource &&
                (!rule.ArchiveSource || SameAutomationPath(captured.ArchiveDirectory, rule.ArchiveDirectory)) &&
                captured.Options.Count == rule.Options.Count && captured.Options.All(pair => rule.Options.GetValueOrDefault(pair.Key) == pair.Value) &&
                JsonSerializer.Deserialize<AutomationFileFingerprint>(fingerprintJson) == fingerprint;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool HasAutomationParameters(MediaTask task, AutomationRule rule)
    {
        var output = rule.GetOutputs().FirstOrDefault(output => output.Id == task.OutputConfigurationId) ??
            (rule.GetOutputs().Count == 1 ? rule.GetOutputs()[0] : null);
        if (output is null) return false;
        var options = task.Options.Where(pair => !OutputConfiguration.IsExecutionOption(pair.Key)).ToArray();
        return task.TargetFormatId == output.TargetFormatId && task.ConversionMode == output.ConversionMode &&
            task.SelectedAudioTrack == output.SelectedAudioTrack && options.Length == output.Options.Count &&
            options.All(pair => output.Options.GetValueOrDefault(pair.Key) == pair.Value);
    }

    private void RememberAutomationCancellation(MediaTask task)
    {
        var write = RememberAutomationCancellationAsync(task);
        lock (_automationCancellationGate) _automationCancellationWrites.Add(write);
        _ = write.ContinueWith(completed =>
        {
            lock (_automationCancellationGate) _automationCancellationWrites.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RememberAutomationCancellationAsync(MediaTask task)
    {
        if (!_automationReady || _automationStopping || _automationService is null ||
            !task.Options.TryGetValue(AutomationRuleOption, out var ruleJson) ||
            !task.Options.TryGetValue(AutomationFingerprintOption, out var fingerprintJson)) return;
        try
        {
            var rule = JsonSerializer.Deserialize<AutomationRule>(ruleJson);
            var fingerprint = JsonSerializer.Deserialize<AutomationFileFingerprint>(fingerprintJson);
            if (rule is not null && fingerprint is not null)
                await _automationService.CompleteExecutionAsync(rule, task.InputPath, fingerprint, new(false, Cancelled: true));
        }
        catch (Exception exception) { SafeDiagnostics.Report(exception); }
    }

    private void DisposeAutomationUpgrades()
    {
        AutomationView.PreviewRequested -= AutomationPreviewRequested;
        AutomationView.SavePresetRequested -= AutomationSavePresetRequested;
        AutomationView.DeletePresetRequested -= AutomationDeletePresetRequested;
        _automationTransientFailures.Clear();
    }
}
