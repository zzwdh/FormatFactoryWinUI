using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MultiOutputQueueTests
{
    [Fact]
    public void Factory_CreatesIndependentOutputsAndKeepsInputOwnershipAndFirstIdentity()
    {
        var source = Source("movie.mp4");
        source.SourceRoot = "folder";
        source.InputAccessToken = "token";
        source.Options["automation.ruleSnapshot"] = "snapshot";
        source.Options["history.capturedSettings"] = "settings";
        source.Options["password"] = "secret";
        source.Options["custom"] = "retained";
        var first = OutputConfiguration.FromTask(source, "高清");
        var second = first.Snapshot() with { Id = Guid.NewGuid(), Label = "音频", TargetFormatId = "mp3" };

        var outputs = MultiOutputTaskFactory.Create(source, [first, second]);

        Assert.Equal(source.Id, outputs[0].Id);
        Assert.NotEqual(source.Id, outputs[1].Id);
        Assert.Equal(outputs[0].OutputGroupId, outputs[1].OutputGroupId);
        Assert.All(outputs, task =>
        {
            Assert.Equal(2, task.OutputExpectedCount);
            Assert.Equal("folder", task.SourceRoot);
            Assert.Equal("token", task.InputAccessToken);
            Assert.Equal("retained", task.Options["custom"]);
            Assert.DoesNotContain(task.Options.Keys, OutputConfiguration.IsExecutionOption);
            Assert.Equal(ConversionState.Waiting, task.State);
            Assert.Empty(task.OutputPath);
        });
        outputs[0].Options["custom"] = "changed";
        Assert.Equal("retained", outputs[1].Options["custom"]);
        Assert.Equal("retained", source.Options["custom"]);
        Assert.Equal("mp3", outputs[1].TargetFormatId);
        var automatic = MultiOutputTaskFactory.Create(source, [first], retainAutomationSnapshot: true);
        Assert.Equal("snapshot", automatic[0].Options["automation.ruleSnapshot"]);
        Assert.DoesNotContain("history.capturedSettings", automatic[0].Options.Keys);
        Assert.DoesNotContain("password", automatic[0].Options.Keys);
    }

    [Fact]
    public void Factory_RejectsIncompatibleAndRepeatedConfigurationsBeforeCreatingRows()
    {
        var source = Source("movie.mp4");
        var output = OutputConfiguration.FromTask(source);
        Assert.Throws<InvalidDataException>(() => MultiOutputTaskFactory.Create(source, [output, output]));
        Assert.Throws<InvalidOperationException>(() => MultiOutputTaskFactory.Create(source,
            [output with { TargetFormatId = "pdf" }]));
        Assert.Null(source.OutputGroupId);
    }

    [Fact]
    public async Task QueueStore_PreservesCompletedSiblingWhileGroupIsUnfinished()
    {
        using var directory = new TemporaryDirectory();
        var source = Source(Path.Combine(directory.Path, "source.mp4"));
        var profile = OutputConfiguration.FromTask(source, "first");
        var group = MultiOutputTaskFactory.Create(source, [profile, profile with { Id = Guid.NewGuid(), Label = "second" }]);
        group[0].State = ConversionState.Completed;
        group[0].OutputPath = Path.Combine(directory.Path, "first.mp4");
        group[0].OutputHistoryRecorded = true;
        group[1].State = ConversionState.Failed;
        group[1].Options["custom"] = "saved";
        var path = Path.Combine(directory.Path, "queue.json");
        var store = new QueueStore(path);

        await store.SaveAsync(group);
        var restored = await new QueueStore(path).LoadAsync();

        Assert.Equal(2, restored.Count);
        Assert.Equal(group[0].OutputPath, restored[0].OutputPath);
        Assert.Equal(ConversionState.Completed, restored[0].State);
        Assert.True(restored[0].OutputHistoryRecorded);
        Assert.Equal("saved", restored[1].Options["custom"]);
        Assert.Contains("\"SchemaVersion\": 4", await File.ReadAllTextAsync(path));
        foreach (var task in group) { task.State = ConversionState.Completed; task.OutputGroupFinalized = true; }
        await store.SaveAsync(group);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task Restore_SuccessfulSiblingDoesNotRequireArchivedSourceOrProbe()
    {
        using var directory = new TemporaryDirectory();
        var source = Source(Path.Combine(directory.Path, "missing-source.mp4"));
        var profile = OutputConfiguration.FromTask(source);
        var group = MultiOutputTaskFactory.Create(source, [profile, profile with { Id = Guid.NewGuid() }]);
        group[0].State = ConversionState.Completed;
        group[0].OutputPath = Path.Combine(directory.Path, "completed.mp4");
        await File.WriteAllTextAsync(group[0].OutputPath, "completed");
        group[0].OutputHistoryRecorded = true;
        group[0].OutputFingerprint = FileFingerprintService.Read(group[0].OutputPath);
        group[1].State = ConversionState.Failed;
        var persistence = new MemoryPersistence(group.Select(QueueStore.ToPersistedItem).ToArray());
        var probed = new List<Guid>();
        using var session = new QueueSession(persistence, (item, _) =>
        {
            probed.Add(item.TaskId);
            return Task.FromResult(new MediaImportItemResult(item.InputPath, null, new FileNotFoundException()));
        }, (_, _, _) => Task.FromResult(Success("unused")), (_, _) => Task.CompletedTask);

        await session.RestoreAsync();

        Assert.Equal([group[1].Id], probed);
        Assert.Equal(ConversionState.Completed, session.Tasks[0].State);
        Assert.Equal(group[0].OutputPath, session.Tasks[0].OutputPath);
        Assert.Equal(ConversionState.Blocked, session.Tasks[1].State);
        Assert.Equal(2, persistence.Saved.Count);
    }

    [Fact]
    public async Task Finalization_WaitsForReliableHistoryAndAllExpectedOutputs()
    {
        using var directory = new TemporaryDirectory();
        var source = Source(Path.Combine(directory.Path, "source.mp4"));
        var profile = OutputConfiguration.FromTask(source);
        var tasks = MultiOutputTaskFactory.Create(source, [profile, profile with { Id = Guid.NewGuid() }]);
        var historyFails = true;
        var persistence = new MemoryPersistence([]);
        using var session = new QueueSession(persistence, (_, _) => throw new InvalidOperationException(),
            (_, _, _) => Task.FromResult(Success("unused")), (_, _) =>
            {
                if (historyFails) throw new IOException("history unavailable");
                return Task.CompletedTask;
            }, diagnostics: _ => { });
        foreach (var task in tasks)
        {
            session.AddTask(task);
            task.State = ConversionState.Completed;
            task.OutputPath = Path.Combine(directory.Path, task.Id.ToString("N") + ".mp4");
            await File.WriteAllTextAsync(task.OutputPath, "result");
        }

        Assert.False(await session.TryFinalizeManualOutputGroupAsync(tasks[0].OutputGroupId!.Value));
        Assert.All(tasks, task => Assert.False(task.OutputGroupFinalized));
        historyFails = false;
        Assert.True(await session.TryFinalizeManualOutputGroupAsync(tasks[0].OutputGroupId!.Value));
        Assert.All(tasks, task => Assert.True(task.OutputGroupFinalized));
        Assert.Empty(persistence.Saved);
    }

    [Fact]
    public void RemovingMember_UpdatesManualExpectationButNeverCompletesAnAutomaticGroup()
    {
        var source = Source("source.mp4");
        var output = OutputConfiguration.FromTask(source);
        var manual = MultiOutputTaskFactory.Create(source, [output, output with { Id = Guid.NewGuid() }]);
        using var session = new QueueSession(new MemoryPersistence([]), (_, _) => throw new InvalidOperationException(),
            (_, _, _) => Task.FromResult(Success("unused")), (_, _) => Task.CompletedTask);
        foreach (var task in manual) session.AddTask(task);
        session.RemoveTask(manual[0]);
        Assert.Equal(1, manual[1].OutputExpectedCount);
        Assert.Equal(1, manual[1].OutputOrder);

        var automatic = MultiOutputTaskFactory.Create(source, [output, output with { Id = Guid.NewGuid() }]);
        foreach (var task in automatic)
        {
            task.Options["automation.ruleSnapshot"] = "snapshot";
            session.AddTask(task);
        }
        session.RemoveTask(automatic[0]);
        Assert.Equal(2, automatic[1].OutputExpectedCount);
    }

    [Fact]
    public void CommitReceipt_RecoversPublishedAndStagedArtifactsWithoutChoosingAnotherName()
    {
        using var directory = new TemporaryDirectory();
        var task = Source(Path.Combine(directory.Path, "source.mp4"));
        task.OutputGroupId = Guid.NewGuid();
        var receipts = new OutputCommitReceiptStore(Path.Combine(directory.Path, "receipts"));
        var staged = Path.Combine(directory.Path, "private.partial.mp4");
        var final = Path.Combine(directory.Path, "output.mp4");
        File.WriteAllText(staged, "validated bytes");
        receipts.Prepare(task, final, staged, false);

        Assert.Equal(final, receipts.TryRecover(task));
        Assert.False(File.Exists(staged));
        Assert.Equal(final, receipts.TryRecover(task));
        Assert.Single(Directory.GetFiles(directory.Path, "*.mp4"));
        File.WriteAllText(final, "changed bytes");
        Assert.Null(receipts.TryRecover(task));
        receipts.Forget(task.Id);
        Assert.Null(receipts.TryRecover(task));
        File.WriteAllText(Path.Combine(directory.Path, "receipts", task.Id.ToString("N") + ".json"), "{}");
        Assert.Null(receipts.TryRecover(task));
    }

    [Fact]
    public void CommitReceipt_RejectsChangedGroupLabelAndEveryMediaOptionIncludingRemovals()
    {
        using var directory = new TemporaryDirectory();
        var task = Source("source.mp4");
        task.OutputGroupId = Guid.NewGuid();
        task.OutputLabel = "original";
        var receipts = new OutputCommitReceiptStore(Path.Combine(directory.Path, "receipts"));
        var staged = Path.Combine(directory.Path, "output.partial.mp4");
        var final = Path.Combine(directory.Path, "output.mp4");
        File.WriteAllText(staged, "validated bytes");
        var originalOptions = MediaConversionOptions.OptionNames.Order(StringComparer.Ordinal)
            .Select((key, index) => new KeyValuePair<string, string>(key, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        foreach (var pair in originalOptions) task.Options[pair.Key] = pair.Value;
        receipts.Prepare(task, final, staged, false);

        var groupId = task.OutputGroupId;
        task.OutputGroupId = Guid.NewGuid();
        Assert.Null(receipts.TryRecover(task));
        task.OutputGroupId = groupId;
        task.OutputLabel = "renamed";
        Assert.Null(receipts.TryRecover(task));
        task.OutputLabel = "original";
        foreach (var pair in originalOptions)
        {
            task.Options[pair.Key] = "different";
            Assert.Null(receipts.TryRecover(task));
            task.Options.Remove(pair.Key);
            Assert.Null(receipts.TryRecover(task));
            task.Options[pair.Key] = pair.Value;
        }
        Assert.False(File.Exists(final));
        Assert.True(File.Exists(staged));
        Assert.Equal(final, receipts.TryRecover(task));

        var empty = Source("source.mp4");
        empty.OutputGroupId = Guid.NewGuid();
        var emptyStaged = Path.Combine(directory.Path, "empty-options.partial.mp4");
        var emptyFinal = Path.Combine(directory.Path, "empty-options.mp4");
        File.WriteAllText(emptyStaged, "validated bytes");
        receipts.Prepare(empty, emptyFinal, emptyStaged, false);
        empty.Options[MediaConversionOptions.MaxWidthKey] = "1920";
        Assert.Null(receipts.TryRecover(empty));
        Assert.False(File.Exists(emptyFinal));
    }

    [Fact]
    public void CommitReceipt_UsesAdmittedOptionsAndAcceptsCapturedDocumentDefaults()
    {
        using var directory = new TemporaryDirectory();
        var task = Source("source.mp4");
        task.OutputGroupId = Guid.NewGuid();
        task.OutputLabel = "original";
        task.SelectedAudioTrack = 2;
        task.Options[MediaConversionOptions.TargetSizeKey] = "1048576";
        var receipts = new OutputCommitReceiptStore(Path.Combine(directory.Path, "receipts"));
        var staged = Path.Combine(directory.Path, "output.partial.mp4");
        var final = Path.Combine(directory.Path, "output.mp4");
        File.WriteAllText(staged, "validated bytes");
        receipts.Register(task, final, staged, false);
        // Internal compatibility/encoder choices must not become the saved user choices.
        task.SelectedAudioTrack = 0;
        task.Options[MediaConversionOptions.VideoBitrateKey] = "350";
        receipts.Prepare(task, final, staged, false);
        task.SelectedAudioTrack = 2;
        task.Options.Remove(MediaConversionOptions.VideoBitrateKey);
        Assert.Equal(final, receipts.TryRecover(task));

        task.TargetFormatId = "pdf";
        var documentStaged = Path.Combine(directory.Path, "document.partial.pdf");
        var documentFinal = Path.Combine(directory.Path, "document.pdf");
        File.WriteAllText(documentStaged, "validated document");
        task.Options.Clear();
        task.Options["layoutMode"] = "reflow";
        task.Options["ocrLanguage"] = "auto";
        receipts.Register(task, documentFinal, documentStaged, false);
        receipts.Prepare(task, documentFinal, documentStaged, false);
        // Queue source choices need not contain defaults inserted into the engine request.
        task.Options.Clear();
        Assert.Equal(documentFinal, receipts.TryRecover(task));
    }

    [Fact]
    public void CommitReceipt_RecoversMarkdownAfterOnlyResourcesWereCommitted()
    {
        using var directory = new TemporaryDirectory();
        var task = Source("source.mp4");
        task.TargetFormatId = "md";
        task.OutputGroupId = Guid.NewGuid();
        var receipts = new OutputCommitReceiptStore(Path.Combine(directory.Path, "receipts"));
        var staged = Path.Combine(directory.Path, "markdown.tmp");
        var final = Path.Combine(directory.Path, "output.md");
        var stagedAssets = Path.Combine(directory.Path, "assets.tmp");
        var finalAssets = Path.Combine(directory.Path, "output.assets");
        File.WriteAllText(staged, "![image](output.assets/image.png)");
        Directory.CreateDirectory(stagedAssets);
        File.WriteAllText(Path.Combine(stagedAssets, "image.png"), "image");
        receipts.Register(task, final, staged, false);
        receipts.PrepareRegisteredPath(final, staged, stagedAssets, finalAssets);
        Directory.Move(stagedAssets, finalAssets);

        Assert.Equal(final, receipts.TryRecover(task));
        Assert.True(File.Exists(final));
        Assert.True(File.Exists(Path.Combine(finalAssets, "image.png")));
    }

    private static MediaTask Source(string path)
    {
        var task = new MediaTask { InputPath = path, SourceFormatId = "mp4", TargetFormatId = "mp4",
            Probe = new MediaProbeResult { ContentKind = ContentKind.Video, FormatId = "mp4" } };
        task.AvailableFormats.Add("MP4");
        task.AvailableFormats.Add("MP3");
        return task;
    }
    private static ConversionWorkflowResult Success(string path) =>
        new(ConversionOutcome.Completed, new(true, false, [new ConversionArtifact("output", "mp4", path)], "completed"), "test");
    private sealed class MemoryPersistence(IReadOnlyList<PersistedQueueItem> items) : IQueuePersistence
    {
        public IReadOnlyList<PersistedQueueItem> Saved { get; private set; } = [];
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken token = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, items));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken token = default)
        {
            Saved = tasks.Where(task => task.State != ConversionState.Completed || task.OutputGroupId is not null && !task.OutputGroupFinalized)
                .Select(QueueStore.ToPersistedItem).ToArray();
            return Task.CompletedTask;
        }
    }
}
