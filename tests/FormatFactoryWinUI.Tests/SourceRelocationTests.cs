using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class SourceRelocationTests
{
    [Fact]
    public async Task Relocation_PreservesTaskAndSettings_ReleasesImports_AndSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 7);
        original.InputAccessToken = "old-file-token";
        original.SourceRoot = directory.Path;
        original.SourceRootAccessToken = "old-folder-token";
        original.Options[MediaConversionOptions.MaxWidthKey] = "1280";
        original.ConversionMode = "quality";
        original.SelectedAudioTrack = 1;
        original.State = ConversionState.Blocked;
        var replacement = FileAt(directory, "new/renamed.mp4", 7);
        var persistence = new MemoryPersistence();
        var released = new List<MediaTask>();
        var remembered = new List<MediaTask>();
        using var session = Session(persistence, released.Add);
        session.AddTask(original);
        var temporary = new List<MediaTask>();
        var result = await session.RelocateSourcesAsync(original, Map(original.InputPath, replacement),
            Import, temporary.Add, remembered.Add);

        Assert.Equal(1, result.UpdatedTasks);
        Assert.Same(original, Assert.Single(session.Tasks));
        Assert.Equal(replacement, original.InputPath);
        Assert.Equal(replacement, Assert.Single(original.Inputs).Path);
        Assert.Null(original.InputAccessToken);
        Assert.Null(original.SourceRootAccessToken);
        Assert.Equal(ConversionState.Waiting, original.State);
        Assert.Equal("quality", original.ConversionMode);
        Assert.Equal("1280", original.Options[MediaConversionOptions.MaxWidthKey]);
        Assert.Equal(1, original.SelectedAudioTrack);
        Assert.Same(original, Assert.Single(released));
        Assert.Same(original, Assert.Single(remembered));
        Assert.NotSame(original, Assert.Single(temporary));
        Assert.False(original.IsRecoveringOutput);
        var saved = Assert.Single(persistence.LastSaved);
        Assert.Equal(original.Id, saved.TaskId);
        Assert.Equal(replacement, saved.InputPath);
        Assert.Equal(replacement, Assert.Single(saved.Inputs).Path);

        using var restored = new QueueSession(new MemoryPersistence(persistence.LastSaved), Import,
            (_, _, _) => throw new NotSupportedException(), (_, _) => Task.CompletedTask);
        await restored.RestoreAsync();
        var restarted = Assert.Single(restored.Tasks);
        Assert.Equal(replacement, restarted.InputPath);
        Assert.Equal(7, SourceRelocationService.KnownSize(restarted, 0));
        Assert.Equal("1280", restarted.Options[MediaConversionOptions.MaxWidthKey]);
    }

    [Fact]
    public async Task MultiOutput_UpdatesOnlyUnfinishedMembers_AndLeavesCompletedOutputUntouched()
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var replacement = FileAt(directory, "new.mp4", 3);
        var group = Guid.NewGuid();
        var first = Model(oldPath, 3);
        var second = Model(oldPath, 3);
        var completed = Model(oldPath, 3);
        foreach (var item in new[] { first, second, completed })
        {
            item.OutputGroupId = group;
            item.OutputConfigurationId = Guid.NewGuid();
            item.OutputExpectedCount = 3;
        }
        second.TargetFormat = "MKV";
        second.TargetFormatId = "mkv";
        completed.State = ConversionState.Completed;
        completed.OutputPath = "completed-output.mp4";
        completed.OutputHistoryRecorded = true;
        var ids = new[] { first.Id, second.Id, completed.Id };
        using var session = Session(new MemoryPersistence());
        foreach (var item in new[] { first, second, completed }) session.AddTask(item);

        var result = await session.RelocateSourcesAsync(first, Map(oldPath, replacement), Import, _ => { });

        Assert.Equal(2, result.UpdatedTasks);
        Assert.Equal(ids, session.Tasks.Select(item => item.Id));
        Assert.Equal(replacement, first.InputPath);
        Assert.Equal(replacement, second.InputPath);
        Assert.Equal("mkv", second.TargetFormatId);
        Assert.Equal(oldPath, completed.InputPath);
        Assert.Equal("completed-output.mp4", completed.OutputPath);
        Assert.Equal(ConversionState.Completed, completed.State);
        Assert.True(completed.OutputHistoryRecorded);
        Assert.All(session.Tasks, item => Assert.Equal(group, item.OutputGroupId));
    }

    [Fact]
    public async Task FailedGroupProbe_IsAtomicAndReleasesEveryTemporaryImport()
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var replacement = FileAt(directory, "new.mp4", 3);
        var first = Model(oldPath, 3);
        var second = Model(oldPath, 3);
        first.OutputGroupId = second.OutputGroupId = Guid.NewGuid();
        using var session = Session(new MemoryPersistence());
        session.AddTask(first);
        session.AddTask(second);
        var released = new List<MediaTask>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RelocateSourcesAsync(first,
            Map(oldPath, replacement), async (item, token) =>
            {
                var result = await Import(item, token);
                if (item.TaskId == second.Id) result.Task!.ContentKind = ContentKind.Audio;
                return result;
            }, released.Add));
        Assert.Equal(2, released.Count);
        Assert.All(session.Tasks, item =>
        {
            Assert.Equal(oldPath, item.InputPath);
            Assert.False(item.IsRecoveringOutput);
        });
    }

    [Fact]
    public async Task SuccessfulRelocation_CommitsRelatedRemovalButPreservesUnrelatedUndo()
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var replacement = FileAt(directory, "new.mp4", 3);
        var removed = Model(oldPath, 3);
        var remaining = Model(oldPath, 3);
        removed.OutputGroupId = remaining.OutputGroupId = Guid.NewGuid();
        var unrelatedRemoved = Model(Path.Combine(directory.Path, "other.mp4"), 3);
        var unrelatedRemaining = Model(unrelatedRemoved.InputPath, 3);
        unrelatedRemoved.OutputGroupId = unrelatedRemaining.OutputGroupId = Guid.NewGuid();
        var released = new List<MediaTask>();
        using var session = Session(new MemoryPersistence(), released.Add);
        foreach (var task in new[] { removed, remaining, unrelatedRemoved, unrelatedRemaining })
        {
            task.OutputExpectedCount = 2;
            session.AddTask(task);
        }
        Assert.Equal(2, session.RemoveTasksWithUndo([removed.Id, unrelatedRemoved.Id]).Length);
        var originalExpiry = session.UndoState!.ExpiresAtUtc;

        Assert.Equal(1, (await session.RelocateSourcesAsync(remaining, Map(oldPath, replacement), Import, _ => { })).UpdatedTasks);

        Assert.False(session.HasRemovalUndoForTask(removed.Id));
        Assert.True(session.HasRemovalUndoForTask(unrelatedRemoved.Id));
        Assert.Equal(1, session.UndoState!.TaskCount);
        Assert.Equal(originalExpiry, session.UndoState.ExpiresAtUtc);
        Assert.Contains(removed, released);
        Assert.DoesNotContain(unrelatedRemoved, released);
        Assert.Equal(1, remaining.OutputExpectedCount);
        Assert.Equal(2, unrelatedRemaining.OutputExpectedCount);
        Assert.Equal(1, session.UndoLastAction());
        Assert.Equal(replacement, Assert.Single(session.GetOutputGroup(remaining.OutputGroupId!.Value)).InputPath);
        Assert.DoesNotContain(removed, session.Tasks);
        Assert.Equal([unrelatedRemoved, unrelatedRemaining], session.GetOutputGroup(unrelatedRemaining.OutputGroupId!.Value));
    }

    [Fact]
    public async Task FailedRelocation_PreservesRelatedRemovalUndo()
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var replacement = FileAt(directory, "new.mp4", 3);
        var removed = Model(oldPath, 3);
        var remaining = Model(oldPath, 3);
        removed.OutputGroupId = remaining.OutputGroupId = Guid.NewGuid();
        removed.OutputExpectedCount = remaining.OutputExpectedCount = 2;
        var released = new List<MediaTask>();
        using var session = Session(new MemoryPersistence(), released.Add);
        session.AddTask(removed);
        session.AddTask(remaining);
        session.RemoveTasksWithUndo([removed.Id]);

        await Assert.ThrowsAsync<IOException>(() => session.RelocateSourcesAsync(remaining, Map(oldPath, replacement),
            (item, _) => Task.FromResult(new MediaImportItemResult(item.InputPath, null, new IOException("probe failed"))), _ => { }));

        Assert.True(session.HasRemovalUndoForTask(removed.Id));
        Assert.Empty(released);
        Assert.Equal(oldPath, remaining.InputPath);
        Assert.Equal(1, session.UndoLastAction());
        Assert.Equal([removed, remaining], session.Tasks);
        Assert.All(session.Tasks, task => Assert.Equal(2, task.OutputExpectedCount));
    }

    [Fact]
    public async Task UndoDuringRelocationProbe_RejectsChangedGroupWithoutPublishingPartialSources()
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var replacement = FileAt(directory, "new.mp4", 3);
        var removed = Model(oldPath, 3);
        var remaining = Model(oldPath, 3);
        removed.OutputGroupId = remaining.OutputGroupId = Guid.NewGuid();
        removed.OutputExpectedCount = remaining.OutputExpectedCount = 2;
        using var session = Session(new MemoryPersistence());
        session.AddTask(removed);
        session.AddTask(remaining);
        session.RemoveTasksWithUndo([removed.Id]);
        var temporaryReleased = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RelocateSourcesAsync(remaining, Map(oldPath, replacement),
            async (item, token) =>
            {
                var imported = await Import(item, token);
                Assert.Equal(1, session.UndoLastAction());
                return imported;
            }, _ => temporaryReleased++));

        Assert.Equal(1, temporaryReleased);
        Assert.Equal([removed, remaining], session.Tasks);
        Assert.All(session.Tasks, task =>
        {
            Assert.Equal(oldPath, task.InputPath);
            Assert.False(task.IsRecoveringOutput);
        });
    }

    [Fact]
    public void FolderMapping_PreservesInputOrder_AndUsesPersistedIndividualIdentity()
    {
        using var directory = new TemporaryDirectory();
        var oldDirectory = Path.Combine(directory.Path, "old");
        var newDirectory = Path.Combine(directory.Path, "new");
        var first = Path.Combine(oldDirectory, "b.mp4");
        var second = Path.Combine(oldDirectory, "a.mp4");
        var model = Model(first, 9);
        model.Inputs.Add(new(first, "mp4", new MediaProbeResult { FileSize = 3 }));
        model.Inputs.Add(new(second, "mp4", new MediaProbeResult { FileSize = 6 }));
        SourceRelocationService.CaptureIdentity(model);
        var restored = QueueSession.FromPersisted(QueueStore.ToPersistedItem(model));
        var replacementFirst = FileAt(directory, "new/b.mp4", 3);
        var replacementSecond = FileAt(directory, "new/a.mp4", 6);
        var request = SourceRelocationService.CreateRequest(restored,
            SourceRelocationService.MapFolder(restored, oldDirectory, newDirectory));
        Assert.Equal([replacementFirst, replacementSecond], request.Inputs.Select(input => input.Path));
        Assert.Equal(replacementFirst, request.InputPath);
        File.WriteAllBytes(replacementSecond, new byte[7]);
        Assert.Throws<SourceRelocationValidationException>(() => SourceRelocationService.CreateRequest(restored,
            SourceRelocationService.MapFolder(restored, oldDirectory, newDirectory)));
    }

    [Fact]
    public void IdentityValidation_RejectsWrongTypeSizeDuplicatesAndUnconfirmedUnknownIdentity()
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 3);
        var wrongType = FileAt(directory, "wrong.mkv", 3);
        var wrongSize = FileAt(directory, "wrong.mp4", 4);
        var correct = FileAt(directory, "correct.mp4", 3);
        Assert.Throws<SourceRelocationValidationException>(() => SourceRelocationService.CreateRequest(original, Map(original.InputPath, wrongType)));
        Assert.Throws<SourceRelocationValidationException>(() => SourceRelocationService.CreateRequest(original, Map(original.InputPath, wrongSize)));
        original.FileSize = 0;
        Assert.Throws<SourceRelocationValidationException>(() => SourceRelocationService.CreateRequest(original, Map(original.InputPath, correct)));
        Assert.Equal(correct, SourceRelocationService.CreateRequest(original, Map(original.InputPath, correct), requireKnownSize: false).InputPath);
        original.Inputs.Add(new(original.InputPath, "mp4"));
        var other = Path.Combine(directory.Path, "other.mp4");
        original.Inputs.Add(new(other, "mp4"));
        Assert.Throws<SourceRelocationValidationException>(() => SourceRelocationService.CreateRequest(original,
            new Dictionary<string, string> { [original.InputPath] = correct, [other] = correct }, requireKnownSize: false));
    }

    [Fact]
    public async Task AutomationAndPendingRestore_AreUnavailableAndCannotBeSilentlyReassigned()
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 3);
        original.Options["automation.ruleId"] = Guid.NewGuid().ToString();
        using var session = Session(new MemoryPersistence());
        session.AddTask(original);
        Assert.False(session.CanRelocateSource(original));
        Assert.Throws<SourceRelocationValidationException>(() => SourceRelocationService.CreateRequest(original,
            Map(original.InputPath, FileAt(directory, "new.mp4", 3))));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.Options.Clear();
        using var restoring = new QueueSession(new MemoryPersistence([QueueStore.ToPersistedItem(original)]), async (item, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(item.InputPath, null, new FileNotFoundException());
        }, (_, _, _) => throw new NotSupportedException(), (_, _) => Task.CompletedTask);
        var restore = restoring.RestoreAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(restoring.CanRelocateSource(Assert.Single(restoring.Tasks))); }
        finally { release.TrySetResult(); await restore; }
        Assert.True(restoring.CanRelocateSource(Assert.Single(restoring.Tasks)));
    }

    [Fact]
    public async Task CandidateRemovedDuringProbe_AndCancellation_LeaveOriginalUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 3);
        var replacement = FileAt(directory, "new.mp4", 3);
        using var session = Session(new MemoryPersistence());
        session.AddTask(original);
        var oldPath = original.InputPath;
        var releases = 0;
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.RelocateSourcesAsync(original, Map(oldPath, replacement),
            async (item, token) =>
            {
                var result = await Import(item, token);
                File.Delete(replacement);
                return result;
            }, _ => releases++));
        Assert.Equal(1, releases);
        Assert.Equal(oldPath, original.InputPath);
        Assert.False(original.IsRecoveringOutput);

        File.WriteAllBytes(replacement, new byte[3]);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RelocateSourcesAsync(original, Map(oldPath, replacement),
            async (item, token) =>
            {
                var result = await Import(item, token);
                cancellation.Cancel();
                return result;
            }, _ => releases++, cancellationToken: cancellation.Token));
        Assert.Equal(2, releases);
        Assert.Equal(oldPath, original.InputPath);
        Assert.False(original.IsRecoveringOutput);
    }

    [Fact]
    public async Task SaveFailure_IsReportedWithoutRevertingPublishedPathsOrLosingIdentity()
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 3);
        var replacement = FileAt(directory, "new.mp4", 3);
        using var session = Session(new MemoryPersistence { SaveError = new IOException("disk full") });
        session.AddTask(original);
        var result = await session.RelocateSourcesAsync(original, Map(original.InputPath, replacement), Import, _ => { });
        Assert.IsType<IOException>(result.SaveError);
        Assert.Equal(1, result.UpdatedTasks);
        Assert.Equal(replacement, original.InputPath);
        Assert.Equal(3, SourceRelocationService.KnownSize(original, 0));
        Assert.False(original.IsRecoveringOutput);
    }

    private static Task<MediaImportItemResult> Import(PersistedQueueItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var model = QueueSession.FromPersisted(item);
        model.Inputs.Clear();
        foreach (var input in item.Inputs)
            model.Inputs.Add(new(input.Path, input.FormatId,
                new MediaProbeResult { ContentKind = ContentKind.Video, FileSize = new FileInfo(input.Path).Length }, input.AccessToken));
        model.FileSize = model.Inputs.Sum(input => input.Probe!.FileSize);
        return Task.FromResult(new MediaImportItemResult(item.InputPath, model, null));
    }

    private static QueueSession Session(IQueuePersistence persistence, Action<MediaTask>? release = null) =>
        new(persistence, Import, (_, _, _) => throw new NotSupportedException(), (_, _) => Task.CompletedTask,
            diagnostics: _ => { }, releaseImport: release);

    private static MediaTask Model(string path, long size) => new()
    {
        InputPath = path, ContentKind = ContentKind.Video, SourceFormatId = "mp4",
        TargetFormat = "MP4", TargetFormatId = "mp4", State = ConversionState.Failed, FileSize = size
    };

    private static Dictionary<string, string> Map(string oldPath, string newPath) =>
        new(StringComparer.OrdinalIgnoreCase) { [oldPath] = newPath };

    private static string FileAt(TemporaryDirectory directory, string relative, int length)
    {
        var path = Path.GetFullPath(Path.Combine(directory.Path, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private sealed class MemoryPersistence(IReadOnlyList<PersistedQueueItem>? initial = null) : IQueuePersistence
    {
        public Exception? SaveError { get; init; }
        public PersistedQueueItem[] LastSaved { get; private set; } = [];
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, initial ?? []));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
        {
            if (SaveError is not null) throw SaveError;
            LastSaved = tasks.Select(QueueStore.ToPersistedItem).ToArray();
            return Task.CompletedTask;
        }
    }
}
