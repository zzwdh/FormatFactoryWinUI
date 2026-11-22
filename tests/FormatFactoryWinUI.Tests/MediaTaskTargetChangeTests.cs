using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaTaskTargetChangeTests
{
    [Fact]
    public async Task SavingFromFormatChangeNotification_PersistsNewRoute()
    {
        using var directory = new TemporaryDirectory();
        var store = new QueueStore(Path.Combine(directory.Path, "queue.json"));
        var task = new MediaTask
        {
            InputPath = Path.Combine(directory.Path, "page.png"),
            ContentKind = ContentKind.Image,
            SourceFormatId = "png",
            TargetFormat = "PNG"
        };
        using var viewModel = new MediaTaskViewModel(task);
        Task? save = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MediaTaskViewModel.TargetFormat))
                save = store.SaveAsync([task]);
        };

        viewModel.TargetFormat = "PDF";

        Assert.NotNull(save);
        await save;
        var restored = Assert.Single(await store.LoadAsync());
        Assert.Equal("pdf", restored.TargetFormatId);
        Assert.Equal("document-pipeline", restored.EngineId);
    }

    [Theory]
    [InlineData(false, ContentKind.Image, "png", "pdf", "document-pipeline")]
    [InlineData(true, ContentKind.Image, "pdf", "png", "ffmpeg")]
    [InlineData(true, ContentKind.Disc, "iso", "cso", "disc-pipeline")]
    public void RouteObservers_AlwaysSeeConsistentFormatAndEngine(
        bool useId, ContentKind kind, string initial, string target, string engine)
    {
        var task = new MediaTask { ContentKind = kind, TargetFormatId = initial };
        var observations = new List<(string Format, string Id, string Engine)>();
        task.PropertyChanged += (_, _) =>
            observations.Add((task.TargetFormat, task.TargetFormatId, task.EngineId));

        var displayName = FormatCatalog.FindById(target)!.DisplayName;
        if (useId) task.TargetFormatId = target;
        else task.TargetFormat = displayName;

        Assert.NotEmpty(observations);
        Assert.All(observations, value => Assert.Equal((displayName, target, engine), value));
    }
}
