using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Controls;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private bool _conversionOptionsOpen;

    private void InitializeConversionOptions()
    {
        ConverterView.CustomSettingsRequested += CustomSettings_Click;
        ConverterView.MultipleOutputsRequested += MultipleOutputs_Click;
        ConverterView.BatchCustomSettingsRequested += BatchCustomSettings_Click;
        ConverterView.BatchMultipleOutputsRequested += BatchMultipleOutputs_Click;
        ConverterView.SaveOutputGroupRequested += SaveOutputGroup_Click;
    }
    private void DisposeConversionOptions()
    {
        ConverterView.CustomSettingsRequested -= CustomSettings_Click;
        ConverterView.MultipleOutputsRequested -= MultipleOutputs_Click;
        ConverterView.BatchCustomSettingsRequested -= BatchCustomSettings_Click;
        ConverterView.BatchMultipleOutputsRequested -= BatchMultipleOutputs_Click;
        ConverterView.SaveOutputGroupRequested -= SaveOutputGroup_Click;
    }

    private async void CustomSettings_Click(object sender, RoutedEventArgs args)
    {
        if (FindTaskFromSender(sender) is { } task) await EditConversionOutputsAsync(new[] { task.Model }, false);
    }
    private async void MultipleOutputs_Click(object sender, RoutedEventArgs args)
    {
        if (FindTaskFromSender(sender) is { } task) await EditConversionOutputsAsync(new[] { task.Model }, true);
    }
    private async void BatchCustomSettings_Click(object sender, RoutedEventArgs args) =>
        await EditConversionOutputsAsync(_queueOperations.PrepareBatchEdit(ConverterView.SelectedTaskIds).EditableTasks, false);
    private async void BatchMultipleOutputs_Click(object sender, RoutedEventArgs args) =>
        await EditConversionOutputsAsync(_queueOperations.PrepareBatchEdit(ConverterView.SelectedTaskIds).EditableTasks, true);

    private async Task EditConversionOutputsAsync(IReadOnlyList<MediaTask> selected, bool multiple)
    {
        if (_windowClosed || _initializing || _conversionOptionsOpen || _closeSaveInProgress ||
            _settingsResetInProgress || _queueSession.IsStopping || RootGrid.XamlRoot is null) return;
        var sources = selected.Where(task => task.ContentKind != ContentKind.Disc)
            .GroupBy(task => multiple ? task.OutputGroupId ?? task.Id : task.Id).Select(group => group.First()).ToArray();
        if (sources.Length == 0)
        { ShowInfo(LocalizationService.T("QueueBatchSelectHint"), InfoBarSeverity.Informational); return; }
        var originals = sources.SelectMany(task => multiple && task.OutputGroupId is { } id
            ? _queueSession.Tasks.Where(item => item.OutputGroupId == id) : new[] { task }).DistinctBy(task => task.Id).ToArray();
        if (originals.Any(task => task.IsRecoveringOutput || _queueCoordinator.IsActive(task.Id) ||
            task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing ||
            multiple && (task.Options.ContainsKey(AutomationRuleOption) || task.State == ConversionState.Completed)))
        { ShowInfo(LocalizationService.T("MultiOutputIdleOnly"), InfoBarSeverity.Informational); return; }
        _conversionOptionsOpen = true;
        try
        {
            var common = sources.Skip(1).Aggregate(sources[0].AvailableFormats.AsEnumerable(),
                (formats, task) => formats.Intersect(task.AvailableFormats, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (common.Length == 0) throw new InvalidOperationException(LocalizationService.T("MultiOutputUnsupported"));
            var initial = multiple && sources[0].OutputGroupId is { } groupId
                ? _queueSession.Tasks.Where(task => task.OutputGroupId == groupId).OrderBy(task => task.OutputOrder)
                    .Select(task => OutputConfiguration.FromTask(task)).ToArray()
                : [OutputConfiguration.FromTask(sources[0])];
            var result = await OutputConfigurationEditorDialog.EditAsync(RootGrid.XamlRoot, common, initial,
                sources[0], _automationPresets, multiple, ShowContentDialogAsync);
            if (result is null || _windowClosed) return;
            if (originals.Any(task => !_queueSession.Tasks.Contains(task) || _queueCoordinator.IsActive(task.Id) ||
                task.IsRecoveringOutput || task.State is ConversionState.Running or ConversionState.Paused or ConversionState.Probing ||
                multiple && task.State == ConversionState.Completed))
                throw new InvalidOperationException(LocalizationService.T("MultiOutputIdleOnly"));
            // Validate every source before publishing any member of a batch.
            foreach (var source in sources)
                foreach (var output in result)
                {
                    var candidate = new MediaTask { InputPath = source.InputPath, ContentProbe = source.ContentProbe,
                        SourceFormatId = source.SourceFormatId, TargetFormatId = output.TargetFormatId,
                        ConversionMode = output.ConversionMode };
                    foreach (var input in source.Inputs) candidate.Inputs.Add(input);
                    MediaConversionOptions.Parse(output.Options).Validate(candidate);
                }
            if (!multiple)
            {
                _queueSession.ApplyTaskOptionsWithUndo(sources.Select(source => source.Id), source =>
                {
                    var output = result[0];
                    source.TargetFormatId = output.TargetFormatId;
                    source.ConversionMode = output.ConversionMode;
                    source.SelectedAudioTrack = output.SelectedAudioTrack;
                    MediaConversionOptions.Parse(output.Options).ApplyTo(source.Options);
                    foreach (var option in output.Options.Where(option => !MediaConversionOptions.OptionNames.Contains(option.Key)))
                        source.Options[option.Key] = option.Value;
                });
            }
            else
            {
                var expanded = sources.Select(source => MultiOutputTaskFactory.Create(source, result)).ToArray();
                var passwords = sources.ToDictionary(source => source.Id, source => _pdfPasswords.GetValueOrDefault(source.Id));
                _queueSession.CommitRemovalUndoForGroups(originals.Select(task => task.OutputGroupId).OfType<Guid>());
                foreach (var original in originals)
                    if (Tasks.FirstOrDefault(task => task.Id == original.Id) is { } view) RemoveTask(view);
                foreach (var (source, members) in sources.Zip(expanded))
                    foreach (var model in members)
                    {
                        if (passwords.GetValueOrDefault(source.Id) is { } password)
                            _pdfPasswords[model.Id] = password;
                        _mediaImportCoordinator.RememberTask(model);
                        _queueSession.AddTask(model);
                    }
            }
            RefreshVisibleTasks();
            ConverterView.RefreshOutputGroups();
            foreach (var task in Tasks) task.RefreshLocalization();
            await SaveQueueAsync();
            ShowInfo(LocalizationService.T("Saved"), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally { _conversionOptionsOpen = false; }
    }

    private async void SaveOutputGroup_Click(object sender, RoutedEventArgs args)
    {
        var source = FindTaskFromSender(sender);
        if (source is null || _conversionOptionsOpen || _windowClosed || RootGrid.XamlRoot is null) return;
        var members = source.Model.OutputGroupId is { } groupId
            ? _queueSession.Tasks.Where(task => task.OutputGroupId == groupId).OrderBy(task => task.OutputOrder).ToArray()
            : [source.Model];
        _conversionOptionsOpen = true;
        try
        {
            var name = new TextBox { Header = LocalizationService.T("AutomationPresetName"), MaxLength = 128 };
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = LocalizationService.T("MultiOutputSavePreset"),
                Content = name, PrimaryButtonText = LocalizationService.T("AutomationSave"), CloseButtonText = LocalizationService.T("Cancel") };
            AutomationConversionPreset? preset = null;
            dialog.PrimaryButtonClick += (_, click) =>
            {
                try { preset = AutomationConversionPreset.FromTasks(members, name.Text.Trim()); preset.Validate(); }
                catch (Exception exception) { click.Cancel = true; name.Header = exception.Message; }
            };
            if (await ShowContentDialogAsync(dialog) == ContentDialogResult.Primary && preset is not null)
            { await SaveAutomationPresetAsync(preset); ShowInfo(LocalizationService.T("AutomationPresetSaved", preset.Name), InfoBarSeverity.Success); }
        }
        catch (Exception exception)
        { SafeDiagnostics.Report(exception); if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Error); }
        finally { _conversionOptionsOpen = false; }
    }
}
