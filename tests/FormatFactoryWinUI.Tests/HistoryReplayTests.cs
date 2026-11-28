using System.Text.Json;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class HistoryReplayTests
{
    [Fact]
    public async Task CompletedHistoryCapturesParametersWithoutSecretsAndClonesIndependently()
    {
        using var directory = new TemporaryDirectory();
        var historyPath = Path.Combine(directory.Path, "history.json");
        var task = CreateTask(directory.Path);
        task.Options["imageColorPolicy"] = "preserve";
        task.Options["password"] = "private-password";
        task.Options["apiToken"] = "private-api-token";
        task.Options["automation.ruleSnapshot"] = "private-automation-rule";
        task.Options["title"] = "private-document-text";
        task.InputAccessToken = "private-access-token";
        HistoryReplayService.CaptureExecutionSettings(task, new AppSettings
        {
            EbookLayoutMode = "fixed", OcrLanguage = "zh-Hans", HardwareAcceleration = false,
            CpuThreadsPerTask = 3, CustomOutputDirectory = directory.Path,
            PreserveFolderStructure = false, OutputFileNameTemplate = "{name}-{format}",
            WatchFolderAccessToken = "private-watch-token", FfmpegPath = "private-engine-path"
        });
        var store = new ConversionHistoryStore(historyPath);

        await store.RecordCompletedAsync(task);
        var item = Assert.Single(await store.LoadAsync());
        Assert.Equal(1, item.SnapshotVersion);
        Assert.Equal("quality", item.ConversionMode);
        Assert.Equal(3, item.ParametersVersion);
        Assert.Equal(1, item.SelectedAudioTrack);
        Assert.Equal("preserve", item.Options["imageColorPolicy"]);
        Assert.Equal("fixed", item.SettingsSnapshot!.EbookLayoutMode);
        Assert.Equal("zh-Hans", item.SettingsSnapshot.OcrLanguage);
        Assert.False(item.SettingsSnapshot.HardwareAcceleration);
        Assert.Equal("{name}-{format}", item.SettingsSnapshot.OutputFileNameTemplate);
        Assert.DoesNotContain("private-", await File.ReadAllTextAsync(historyPath));
        task.Options["imageColorPolicy"] = "srgb";
        item.Options["imageColorPolicy"] = "srgb";
        item.SettingsSnapshot.OcrLanguage = "mutated";
        var restored = Assert.Single(await store.LoadAsync());
        Assert.Equal("preserve", restored.Options["imageColorPolicy"]);
        Assert.Equal("zh-Hans", restored.SettingsSnapshot!.OcrLanguage);
    }

    [Fact]
    public async Task LegacyHistoryLoadsAndReplaysWithExplicitDefaultParameters()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory.Path);
        var path = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { new
        {
            TaskId = task.Id, InputPaths = task.InputPaths, task.OutputPath, DisplayName = "result.pdf",
            ContentKind = (int)ContentKind.Document, SourceFormatId = "docx", TargetFormatId = "pdf", EngineId = "document-pipeline"
        }}));
        var item = Assert.Single(await new ConversionHistoryStore(path).LoadAsync());

        var replay = HistoryReplayService.CreateQueueItem(item);

        Assert.Equal(0, item.SnapshotVersion);
        Assert.NotEqual(item.TaskId, replay.TaskId);
        Assert.Equal("pdf", replay.TargetFormatId);
        Assert.Equal("balanced", replay.ConversionMode);
        Assert.Empty(replay.Options);
        Assert.Equal(LocalizationService.T("HistoryLegacyParameters"), HistoryPageViewModel.DescribeParameters(item));
    }

    [Fact]
    public async Task ReplayFreshlyImportsWhileOriginalIsStillQueuedAndPreservesTargetModeAndAudio()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory.Path);
        var existingImporter = CreateImporter();
        Assert.NotNull((await existingImporter.ImportPathsAsync("", [task.InputPath])).AddedTasks.Single());
        var item = ConversionHistoryItem.FromTask(task);
        var importer = CreateImporter();

        var first = await HistoryReplayService.ImportAsync(item, "", importer);
        var second = await HistoryReplayService.ImportAsync(item, "", CreateImporter());

        Assert.NotNull(first.Task);
        Assert.NotNull(second.Task);
        Assert.NotEqual(task.Id, first.Task.Id);
        Assert.NotEqual(first.Task.Id, second.Task.Id);
        Assert.Equal("pdf", first.Task.TargetFormatId);
        Assert.Equal("quality", first.Task.ConversionMode);
        Assert.Equal(1, first.Task.SelectedAudioTrack);
        Assert.Equal(ConversionState.Waiting, first.Task.State);
        Assert.Empty(first.Task.OutputPath);
        Assert.Equal(string.Empty, first.Task.LastError);
    }

    [Fact]
    public async Task ReplayReservationTransfersToQueueImporterAndSurvivesOriginalRemoval()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory.Path);
        var queueImporter = CreateImporter();
        var original = Assert.Single((await queueImporter.ImportPathsAsync("", [task.InputPath])).AddedTasks);
        var historyImporter = CreateImporter();
        var replay = (await HistoryReplayService.ImportAsync(ConversionHistoryItem.FromTask(task), "", historyImporter)).Task;
        Assert.NotNull(replay);

        // Match the UI transfer: register only after the queue accepts the task,
        // then release the temporary history importer's reservation.
        queueImporter.RememberTask(replay);
        historyImporter.Forget(replay);
        queueImporter.Forget(original);

        Assert.Equal(1, (await queueImporter.ImportPathsAsync("", [task.InputPath])).IgnoredCount);
        Assert.Single((await historyImporter.ImportPathsAsync("", [task.InputPath])).AddedTasks);
        queueImporter.Forget(replay);
        Assert.Single((await queueImporter.ImportPathsAsync("", [task.InputPath])).AddedTasks);
    }

    [Fact]
    public async Task MissingInputStopsReplayBeforeProbingOrPublishingAnyTask()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory.Path);
        var item = ConversionHistoryItem.FromTask(task);
        File.Delete(task.InputPath);
        var probeCalls = 0;
        var importer = new MediaImportCoordinator((_, _, _) =>
        {
            probeCalls++;
            return Task.FromResult(new MediaProbeResult());
        });

        var missing = Assert.Single(HistoryReplayService.MissingInputs(item));
        await Assert.ThrowsAsync<FileNotFoundException>(() => HistoryReplayService.ImportAsync(item, "", importer));

        Assert.Equal(task.InputPath, missing);
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task UnsupportedSavedTargetIsRejectedInsteadOfSilentlyUsingDefault()
    {
        using var directory = new TemporaryDirectory();
        var item = ConversionHistoryItem.FromTask(CreateTask(directory.Path));
        item.TargetFormatId = "mp3";
        var importer = CreateImporter();

        await Assert.ThrowsAsync<InvalidDataException>(() => HistoryReplayService.ImportAsync(item, "", importer));
        Assert.Single((await importer.ImportPathsAsync("", item.InputPaths)).AddedTasks);
    }

    [Fact]
    public void ReplayRestoresSavedConversionSettingsWithoutChangingNotificationsOrEnginePaths()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory.Path);
        var settings = new AppSettings
        {
            EbookLayoutMode = "fixed", OcrLanguage = "ja-JP", HardwareAcceleration = false,
            CpuThreadsPerTask = 3, CustomOutputDirectory = directory.Path,
            PreserveFolderStructure = false, OutputFileNameTemplate = "{date}-{name}"
        };
        HistoryReplayService.CaptureExecutionSettings(task, settings);
        var queueItem = HistoryReplayService.CreateQueueItem(ConversionHistoryItem.FromTask(task));
        var replayTask = new MediaTask();
        foreach (var option in queueItem.Options) replayTask.Options[option.Key] = option.Value;
        var actual = new AppSettings { FfmpegPath = "current-engine", Notifications = false };

        HistoryReplayService.ApplyReplaySettings(replayTask, actual);

        Assert.Equal("fixed", actual.EbookLayoutMode);
        Assert.Equal("ja-JP", actual.OcrLanguage);
        Assert.False(actual.HardwareAcceleration);
        Assert.Equal(3, actual.CpuThreadsPerTask);
        Assert.Equal(directory.Path, actual.CustomOutputDirectory);
        Assert.False(actual.PreserveFolderStructure);
        Assert.Equal("{date}-{name}", actual.OutputFileNameTemplate);
        Assert.Equal("current-engine", actual.FfmpegPath);
        Assert.False(actual.Notifications);
    }

    [Fact]
    public void ReplayRetainsOrderedImageInputsAndDoesNotRetainOutputPathOrAccessTokens()
    {
        var item = new ConversionHistoryItem
        {
            TaskId = Guid.NewGuid(), SnapshotVersion = 1, ContentKind = ContentKind.Image,
            SourceFormatId = "png", TargetFormatId = "pdf", SourceRoot = "pictures",
            InputPaths = ["pictures/02.png", "pictures/01.jpg"], OutputPath = "old-output.pdf",
            Options = new() { ["outputPath"] = "old-output.pdf", ["password"] = "secret", ["layoutMode"] = "fixed" }
        };

        var replay = HistoryReplayService.CreateQueueItem(item);

        Assert.Equal(item.InputPaths, replay.Inputs.Select(input => input.Path));
        Assert.Equal(new[] { "png", "jpg" }, replay.Inputs.Select(input => input.FormatId));
        Assert.Equal("pictures", replay.SourceRoot);
        Assert.Null(replay.InputAccessToken);
        Assert.All(replay.Inputs, input => Assert.Null(input.AccessToken));
        Assert.Equal("fixed", Assert.Single(replay.Options).Value);
    }

    [Theory]
    [InlineData("[{\"Options\":null}]")]
    [InlineData("[{\"Options\":{\"layoutMode\":null}}]")]
    [InlineData("[{\"Options\":{\"layoutMode\":\"fixed\",\"LAYOUTMODE\":\"reflow\"}}]")]
    [InlineData("[{\"SettingsSnapshot\":{\"OcrLanguage\":null}}]")]
    public async Task InvalidNewSnapshotCannotReplaceOriginalHistory(string contents)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(path, contents);
        var store = new ConversionHistoryStore(path);

        Assert.Empty(await store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecordCompletedAsync(CreateTask(directory.Path)));
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExternallyLoadedSecretsAreRemovedBeforeDisplayingAndResavingHistory()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory.Path);
        var path = Path.Combine(directory.Path, "history.json");
        var original = ConversionHistoryItem.FromTask(task);
        original.Options["password"] = "external-secret";
        original.Options["title"] = "external-secret-text";
        original.Options["imageColorPolicy"] = "preserve";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { original }));
        var store = new ConversionHistoryStore(path);

        var item = Assert.Single(await store.LoadAsync());
        Assert.Equal("imageColorPolicy", Assert.Single(item.Options).Key);
        await store.RecordCompletedAsync(task);
        Assert.DoesNotContain("external-secret", await File.ReadAllTextAsync(path));
    }
    private static MediaImportCoordinator CreateImporter() =>
        new((_, _, _) => Task.FromResult(new MediaProbeResult()));

    private static MediaTask CreateTask(string directory)
    {
        var input = Path.Combine(directory, "source.docx");
        var output = Path.Combine(directory, "result.pdf");
        File.WriteAllText(input, "source");
        File.WriteAllText(output, "result");
        return new MediaTask
        {
            InputPath = input, OutputPath = output, ContentKind = ContentKind.Document,
            SourceFormatId = "docx", TargetFormatId = "pdf", ConversionMode = "quality", ParametersVersion = 3,
            SelectedAudioTrack = 1, SourceRoot = directory, State = ConversionState.Completed
        };
    }
}

