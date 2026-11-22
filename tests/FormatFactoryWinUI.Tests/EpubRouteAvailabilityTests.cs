using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class EpubRouteAvailabilityTests
{
    [Fact]
    public async Task NewEpubImport_SelectsAnImplementedRouteWithoutFfprobe()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateEpubAsync(directory.Path);

        var result = await new MediaImportCoordinator().ImportPathsAsync(string.Empty, [source]);

        Assert.Empty(result.Failures);
        var task = Assert.Single(result.AddedTasks);
        Assert.Equal(ContentKind.Ebook, task.ContentKind);
        Assert.Equal("epub", task.SourceFormatId);
        Assert.Equal("mobi", task.TargetFormatId);
        Assert.Equal(["MOBI", "AZW3"], task.AvailableFormats);
        Assert.All(task.AvailableFormats, target =>
            Assert.True(new DocumentConversionRoutePlanner().CanPlan(task.SourceFormatId, target)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistedSameFormatEpub_RetainsItsRequestedTargetForTheUiToBlock(bool usesStableId)
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateEpubAsync(directory.Path);
        var persisted = new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(), InputPath = source, SourceFormatId = "epub", ContentKind = ContentKind.Ebook,
            TargetFormatId = usesStableId ? "epub" : string.Empty,
            TargetFormat = usesStableId ? string.Empty : "EPUB",
            State = ConversionState.Waiting
        };

        var result = await new MediaImportCoordinator().ImportPersistedAsync(string.Empty, persisted);

        Assert.Null(result.Error);
        var task = Assert.IsType<MediaTask>(result.Task);
        Assert.Equal(persisted.TaskId, task.Id);
        Assert.Equal("epub", task.TargetFormatId);
        Assert.Contains("EPUB", task.AvailableFormats);
        Assert.Contains("MOBI", task.AvailableFormats);
        Assert.Contains("AZW3", task.AvailableFormats);
        Assert.False(new DocumentConversionRoutePlanner().CanPlan(task.SourceFormatId, task.TargetFormatId));
    }

    [Theory]
    [InlineData("", "mobi")]
    [InlineData("mobi", "mobi")]
    [InlineData("azw3", "azw3")]
    public async Task PersistedEpub_RetainsSupportedTargetsAndRecoversAnEmptyTarget(string target, string expected)
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateEpubAsync(directory.Path);
        var persisted = new PersistedQueueItem
        {
            TaskId = Guid.NewGuid(), InputPath = source, SourceFormatId = "epub", ContentKind = ContentKind.Ebook,
            TargetFormatId = target, State = ConversionState.Waiting
        };

        var result = await new MediaImportCoordinator().ImportPersistedAsync(string.Empty, persisted);

        Assert.Null(result.Error);
        var task = Assert.IsType<MediaTask>(result.Task);
        Assert.Equal(expected, task.TargetFormatId);
        Assert.DoesNotContain("EPUB", task.AvailableFormats);
        Assert.True(new DocumentConversionRoutePlanner().CanPlan(task.SourceFormatId, task.TargetFormatId));
    }

    private static async Task<string> CreateEpubAsync(string directory)
    {
        var path = Path.Combine(directory, "synthetic-book.epub");
        var document = new NormalizedDocument("Route fixture", "en",
            [new NormalizedSection(null, [new NormalizedParagraph("Synthetic EPUB route regression.")])], []);
        var publication = new NormalizedDocumentEpubAdapter().CreatePublication(document, "urn:test:epub-route");
        await new Epub3Writer().WriteAsync(publication, path);
        var validation = await new EpubStructureValidator().ValidateAsync(path);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        return path;
    }
}
