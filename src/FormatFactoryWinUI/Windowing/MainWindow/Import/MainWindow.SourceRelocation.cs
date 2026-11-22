using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private void InitializeSourceRelocation()
    {
        ConverterView.RelocateSource_ClickRequested += RelocateSource_Click;
        ConverterView.RelocateSourceFolder_ClickRequested += RelocateSourceFolder_Click;
    }

    private void DisposeSourceRelocation()
    {
        ConverterView.RelocateSource_ClickRequested -= RelocateSource_Click;
        ConverterView.RelocateSourceFolder_ClickRequested -= RelocateSourceFolder_Click;
    }

    private bool CanBeginSourceRelocation(MediaTaskViewModel? task) => task is not null &&
        !_windowClosed && !_initializing && !_closeSaveInProgress && !_settingsResetInProgress &&
        !_desktopCommandInProgress && _reviewBusinessDialogs == 0 && _queueSession.CanRelocateSource(task.Model);

    private async void RelocateSource_Click(object sender, RoutedEventArgs args)
    {
        var task = FindTaskFromSender(sender);
        if (!CanBeginSourceRelocation(task)) return;
        _desktopCommandInProgress = true;
        try
        {
            var model = task!.Model;
            var originalPaths = model.InputPaths.ToArray();
            var missing = originalPaths.Where(path => !File.Exists(path)).ToArray();
            if (missing.Length == 0) return;
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            bool CanContinueRelocation()
            {
                if (_windowClosed || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping) return false;
                if (missing.Any(File.Exists))
                {
                    ShowInfo(LocalizationService.T("SourceRelocationSourceAvailable"), InfoBarSeverity.Informational);
                    return false;
                }
                return _queueSession.CanRelocateSource(model) &&
                    originalPaths.SequenceEqual(model.InputPaths, StringComparer.OrdinalIgnoreCase);
            }

            for (var index = 0; index < missing.Length; index++)
            {
                if (!CanContinueRelocation()) return;
                var originalPath = missing[index];
                ShowInfo($"{LocalizationService.T("SourceRelocationAction")} ({index + 1}/{missing.Length}){Environment.NewLine}{originalPath}",
                    InfoBarSeverity.Informational);
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    CommitButtonText = LocalizationService.T("SourceRelocationAction")
                };
                var extension = Path.GetExtension(originalPath);
                picker.FileTypeFilter.Add(string.IsNullOrWhiteSpace(extension) ? "*" : extension);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
                var file = await picker.PickSingleFileAsync();
                if (file is null || !CanContinueRelocation()) return;
                var inputIndex = Array.FindIndex(originalPaths, path => path.Equals(originalPath, StringComparison.OrdinalIgnoreCase));
                if (SourceRelocationService.KnownSize(model, inputIndex) is null)
                {
                    var confirmation = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, FlowDirection = RootGrid.FlowDirection,
                        Title = LocalizationService.T("SourceRelocationConfirmTitle"),
                        Content = LocalizationService.T("SourceRelocationConfirmUnknown", originalPath, file.Path),
                        PrimaryButtonText = LocalizationService.T("SourceRelocationConfirm"),
                        CloseButtonText = LocalizationService.T("NativeUiCancel"), DefaultButton = ContentDialogButton.Close
                    };
                    if (await ShowContentDialogAsync(confirmation) != ContentDialogResult.Primary || !CanContinueRelocation()) return;
                }
                replacements[originalPath] = file.Path;
            }
            if (!CanContinueRelocation()) return;
            var result = await RelocateSourceModelAsync(model, replacements);
            if (!_windowClosed) ShowInfo(LocalizationService.T(result.SaveError is null ? "SourceRelocationCompleted" : "SourceRelocationSaveFailed",
                result.SaveError is null ? result.UpdatedTasks : result.SaveError.Message),
                result.SaveError is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            if (exception is not SourceRelocationValidationException) SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
        finally { _desktopCommandInProgress = false; }
    }

    private async void RelocateSourceFolder_Click(object sender, RoutedEventArgs args)
    {
        var selected = FindTaskFromSender(sender);
        if (!CanBeginSourceRelocation(selected)) return;
        _desktopCommandInProgress = true;
        try
        {
            var firstMissing = selected!.Model.InputPaths.First(path => !File.Exists(path));
            var oldDirectory = Path.GetDirectoryName(Path.GetFullPath(firstMissing))!;
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || _windowClosed || _closeSaveInProgress || _settingsResetInProgress || _queueSession.IsStopping) return;
            var candidates = Tasks.Select(item => item.Model).Where(model => _queueSession.CanRelocateSource(model) &&
                model.InputPaths.Where(path => !File.Exists(path)).All(path =>
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), oldDirectory, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(model => model.OutputGroupId ?? model.Id).Select(group => group.First()).ToArray();
            if (candidates.Length == 0)
            {
                var messageKey = !SourceRelocationService.HasMissingInputs(selected.Model) ? "SourceRelocationNothingMissing" :
                    _queueSession.CanRelocateSource(selected.Model) ? "SourceRelocationOtherFolder" : "SourceRelocationUnavailable";
                ShowInfo(LocalizationService.T(messageKey), InfoBarSeverity.Informational);
                return;
            }
            var updated = 0;
            var skipped = 0;
            var errors = new List<string>();
            foreach (var model in candidates)
            {
                if (_windowClosed || _queueSession.IsStopping) break;
                try
                {
                    var map = SourceRelocationService.MapFolder(model, oldDirectory, folder.Path);
                    var result = await RelocateSourceModelAsync(model, map, requireKnownSize: true);
                    updated += result.UpdatedTasks;
                    if (result.SaveError is { } saveError) errors.Add(saveError.Message);
                }
                catch (Exception exception)
                {
                    skipped++;
                    if (exception is not SourceRelocationValidationException) SafeDiagnostics.Report(exception);
                    errors.Add($"{model.FileName}: {exception.Message}");
                }
            }
            if (!_windowClosed) ShowInfo(LocalizationService.T("SourceRelocationFolderCompleted", updated, skipped) +
                (errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, errors.Take(3)) : string.Empty),
                errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            if (exception is not SourceRelocationValidationException) SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
        finally { _desktopCommandInProgress = false; }
    }

    private async Task<SourceRelocationResult> RelocateSourceModelAsync(MediaTask model,
        IReadOnlyDictionary<string, string> replacements, bool requireKnownSize = false)
    {
        var members = _queueSession.Tasks.Where(task => task.Id == model.Id ||
            model.OutputGroupId is { } groupId && task.OutputGroupId == groupId).ToArray();
        var before = members.ToDictionary(task => task.Id, task => task.InputPaths.ToArray());
        var access = new RelocationStorageAccess();
        var importer = _services.Conversions.CreateImporter(access);
        SourceRelocationResult result;
        try
        {
            result = await _queueSession.RelocateSourcesAsync(model, replacements,
                (item, token) => importer.ImportPersistedAsync(_ffprobePath, item, token), importer.Forget,
                _mediaImportCoordinator.RememberTask, requireKnownSize);
        }
        finally
        {
            // Failed probes may have acquired permissions before producing a task.
            // Keep grants already published to the queue even if saving then failed.
            var retained = members.Where(task => !before[task.Id].SequenceEqual(task.InputPaths, StringComparer.OrdinalIgnoreCase))
                .SelectMany(task => task.Inputs.Select(input => input.AccessToken).Append(task.InputAccessToken))
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            await access.ReleaseUncommittedAsync(retained);
        }
        foreach (var view in Tasks.Where(view => view.Id == model.Id || model.OutputGroupId is not null && view.Model.OutputGroupId == model.OutputGroupId))
            view.RefreshLocalization();
        ScheduleQueueRefresh();
        return result;
    }

    private sealed class RelocationStorageAccess : IImportStorageAccess
    {
        private readonly WindowsImportStorageAccess _inner = new();
        private readonly List<string> _acquired = [];

        public async Task<string?> RememberFileAsync(string path, CancellationToken cancellationToken = default)
        {
            var token = await StorageTokenRegistry.RememberFileAsync(path, cancellationToken);
            if (!string.IsNullOrWhiteSpace(token)) _acquired.Add(token);
            cancellationToken.ThrowIfCancellationRequested();
            return token;
        }
        public Task<string> ResolveFilePathAsync(string path, string? token, CancellationToken cancellationToken = default) =>
            _inner.ResolveFilePathAsync(path, token, cancellationToken);
        public Task<string?> ResolveFolderPathAsync(string? path, string? token, CancellationToken cancellationToken = default) =>
            _inner.ResolveFolderPathAsync(path, token, cancellationToken);

        public async Task ReleaseUncommittedAsync(IReadOnlySet<string> retained)
        {
            foreach (var token in _acquired.Where(token => !retained.Contains(token)))
                try { await StorageAccessService.ReleaseAsync(token); }
                catch (Exception exception) { SafeDiagnostics.Report(exception); }
        }
    }
}
