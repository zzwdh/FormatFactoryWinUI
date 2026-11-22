using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private const string AutomationRuleOption = "automation.ruleSnapshot";
    private const string AutomationFingerprintOption = "automation.fingerprint";
    private readonly SemaphoreSlim _automationChanges = new(1, 1);
    private readonly Dictionary<Guid, TaskCompletionSource<AutomationExecutionResult>> _automationCompletions = [];
    private readonly HashSet<Guid> _automationGroupsInFlight = [];
    private IReadOnlyList<AutomationRule> _automationRules = [];
    private AutomationService? _automationService;
    private bool _automationReady;
    private bool _automationStopping;

    private bool HasAutomaticRules => _automationReady && _automationRules.Any(rule =>
        rule.Enabled && (rule.WatchEnabled || rule.ScheduleEnabled || rule.CatchUpOnStartup));

    private async Task InitializeAutomationAsync()
    {
        try
        {
            _automationService = _services.CreateAutomationService(QueueAutomationFileAsync);
            _automationService.ActivityChanged += AutomationActivityChanged;
            var stored = await _services.AutomationStore.LoadAsync();
            if (_windowClosed) return;
            var rules = stored.Rules.Select(rule => rule.Snapshot()).ToArray();
            foreach (var rule in rules)
            {
                var resolved = await StorageAccessService.ResolveFolderPathAsync(rule.FolderPath, rule.FolderAccessToken);
                if (!string.IsNullOrWhiteSpace(resolved) && !SameAutomationPath(resolved, rule.FolderPath))
                {
                    rule.FolderPath = resolved;
                }
            }
            if (_windowClosed) return;
            await _automationService.ConfigureAsync(rules);
            _automationRules = rules;
            AutomationView.Initialize(rules, BrowseAutomationFolderAsync);
            AutomationView.RulesChanged += AutomationRulesChanged;
            AutomationView.RunRequested += AutomationRunRequested;
            AutomationView.IsEnabled = _automationReady = true;
            await InitializeAutomationPresetsAsync();
            foreach (var task in _queueSession.Tasks.Where(task => task.State == ConversionState.Cancelled).ToArray())
                await RememberAutomationCancellationAsync(task);
            await _automationService.StartAsync();
        }
        catch (Exception exception)
        {
            _automationReady = false;
            SafeDiagnostics.Report(exception);
            AutomationView.ShowLocalizedStatus("AutomationLoadFailed", true, exception.Message);
            if (_automationService is not null) await _automationService.StopAsync();
        }
    }

    private async Task<(string Path, string? AccessToken)?> BrowseAutomationFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || _windowClosed) return null;
        return (folder.Path, await StorageAccessService.RememberFolderAsync(folder.Path));
    }

    private async void AutomationRulesChanged(IReadOnlyList<AutomationRule> rules)
    {
        await _automationChanges.WaitAsync();
        try
        {
            if (_windowClosed || !_automationReady || _automationService is null || _queueSession.IsStopping) return;
            var snapshot = rules.Select(rule => rule.Snapshot()).ToArray();
            await _automationService.ConfigureAsync(snapshot);
            _automationRules = snapshot;
            if (_windowClosed) return;
            ConfigureWatchFolder();
            AutomationView.ShowLocalizedStatus("AutomationRulesSaved");
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed)
            {
                AutomationView.RestoreRules(_automationRules);
                AutomationView.ShowLocalizedStatus("AutomationInvalidRule", true, exception.Message);
            }
        }
        finally { _automationChanges.Release(); }
    }

    private async void AutomationRunRequested(Guid ruleId)
    {
        try
        {
            AutomationService? service;
            await _automationChanges.WaitAsync();
            try
            {
                if (!_automationReady || _automationStopping || _automationService is null || _queueSession.IsStopping) return;
                service = _automationService;
            }
            finally { _automationChanges.Release(); }
            var summary = await service.RunNowAsync(ruleId);
            if (!_windowClosed)
                AutomationView.ShowLocalizedStatus("AutomationRunFinished", summary.Failed > 0,
                    summary.Scanned, summary.Succeeded, summary.Failed, summary.Skipped);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) AutomationView.ShowStatus(exception.Message, isError: true);
        }
    }

    private void AutomationActivityChanged(object? sender, AutomationActivity activity)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_windowClosed) return;
            AutomationView.ShowStatus($"{activity.RuleName}: {activity.Message}",
                activity.State == AutomationActivityState.Failed);
        });
    }

    private async Task<AutomationExecutionResult> QueueAutomationFileAsync(
        AutomationRule rule, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = FileFingerprintService.Read(path);
        if (fingerprint is null) return new(false, Error: LocalizationService.T("AutomationFileBusy", Path.GetFileName(path)), Deferred: true);
        var busy = false;
        var cancelled = false;
        MediaTask? reusable = null;
        IReadOnlyList<MediaTask> models = [];
        await RunOnUiAsync(() =>
        {
            cancelled = _queueSession.Tasks.Any(task => task.State == ConversionState.Cancelled &&
                SameAutomationPath(task.InputPath, path) && HasMatchingAutomationSnapshot(task, rule, fingerprint));
            reusable = FindReusableAutomationTask(rule, path, fingerprint);
            if (reusable is not null) models = reusable.OutputGroupId is { } existingGroup
                ? _queueSession.GetOutputGroup(existingGroup) : [reusable];
            busy = _settingsResetInProgress || !IsAutomationRuleEnabled(rule.Id)
                || HasConflictingAutomationTask(path, reusable?.Id, reusable?.OutputGroupId);
            return Task.CompletedTask;
        });
        if (cancelled) return new(false, Cancelled: true);
        if (busy) return new(false, Error: LocalizationService.T("AutomationFileBusy", Path.GetFileName(path)), Deferred: true);

        MediaTask? importedSource = null;
        if (reusable is null)
        {
            var imported = await _services.Conversions.AutomationImport.ImportPathsAsync(
                _ffprobePath, (string[])[path], rule.FolderPath, rule.FolderAccessToken, cancellationToken);
            importedSource = imported.AddedTasks.FirstOrDefault();
            if (importedSource is null)
                return new(false, Error: LocalizationService.T("AutomationImportFailed", Path.GetFileName(path),
                    imported.Failures.FirstOrDefault()?.Error?.Message ?? LocalizationService.T("AutomationFileBusy", Path.GetFileName(path))),
                    Retryable: ConversionFailurePolicy.IsTransient(imported.Failures.FirstOrDefault()?.Error));
            try
            {
                if (importedSource.ContentKind == ContentKind.Disc)
                {
                    if (rule.GetOutputs().Count != 1) throw new InvalidDataException(LocalizationService.T("AutomationValidationTarget"));
                    var output = rule.GetOutputs()[0];
                    if (!importedSource.AvailableFormats.Contains(FormatCatalog.FindById(output.TargetFormatId)?.DisplayName ?? string.Empty))
                        throw new InvalidDataException(LocalizationService.T("AutomationTargetUnsupported", importedSource.FileName, output.TargetFormatId));
                    importedSource.TargetFormatId = output.TargetFormatId;
                    importedSource.ConversionMode = output.ConversionMode;
                    importedSource.SelectedAudioTrack = output.SelectedAudioTrack;
                    foreach (var option in output.Options) importedSource.Options[option.Key] = option.Value;
                    models = new[] { importedSource };
                }
                else models = MultiOutputTaskFactory.Create(importedSource, rule.GetOutputs());
            }
            catch (Exception exception)
            {
                _services.Conversions.AutomationImport.Forget(importedSource);
                return new(false, Error: exception.Message);
            }
        }

        var completions = models.ToDictionary(model => model.Id,
            _ => new TaskCompletionSource<AutomationExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        AutomationExecutionResult? rejected = null;
        var groupId = models[0].OutputGroupId;
        try
        {
            await RunOnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_windowClosed || _queueSession.IsStopping)
                {
                    rejected = new(false, Error: LocalizationService.T("AutomationQueueRejected"));
                    return Task.CompletedTask;
                }
                // Importing yields to the UI. Cancellation or removal since admission wins over a retry.
                if (_queueSession.Tasks.Any(task => task.State == ConversionState.Cancelled &&
                        SameAutomationPath(task.InputPath, path) && HasMatchingAutomationSnapshot(task, rule, fingerprint)) ||
                    reusable is not null && (models.Any(model => !_queueSession.Tasks.Contains(model)) ||
                        groupId.HasValue && models.Count != models[0].OutputExpectedCount))
                {
                    rejected = new(false, Cancelled: true);
                    return Task.CompletedTask;
                }
                if (reusable is not null && models.Any(model => _queueCoordinator.IsActive(model.Id) ||
                    model.State is not (ConversionState.Waiting or ConversionState.Failed or ConversionState.Completed)))
                {
                    rejected = new(false, Deferred: true);
                    return Task.CompletedTask;
                }
                if (reusable is not null && models.Any(model => !HasAutomationParameters(model, rule)))
                {
                    rejected = new(false, Error: LocalizationService.T("AutomationTaskEdited"));
                    return Task.CompletedTask;
                }
                if (_settingsResetInProgress || !IsAutomationRuleEnabled(rule.Id)
                    || HasConflictingAutomationTask(path, reusable?.Id, groupId))
                {
                    rejected = new(false, Error: LocalizationService.T("AutomationFileBusy", Path.GetFileName(path)), Deferred: true);
                    return Task.CompletedTask;
                }
                if (groupId is { } group) _automationGroupsInFlight.Add(group);
                foreach (var model in models)
                {
                    model.Options[AutomationRuleOption] = JsonSerializer.Serialize(rule.Snapshot());
                    model.Options[AutomationFingerprintOption] = JsonSerializer.Serialize(fingerprint);
                    model.Options["automation.outputDirectory"] = rule.OutputDirectory;
                    var completion = completions[model.Id];
                    if (model.State == ConversionState.Completed)
                    {
                        completion.TrySetResult(new(true, model.OutputPath));
                        continue;
                    }
                    if (!AutomationOutputExecutionPolicy.ShouldExecute(model, rule))
                    {
                        completion.TrySetResult(new(false, model.OutputPath, model.LastError));
                        continue;
                    }
                    _automationCompletions[model.Id] = completion;
                    if ((reusable is null && !_queueSession.AddTask(model)) || Tasks.FirstOrDefault(task => task.Id == model.Id) is not { } viewModel)
                    {
                        completion.TrySetResult(new(false, Error: LocalizationService.T("AutomationQueueRejected")));
                        continue;
                    }
                    if (reusable is null) _mediaImportCoordinator.RememberTask(model);
                    else if (viewModel.State == ConversionState.Failed) ResetTask(viewModel);
                    StartTask(viewModel);
                    if (!_queueCoordinator.IsActive(model.Id))
                        completion.TrySetResult(new(false, Error: LocalizationService.T("AutomationQueueRejected")));
                }
                return Task.CompletedTask;
            });
            if (rejected is not null) return rejected;
            var results = await Task.WhenAll(completions.Values.Select(completion => completion.Task)).WaitAsync(cancellationToken);
            var aggregate = AutomationOutputExecutionPolicy.Aggregate(results);
            if (!aggregate.Success) return aggregate;
            if (groupId is { } durableGroup)
            {
                var durable = false;
                await RunOnUiAsync(async () => { durable = await _queueSession.EnsureOutputGroupDurableAsync(durableGroup, cancellationToken); });
                if (!durable)
                    return new(false, Error: LocalizationService.T("AutomationQueueRejected"), Deferred: true);
            }
            else await _queueSession.SaveAsync(cancellationToken);
            aggregate = await _automationService!.CompleteExecutionAsync(rule, path, fingerprint, aggregate, cancellationToken);
            if (aggregate.Success && !aggregate.ArchivePending && groupId is { } completedGroup)
                await RunOnUiAsync(() => _queueSession.FinalizeOutputGroupAsync(completedGroup));
            return aggregate with { Checkpointed = aggregate.Success };
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                if (groupId is { } group) _automationGroupsInFlight.Remove(group);
                foreach (var model in models)
                {
                    _automationCompletions.Remove(model.Id);
                    if (cancellationToken.IsCancellationRequested && _queueCoordinator.IsActive(model.Id))
                        _queueSession.Cancel(model);
                }
                return Task.CompletedTask;
            });
            if (importedSource is not null) _services.Conversions.AutomationImport.Forget(importedSource);
        }
    }

    private bool HasConflictingAutomationTask(string path, Guid? exceptId = null, Guid? exceptGroup = null) => _queueSession.Tasks.Any(task =>
        task.Id != exceptId && (!exceptGroup.HasValue || task.OutputGroupId != exceptGroup) &&
        (task.State is ConversionState.Waiting or ConversionState.Probing or ConversionState.Running or ConversionState.Paused or ConversionState.Blocked) &&
        task.InputPaths.Any(input => SameAutomationPath(input, path)));

    private static bool SameAutomationPath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static void ApplyAutomationOutputSettings(MediaTask task, AppSettings settings)
    {
        if (!task.Options.TryGetValue(AutomationRuleOption, out _)) return;
        if (task.Options.TryGetValue("automation.outputDirectory", out var output) && !string.IsNullOrWhiteSpace(output))
        {
            settings.OutputMode = "custom";
            settings.CustomOutputDirectory = output;
        }
        if (task.Options.TryGetValue("layoutMode", out var layout) || task.Options.TryGetValue("layout", out layout)) settings.EbookLayoutMode = layout;
        if (task.Options.TryGetValue("ocrLanguage", out var language) || task.Options.TryGetValue("language", out language)) settings.OcrLanguage = language;
        if (task.Options.TryGetValue("outputFileNameTemplate", out var template)) settings.OutputFileNameTemplate = template;
        if (task.Options.TryGetValue("preserveFolderStructure", out var preserve) && bool.TryParse(preserve, out var preserveFolders))
            settings.PreserveFolderStructure = preserveFolders;
        if (task.Options.TryGetValue("repairDocx", out var repair) && bool.TryParse(repair, out var repairDocx))
            settings.RepairDocxOnImport = repairDocx;
    }

    private async Task HandleAutomationCompletionAsync(ConversionQueueOperationCompleted completion, MediaTask? task)
    {
        if (task?.State == ConversionState.Paused && !completion.RemoveRequested) return;
        var transient = _automationTransientFailures.TryRemove(completion.TaskId, out _) || ConversionFailurePolicy.IsTransient(completion.Error);
        var result = new AutomationExecutionResult(task?.State == ConversionState.Completed, task?.OutputPath,
            completion.Error?.Message ?? task?.LastError,
            completion.RemoveRequested || completion.Outcome == ConversionOutcome.Cancelled,
            Retryable: transient && string.IsNullOrWhiteSpace(task?.RecoverableOutputId));
        if (task is { State: ConversionState.Failed } && task.Options.ContainsKey(AutomationRuleOption))
        {
            task.Options[AutomationOutputExecutionPolicy.RetryableOption] = result.Retryable.ToString();
            try { await _queueSession.SaveAsync(); }
            catch (Exception exception)
            {
                SafeDiagnostics.Report(exception);
                result = result with { Error = exception.Message, Deferred = true, Retryable = false };
            }
        }
        if (_automationCompletions.TryGetValue(completion.TaskId, out var pending))
        {
            pending.TrySetResult(result);
            return;
        }
        // A restored failed task can be retried directly from the queue. Its durable
        // snapshot keeps the same rule and source version for completion/archiving.
        if ((!result.Success && !result.Cancelled) || !_automationReady || _automationStopping || _closeSaveInProgress || _queueSession.IsStopping ||
            _automationService is null || task is null ||
            !task.Options.TryGetValue(AutomationRuleOption, out var ruleJson) ||
            !task.Options.TryGetValue(AutomationFingerprintOption, out var fingerprintJson)) return;
        try
        {
            var rule = JsonSerializer.Deserialize<AutomationRule>(ruleJson);
            var fingerprint = JsonSerializer.Deserialize<AutomationFileFingerprint>(fingerprintJson);
            if (rule is not null && fingerprint is not null)
            {
                if (task.OutputGroupId is { } group)
                {
                    if (_automationGroupsInFlight.Contains(group)) return;
                    var siblings = _queueSession.GetOutputGroup(group);
                    if (siblings.Length != task.OutputExpectedCount || siblings.Any(sibling => sibling.State != ConversionState.Completed))
                    {
                        if (result.Cancelled)
                            await _automationService.CompleteExecutionAsync(rule, task.InputPath, fingerprint, result);
                        return;
                    }
                    if (!await _queueSession.EnsureOutputGroupDurableAsync(group)) return;
                    result = result with { Success = true, Cancelled = false, OutputPaths = siblings.Select(sibling => sibling.OutputPath).ToArray() };
                }
                var finalized = await _automationService.CompleteExecutionAsync(rule, task.InputPath, fingerprint, result);
                if (finalized.Success && !finalized.ArchivePending && task.OutputGroupId is { } completedGroup)
                    await _queueSession.FinalizeOutputGroupAsync(completedGroup);
                if (!string.IsNullOrWhiteSpace(finalized.Error)) AutomationView.ShowStatus(finalized.Error, isError: true);
            }
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            AutomationView.ShowStatus(exception.Message, isError: true);
        }
    }

    private async Task StopAutomationAsync()
    {
        _automationStopping = true;
        Task[] cancellations;
        lock (_automationCancellationGate) cancellations = _automationCancellationWrites.ToArray();
        await Task.WhenAll(cancellations);
        if (_automationService is not null) await _automationService.StopAsync();
    }

    private async Task ReconcileRestoredAutomationGroupsAsync()
    {
        if (!_automationReady || _automationService is null) return;
        foreach (var grouping in _queueSession.Tasks.Where(task => task.OutputGroupId.HasValue && !task.OutputGroupFinalized)
                     .GroupBy(task => task.OutputGroupId!.Value).ToArray())
        {
            var tasks = grouping.OrderBy(task => task.OutputOrder).ToArray();
            var first = tasks[0];
            if (tasks.Length != first.OutputExpectedCount || tasks.Any(task => task.State != ConversionState.Completed) ||
                !first.Options.TryGetValue(AutomationRuleOption, out var ruleJson) ||
                !first.Options.TryGetValue(AutomationFingerprintOption, out var fingerprintJson)) continue;
            try
            {
                var rule = JsonSerializer.Deserialize<AutomationRule>(ruleJson);
                var fingerprint = JsonSerializer.Deserialize<AutomationFileFingerprint>(fingerprintJson);
                if (rule is null || fingerprint is null) continue;
                if (!await _queueSession.EnsureOutputGroupDurableAsync(grouping.Key)) continue;
                var result = await _automationService.CompleteExecutionAsync(rule, first.InputPath, fingerprint,
                    new(true, first.OutputPath, OutputPaths: tasks.Select(task => task.OutputPath).ToArray()));
                if (result.Success && !result.ArchivePending) await _queueSession.FinalizeOutputGroupAsync(grouping.Key);
            }
            catch (Exception exception) { SafeDiagnostics.Report(exception); }
        }
    }

    private async Task HandleRecoveredAutomationOutputAsync(MediaTask task)
    {
        if (!_automationReady || _automationService is null || task.State != ConversionState.Completed ||
            !task.Options.TryGetValue(AutomationRuleOption, out var ruleJson) ||
            !task.Options.TryGetValue(AutomationFingerprintOption, out var fingerprintJson)) return;
        if (_automationCompletions.TryGetValue(task.Id, out var pending))
        {
            pending.TrySetResult(new(true, task.OutputPath));
            return;
        }
        if (task.OutputGroupId.HasValue)
        {
            await ReconcileRestoredAutomationGroupsAsync();
            return;
        }
        var rule = JsonSerializer.Deserialize<AutomationRule>(ruleJson);
        var fingerprint = JsonSerializer.Deserialize<AutomationFileFingerprint>(fingerprintJson);
        if (rule is not null && fingerprint is not null)
            await _automationService.CompleteExecutionAsync(rule, task.InputPath, fingerprint, new(true, task.OutputPath));
    }

    private bool IsAutomationRuleEnabled(Guid ruleId) =>
        _automationService?.Rules.Any(rule => rule.Id == ruleId && rule.Enabled) == true;

    private async Task RestartAutomationAsync()
    {
        if (_automationService is not null) await _automationService.StartAsync();
        _automationStopping = false;
    }

    private void DisposeAutomation()
    {
        _automationReady = false;
        AutomationView.RulesChanged -= AutomationRulesChanged;
        AutomationView.RunRequested -= AutomationRunRequested;
        DisposeAutomationUpgrades();
        if (_automationService is not null)
        {
            _automationService.ActivityChanged -= AutomationActivityChanged;
            _ = DisposeAutomationAsync(_automationService);
        }
        foreach (var pending in _automationCompletions.Values) pending.TrySetCanceled();
        _automationCompletions.Clear();
        _automationGroupsInFlight.Clear();
    }

    private static async Task DisposeAutomationAsync(AutomationService service)
    {
        try { await service.DisposeAsync(); }
        catch (Exception exception) { SafeDiagnostics.Report(exception); }
    }
}
