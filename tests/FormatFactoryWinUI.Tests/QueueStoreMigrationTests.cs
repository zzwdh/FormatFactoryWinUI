using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class QueueStoreMigrationTests
{
    [Fact]
    public async Task LoadAsync_MigratesLegacyMediaArrayWithoutDroppingTask()
    {
        using var directory = new TemporaryDirectory();
        var inputPath = Path.Combine(directory.Path, "旧视频.mp4");
        var queuePath = Path.Combine(directory.Path, "pending-queue.json");
        await File.WriteAllTextAsync(
            queuePath,
            JsonSerializer.Serialize(new[]
            {
                new
                {
                    InputPath = inputPath,
                    TargetFormat = "MP3",
                    ConversionMode = "高质量",
                    SelectedAudioTrack = 2
                }
            }));

        var items = await new QueueStore(queuePath).LoadAsync();

        var item = Assert.Single(items);
        Assert.Equal(inputPath, item.InputPath);
        Assert.Equal("mp4", item.SourceFormatId);
        Assert.Equal(ContentKind.Video, item.ContentKind);
        Assert.Equal("mp3", item.TargetFormatId);
        Assert.Equal("ffmpeg", item.EngineId);
        Assert.Equal("quality", item.ConversionMode);
        Assert.Equal(1, item.ParametersVersion);
        Assert.Equal(inputPath, Assert.Single(item.Inputs).Path);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesOrderedInputsAndV2RouteFields()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "第1页.png");
        var second = Path.Combine(directory.Path, "第2页.webp");
        var task = new MediaTask
        {
            InputPath = first,
            ContentKind = ContentKind.Image,
            SourceFormatId = "png",
            TargetFormatId = "epub",
            ConversionMode = "quality",
            ParametersVersion = 3
        };
        task.Inputs.Add(new ConversionInput(first, "png", AccessToken: "token-1"));
        task.Inputs.Add(new ConversionInput(second, "webp", AccessToken: "token-2"));
        task.Options["layout"] = "fixed";
        task.Options["password"] = "not-persisted";

        var queuePath = Path.Combine(directory.Path, "pending-queue.json");
        var store = new QueueStore(queuePath);
        await store.SaveAsync([task]);
        var item = Assert.Single(await new QueueStore(queuePath).LoadAsync());

        Assert.Equal(task.Id, item.TaskId);
        Assert.Equal(["png", "webp"], item.Inputs.Select(input => input.FormatId));
        Assert.Equal([first, second], item.Inputs.Select(input => input.Path));
        Assert.Equal(["token-1", "token-2"], item.Inputs.Select(input => input.AccessToken));
        Assert.Equal("epub", item.TargetFormatId);
        Assert.Equal("document-pipeline", item.EngineId);
        Assert.Equal("quality", item.ConversionMode);
        Assert.Equal(3, item.ParametersVersion);
        Assert.Equal("fixed", item.Options["layout"]);
        Assert.DoesNotContain("password", item.Options.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergePendingAsync_NormalizesBackgroundItemsBeforeWritingV2()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "扫描件.pdf");
        var queuePath = Path.Combine(directory.Path, "pending-queue.json");
        var store = new QueueStore(queuePath);

        await store.MergePendingAsync(
        [
            new PersistedQueueItem
            {
                InputPath = input,
                TargetFormat = "EPUB"
            }
        ]);

        var item = Assert.Single(await new QueueStore(queuePath).LoadAsync());
        Assert.Equal("pdf", item.SourceFormatId);
        Assert.Equal(ContentKind.Document, item.ContentKind);
        Assert.Equal("epub", item.TargetFormatId);
        Assert.Equal("document-pipeline", item.EngineId);
        Assert.NotEqual(Guid.Empty, item.TaskId);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesPhysicalDiscRouteWithoutChangingToFfmpeg()
    {
        using var directory = new TemporaryDirectory();
        var queuePath = Path.Combine(directory.Path, "pending-queue.json");
        var task = new MediaTask
        {
            InputPath = @"E:\",
            ContentKind = ContentKind.Disc,
            SourceFormatId = OpticalDiscFormatIds.AudioCd,
            TargetFormat = "MP3",
            EngineId = "disc-pipeline"
        };
        task.Inputs.Add(new ConversionInput(
            task.InputPath,
            OpticalDiscFormatIds.AudioCd));

        await new QueueStore(queuePath).SaveAsync([task]);
        var item = Assert.Single(await new QueueStore(queuePath).LoadAsync());

        Assert.Equal(ContentKind.Disc, item.ContentKind);
        Assert.Equal(OpticalDiscFormatIds.AudioCd, item.SourceFormatId);
        Assert.Equal("mp3", item.TargetFormatId);
        Assert.Equal("disc-pipeline", item.EngineId);
    }
}
