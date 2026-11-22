using System.Collections.Concurrent;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class WatchFolderImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingWatch_CancelsPendingDiscovery(bool dispose)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "waiting.docx");
        File.WriteAllText(path, "content");
        var backend = new TestDirectoryChangeBackend();
        var native = new NativeDirectoryWatcher(backend);
        using var watcher = new WatchFolderService(native);
        var discovered = new ConcurrentQueue<string>();
        watcher.FileDiscovered += (_, value) => discovered.Enqueue(value);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Changed += (_, _) => queued.TrySetResult();
        watcher.Start(directory.Path);
        backend.Publish("waiting.docx");
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (dispose)
        {
            watcher.Dispose();
        }
        else
        {
            watcher.Stop();
        }
        await Task.Delay(1300);

        Assert.False(watcher.IsWatching);
        Assert.Empty(discovered);
    }

    [Fact]
    public async Task SwitchingWatch_DiscardsOldPendingAndDelayedEvents()
    {
        using var oldDirectory = new TemporaryDirectory();
        using var newDirectory = new TemporaryDirectory();
        var oldPath = Path.Combine(oldDirectory.Path, "old.docx");
        var newPath = Path.Combine(newDirectory.Path, "new.docx");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(newPath, "new");
        var backend = new TestDirectoryChangeBackend();
        var native = new NativeDirectoryWatcher(backend);
        using var watcher = new WatchFolderService(native);
        var discovered = new ConcurrentQueue<string>();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileDiscovered += (_, value) =>
        {
            discovered.Enqueue(value);
            completion.TrySetResult(value);
        };
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Changed += (_, _) => queued.TrySetResult();
        watcher.Start(oldDirectory.Path);
        backend.Publish("old.docx");
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        watcher.Start(newDirectory.Path + Path.DirectorySeparatorChar);
        backend.Publish(Path.GetRelativePath(newDirectory.Path, oldPath));
        backend.Publish("new.docx");
        Assert.Equal(newPath, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(300);

        Assert.Equal([newPath], discovered);
    }

    [Theory]
    [InlineData("docx", ContentKind.Document)]
    [InlineData("iso", ContentKind.Disc)]
    public async Task WatchImport_ImportsNonMediaWithoutFfprobe(string extension, ContentKind expectedKind)
    {
        using var directory = new TemporaryDirectory();
        var fileName = "input." + extension;
        var path = Path.Combine(directory.Path, fileName);
        File.WriteAllText(path, "content");
        var backend = new TestDirectoryChangeBackend();
        var service = new WatchFolderService(new NativeDirectoryWatcher(backend));
        using var coordinator = new WatchFolderImportCoordinator(new MediaImportCoordinator(), service);
        var completion = new TaskCompletionSource<MediaImportBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ImportCompleted += (_, result) => completion.TrySetResult(result);
        coordinator.Configure(new AppSettings
        {
            WatchFolderEnabled = true,
            WatchFolderPath = directory.Path
        }, static () => string.Empty);

        backend.Publish(fileName);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(result.Failures);
        var task = Assert.Single(result.AddedTasks);
        Assert.Equal(path, task.InputPath);
        Assert.Equal(expectedKind, task.ContentKind);
        Assert.Equal(directory.Path, task.SourceRoot);
    }

    [Theory]
    [InlineData(FileShare.ReadWrite, 1300)]
    [InlineData(FileShare.None, 11000)]
    public async Task WatchImport_WaitsForWriterToCloseEvenWhenCopyPauses(
        FileShare sharing,
        int copyPauseMilliseconds)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "copying.docx");
        var backend = new TestDirectoryChangeBackend();
        var native = new NativeDirectoryWatcher(backend);
        using var watcher = new WatchFolderService(native);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileDiscovered += (_, value) => completion.TrySetResult(value);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Changed += (_, _) => queued.TrySetResult();
        watcher.Start(directory.Path);

        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, sharing))
        {
            writer.Write([1, 2, 3]);
            writer.Flush();
            backend.Publish("copying.docx");
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(copyPauseMilliseconds);

            Assert.False(completion.Task.IsCompleted);
            writer.Write([4, 5, 6]);
        }

        Assert.Equal(path, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact]
    public async Task DeletedPendingFile_ReleasesReservationSoRecreatedFileCanBeDiscovered()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "recreated.docx");
        File.WriteAllBytes(path, []);
        var backend = new TestDirectoryChangeBackend();
        var native = new NativeDirectoryWatcher(backend);
        using var watcher = new WatchFolderService(native);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileDiscovered += (_, value) => completion.TrySetResult(value);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Changed += (_, _) => queued.TrySetResult();
        watcher.Start(directory.Path);
        backend.Publish("recreated.docx");
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(650);

        File.Delete(path);
        await Task.Delay(650);
        Assert.False(completion.Task.IsCompleted);
        File.WriteAllText(path, "completed replacement");
        backend.Publish("recreated.docx");

        Assert.Equal(path, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
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
