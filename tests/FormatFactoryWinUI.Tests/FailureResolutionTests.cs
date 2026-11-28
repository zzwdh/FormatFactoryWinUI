using System.Globalization;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class FailureResolutionTests
{
    [Theory]
    [InlineData("OutputDiskInsufficientSpace", "en-US", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("OutputDiskInsufficientSpace", "zh-Hans", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("OutputDiskInsufficientSpace", "fr-FR", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("ConversionUiOutputDirectoryNameInvalid", "zh-Hans", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("MediaSizeCannotMeetTarget", "en-US", FailureResolutionAction.EditConversionSettings)]
    [InlineData("MediaSizeCannotMeetTarget", "zh-Hans", FailureResolutionAction.EditConversionSettings)]
    [InlineData("MediaOptionsSizeBitrateConflict", "es-ES", FailureResolutionAction.EditConversionSettings)]
    [InlineData("RemuxUnsupportedStream", "de-DE", FailureResolutionAction.EditConversionSettings)]
    [InlineData("MediaCompatibilityMissingOutput", "en-US", FailureResolutionAction.OpenEngineSettings)]
    [InlineData("MediaCompatibilityEngineUnavailable", "zh-Hans", FailureResolutionAction.OpenEngineSettings)]
    [InlineData("OutputDiskInsufficientSpace", "ur-PK", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("MediaSizeCannotMeetTarget", "ha-Latn-NG", FailureResolutionAction.EditConversionSettings)]
    [InlineData("PleaseSetFfmpeg", "es-419", FailureResolutionAction.OpenEngineSettings)]
    [InlineData("FfmpegInvalid", "uz-Latn-UZ", FailureResolutionAction.OpenEngineSettings)]
    public void RecognizedSavedDiagnostic_OffersSpecificActionInAnyCurrentCulture(
        string key, string language, FailureResolutionAction expected)
    {
        using var directory = new TemporaryDirectory();
        var task = FailedTask(directory.Path);
        var template = LocalizationService.GetTextForLanguage(language, key);
        task.Message = task.LastError = string.Format(CultureInfo.InvariantCulture, template, "D:\\output", 100d, 10d);
        var original = task.LastError;

        Assert.Equal(expected, FailureResolutionService.ForTask(task));
        Assert.Equal(original, task.LastError);
        Assert.Equal(original, task.Message);
    }

    [Theory]
    [InlineData("Access to the path 'C:\\input.mp4' is denied.", FailureResolutionAction.None)]
    [InlineData("Unrecognized engine diagnostic", FailureResolutionAction.None)]
    [InlineData("Failed: Error opening output file D:\\output.mp4.", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("[out#0/mp4 @ 000001] Error opening output files: Permission denied", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("No space left on device", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("Access denied to 'D:\\.0123456789abcdef0123456789abcdef.write-check'.", FailureResolutionAction.ChooseOutputDirectory)]
    [InlineData("C:\\No space left on device.mp4", FailureResolutionAction.None)]
    public void RawDiagnostics_RequireSpecificOutputEvidence(string message, FailureResolutionAction expected)
    {
        using var directory = new TemporaryDirectory();
        var task = FailedTask(directory.Path);
        task.LastError = message;
        Assert.Equal(expected, FailureResolutionService.ForTask(task));
    }

    [Fact]
    public void MissingInput_IsLocatedBeforeSuggestingOutputChanges_AndAutomaticTasksOpenTheirRules()
    {
        var task = new MediaTask { InputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"),
            State = ConversionState.Blocked, LastError = "No space left on device" };
        Assert.Equal(FailureResolutionAction.LocateSource, FailureResolutionService.ForTask(task));
        task.Options["automation.ruleId"] = Guid.NewGuid().ToString();
        Assert.Equal(FailureResolutionAction.OpenAutomationRules, FailureResolutionService.ForTask(task));
        task.RecoverableOutputId = "validated-result";
        Assert.Equal(FailureResolutionAction.SaveValidatedOutput, FailureResolutionService.ForTask(task));
        task.IsRecoveringOutput = true;
        Assert.Equal(FailureResolutionAction.None, FailureResolutionService.ForTask(task));
    }

    [Theory]
    [InlineData(ConversionState.Waiting)]
    [InlineData(ConversionState.Running)]
    [InlineData(ConversionState.Paused)]
    [InlineData(ConversionState.Completed)]
    [InlineData(ConversionState.Cancelled)]
    public void NonFailureStates_NeverOfferFailureAction(ConversionState state)
    {
        var task = new MediaTask { State = state, RecoverableOutputId = "validated-result" };
        Assert.Equal(FailureResolutionAction.None, FailureResolutionService.ForTask(task));
    }

    [Fact]
    public void RestoredRow_ResolvesCurrentSourceOnFirstDisplayAndAfterActionRefresh()
    {
        using var directory = new TemporaryDirectory();
        var model = FailedTask(directory.Path);
        model.LastError = "No space left on device";
        using var view = new MediaTaskViewModel(model);

        // The source can disappear between queue restoration and realizing this row.
        File.Delete(model.InputPath);
        Assert.Equal(FailureResolutionAction.LocateSource, view.FailureAction);

        File.WriteAllText(model.InputPath, "restored");
        view.RefreshQueueActions();
        Assert.Equal(FailureResolutionAction.ChooseOutputDirectory, view.FailureAction);
    }

    [Fact]
    public void ViewModel_RefreshesActionFromDiagnosticsAndKeepsRawError()
    {
        using var directory = new TemporaryDirectory();
        var model = FailedTask(directory.Path);
        using var view = new MediaTaskViewModel(model);
        Assert.False(view.HasFailureAction);
        var changed = new List<string>();
        view.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        model.Message = model.LastError = "No space left on device";
        Assert.Equal(FailureResolutionAction.ChooseOutputDirectory, view.FailureAction);
        Assert.True(view.HasFailureAction);
        Assert.Contains(nameof(view.HasFailureAction), changed);
        Assert.Equal("No space left on device", view.DetailMessage);
        view.RefreshLocalization();
        Assert.Equal("No space left on device", model.LastError);
        model.State = ConversionState.Running;
        Assert.False(view.HasFailureAction);
    }

    [Fact]
    public void PerTaskOutputDirectory_IsCapturedAndUsedWithoutChangingGlobalSettings()
    {
        using var directory = new TemporaryDirectory();
        var task = FailedTask(directory.Path);
        var chosen = Path.Combine(directory.Path, "chosen");
        task.Options[OutputPathService.DirectoryOverrideKey] = chosen;
        var settings = new AppSettings { OutputMode = "source", CustomOutputDirectory = "unchanged", PreserveFolderStructure = true };
        var request = ConversionRequestFactory.Capture(task, settings);
        var executionSettings = ConversionRequestFactory.OutputSettings(settings, request);

        Assert.Equal(chosen, OutputPathService.ResolveOutputDirectory(task, settings));
        Assert.Equal(chosen, request.OutputDirectory);
        Assert.Equal(chosen, executionSettings.CustomOutputDirectory);
        Assert.Equal("custom", executionSettings.OutputMode);
        Assert.False(executionSettings.PreserveFolderStructure);
        Assert.Equal("source", settings.OutputMode);
        Assert.Equal("unchanged", settings.CustomOutputDirectory);
        Assert.True(settings.PreserveFolderStructure);
        Assert.Equal(chosen, HistoryReplayService.CopySafeOptions(task)[OutputPathService.DirectoryOverrideKey]);
    }

    [Fact]
    public async Task PerTaskOutputDirectory_SurvivesQueuePersistence()
    {
        using var directory = new TemporaryDirectory();
        var task = FailedTask(directory.Path);
        task.Options[OutputPathService.DirectoryOverrideKey] = Path.Combine(directory.Path, "chosen");
        var store = new QueueStore(Path.Combine(directory.Path, "queue.json"), () => []);
        await store.SaveAsync([task]);
        var restored = Assert.Single((await store.LoadResultAsync()).Items);
        Assert.Equal(task.Options[OutputPathService.DirectoryOverrideKey], restored.Options[OutputPathService.DirectoryOverrideKey]);
        Assert.Equal(task.LastError, restored.LastError);
    }

    private static MediaTask FailedTask(string directory)
    {
        var path = Path.Combine(directory, "source.mp4");
        File.WriteAllText(path, "fixture");
        return new MediaTask { InputPath = path, SourceFormatId = "mp4", TargetFormatId = "mp4",
            Kind = MediaKind.Video, State = ConversionState.Failed, LastError = "Synthetic failure", Message = "Synthetic failure" };
    }
}
