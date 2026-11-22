using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class ConversionTargetRecoveryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptySelectorWriteBack_PreservesSelectedConversionRoute(string? selection)
    {
        var task = new MediaTask { InputPath = "input.png", ContentKind = ContentKind.Image, TargetFormat = "PDF" };
        using var viewModel = new MediaTaskViewModel(task);
        var routeChanges = 0;
        task.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MediaTask.TargetFormat) or nameof(MediaTask.TargetFormatId)) routeChanges++;
        };

        viewModel.TargetFormat = selection!;

        Assert.Equal("PDF", task.TargetFormat);
        Assert.Equal("pdf", task.TargetFormatId);
        Assert.Equal("document-pipeline", task.EngineId);
        Assert.Equal(0, routeChanges);
        ConversionRequestFactory.Capture(task, new()).Validate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoringEmptyTarget_UsesProbedTargetAndPersistsRepairBeforeRetry(bool initiallyUnavailable)
    {
        var item = new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(), InputPath = "input.png", SourceFormatId = "png",
            ContentKind = ContentKind.Image, TargetFormat = "", TargetFormatId = "",
            State = ConversionState.Failed, LastError = "Previous conversion failed"
        };
        var persistence = new Persistence(item);
        var imports = 0;
        using var session = new QueueSession(persistence, (persisted, _) =>
        {
            if (++imports == 1 && initiallyUnavailable)
                return Task.FromResult(new MediaImportItemResult(persisted.InputPath, null, new IOException("Synthetic probe failure")));
            var imported = new MediaTask
            {
                Id = persisted.TaskId, InputPath = persisted.InputPath, SourceFormatId = "png",
                ContentProbe = new() { ContentKind = ContentKind.Image, FormatId = "png" }, TargetFormat = "PNG"
            };
            imported.AvailableFormats.Add("PNG");
            return Task.FromResult(new MediaImportItemResult(persisted.InputPath, imported, null));
        }, (task, _, _) =>
        {
            var request = ConversionRequestFactory.Capture(task, new());
            request.Validate();
            Assert.Equal("png", request.TargetFormatId);
            return Task.FromResult(new ConversionWorkflowResult(ConversionOutcome.Completed,
                new(true, false, [new("output", "png", "result.png")], "Done"), "ffmpeg"));
        }, (_, _) => Task.CompletedTask);

        await session.RestoreAsync();

        var task = Assert.Single(session.Tasks);
        Assert.DoesNotContain(task.AvailableFormats, string.IsNullOrWhiteSpace);
        if (initiallyUnavailable) Assert.Equal(ConversionState.Blocked, task.State);
        else
        {
            Assert.Equal("png", task.TargetFormatId);
            Assert.Equal("png", Assert.Single(persistence.Saved).TargetFormatId);
        }
        Assert.True(session.TryStart(task));
        await session.Coordinator.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConversionState.Completed, task.State);
        Assert.Equal("png", task.TargetFormatId);
        Assert.Equal("png", Assert.Single(persistence.Saved).TargetFormatId);
    }

    [Fact]
    public async Task RebindingFormatsDuringRestore_PreservesExistingNonDefaultRoute()
    {
        var item = new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(), InputPath = "input.png", SourceFormatId = "png", ContentKind = ContentKind.Image,
            TargetFormat = "PDF", TargetFormatId = "pdf", EngineId = "document-pipeline"
        };
        using var session = new QueueSession(new Persistence(item), (persisted, _) =>
        {
            var imported = new MediaTask { InputPath = persisted.InputPath, SourceFormatId = "png",
                ContentProbe = new() { ContentKind = ContentKind.Image, FormatId = "png" }, TargetFormat = "PNG" };
            imported.AvailableFormats.Add("PNG");
            imported.AvailableFormats.Add("PDF");
            return Task.FromResult(new MediaImportItemResult(persisted.InputPath, imported, null));
        }, (_, _, _) => throw new InvalidOperationException("Restore must not execute conversions."), (_, _) => Task.CompletedTask);
        MediaTaskViewModel? viewModel = null;
        var resets = 0;
        session.TaskAdded += task =>
        {
            viewModel = new MediaTaskViewModel(task);
            task.AvailableFormats.CollectionChanged += (_, args) =>
            {
                if (args.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset) return;
                resets++;
                viewModel.TargetFormat = null!;
            };
        };
        try
        {
            await session.RestoreAsync();
            var task = Assert.Single(session.Tasks);
            Assert.Equal(1, resets);
            Assert.Equal("PDF", task.TargetFormat);
            Assert.Equal("pdf", task.TargetFormatId);
            Assert.Equal("document-pipeline", task.EngineId);
            Assert.Contains("PDF", task.AvailableFormats);
            ConversionRequestFactory.Capture(task, new()).Validate();
        }
        finally { viewModel?.Dispose(); }
    }

    private sealed class Persistence(PersistedQueueItem item) : IQueuePersistence
    {
        public PersistedQueueItem[] Saved { get; private set; } = [];
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, [item]));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
        {
            Saved = tasks.Select(QueueStore.ToPersistedItem).ToArray();
            return Task.CompletedTask;
        }
    }
}
