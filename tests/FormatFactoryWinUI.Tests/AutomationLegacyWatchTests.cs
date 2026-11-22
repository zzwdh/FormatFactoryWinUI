using System.Collections.Concurrent;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationLegacyWatchTests
{
    [Fact]
    public async Task LegacyWatch_RejectsRuleOwnedFileBeforeProbeAndStillImportsUnmatchedFile()
    {
        using var directory = new TemporaryDirectory();
        var rule = new AutomationRule
        {
            Name = "Custom videos", FolderPath = directory.Path,
            FileNamePattern = "custom-*", OutputDirectory = Path.Combine(directory.Path, "output")
        };
        var custom = Path.Combine(directory.Path, "custom-video.mp4");
        var ordinary = Path.Combine(directory.Path, "ordinary.mp4");
        File.WriteAllText(custom, "custom video");
        File.WriteAllText(ordinary, "ordinary video");
        var backend = new TestDirectoryChangeBackend();
        var watcher = new WatchFolderService(new NativeDirectoryWatcher(backend));
        var probed = new ConcurrentQueue<string>();
        var importer = new MediaImportCoordinator((_, input, _) =>
        {
            probed.Enqueue(input);
            return Task.FromResult(new MediaProbeResult { ContentKind = ContentKind.Video, FormatId = "mp4" });
        });
        using var coordinator = new WatchFolderImportCoordinator(importer, watcher);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imported = new TaskCompletionSource<MediaImportBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ImportCompleted += (_, result) => imported.TrySetResult(result);
        coordinator.Configure(new AppSettings { WatchFolderEnabled = true, WatchFolderPath = directory.Path },
            static () => "fake-ffprobe", input =>
            {
                var shouldImport = AutomationRuleMatcher.FirstMatch([rule], input) is null &&
                    !AutomationRuleMatcher.IsExcludedByDestinations(input, [rule]);
                if (!shouldImport) rejected.TrySetResult();
                return shouldImport;
            });

        backend.Publish("custom-video.mp4");
        backend.Publish("ordinary.mp4");
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await imported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(result.Failures);
        Assert.Equal(ordinary, Assert.Single(result.AddedTasks).InputPath);
        Assert.Equal([ordinary], probed);
    }

    [Fact]
    public async Task LegacyWatch_RejectsAutomationOutputBeforeProbeOrImport()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "input");
        var output = Path.Combine(directory.Path, "output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        var rule = new AutomationRule { Name = "Output exclusion", FolderPath = source, OutputDirectory = output };
        File.WriteAllText(Path.Combine(output, "converted.mp4"), "converted video");
        var backend = new TestDirectoryChangeBackend();
        var watcher = new WatchFolderService(new NativeDirectoryWatcher(backend));
        var probed = 0;
        var published = 0;
        var importer = new MediaImportCoordinator((_, _, _) =>
        {
            Interlocked.Increment(ref probed);
            return Task.FromResult(new MediaProbeResult { ContentKind = ContentKind.Video, FormatId = "mp4" });
        });
        using var coordinator = new WatchFolderImportCoordinator(importer, watcher);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ImportCompleted += (_, _) => Interlocked.Increment(ref published);
        coordinator.Configure(new AppSettings { WatchFolderEnabled = true, WatchFolderPath = output },
            static () => "fake-ffprobe", input =>
            {
                var shouldImport = !AutomationRuleMatcher.IsExcludedByDestinations(input, [rule]);
                if (!shouldImport) rejected.TrySetResult();
                return shouldImport;
            });

        backend.Publish("converted.mp4");
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Volatile.Read(ref probed));
        Assert.Equal(0, Volatile.Read(ref published));
    }

    private sealed class TestDirectoryChangeBackend : IDirectoryChangeBackend
    {
        private readonly ConcurrentQueue<IReadOnlyList<NativeDirectoryChange>> _changes = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void Open(string folderPath, bool includeSubdirectories) { }

        public IReadOnlyList<NativeDirectoryChange> Read(CancellationToken cancellationToken)
        {
            _signal.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return _changes.TryDequeue(out var changes) ? changes : [];
        }

        public void Publish(string relativePath)
        {
            _changes.Enqueue([new NativeDirectoryChange(NativeDirectoryChangeAction.Added, relativePath)]);
            _signal.Release();
        }

        public void Cancel() => _signal.Release();
        public void Close() { }
        public void Dispose() => _signal.Dispose();
    }
}
