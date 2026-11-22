using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class ArchiveHistoryReplayTests
{
    [Theory]
    [InlineData("zip", ArchiveFormat.Zip)]
    [InlineData("7z", ArchiveFormat.SevenZip)]
    [InlineData("tar", ArchiveFormat.Tar)]
    [InlineData("tar.gz", ArchiveFormat.TarGZip)]
    [InlineData("tar.xz", ArchiveFormat.TarXz)]
    public void ReplayRetainsArchiveFormatAndStrictVerification(string format, ArchiveFormat expected)
    {
        var item = Item(format);
        item.Options[ArchiveHistoryReplayService.StrictVerificationKey] = "true";

        var (options, encrypted) = ArchiveHistoryReplayService.ReadOptions(item);

        Assert.Equal(expected, options.TargetFormat);
        Assert.True(options.StrictVerification);
        Assert.Null(options.OutputPassword);
        Assert.False(encrypted);
        Assert.Contains(ArchiveFormats.DisplayName(expected), HistoryPageViewModel.DescribeParameters(item));
    }

    [Fact]
    public async Task HistoryKeepsEncryptionIntentAndNeverStoresPasswords()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "result.7z");
        await File.WriteAllTextAsync(output, "Synthetic completed archive for history metadata testing.");
        var item = Item("7z") with { OutputPath = output };
        item.Options[ArchiveHistoryReplayService.EncryptOutputKey] = "true";
        item.Options[ArchiveHistoryReplayService.StrictVerificationKey] = "true";
        item.Options["archive.outputPassword"] = "synthetic-secret-do-not-persist";
        item.Options["sourcePassword"] = "synthetic-source-secret";
        var path = Path.Combine(directory.Path, "history.json");
        var store = new ConversionHistoryStore(path);

        await store.RecordCompletedAsync(item);
        await store.RecordCompletedAsync(item);
        var saved = Assert.Single(await store.LoadAsync());
        var (options, encrypted) = ArchiveHistoryReplayService.ReadOptions(saved);

        Assert.True(encrypted);
        Assert.True(options.StrictVerification);
        Assert.Null(options.OutputPassword);
        Assert.Equal(ContentKind.Archive, saved.ContentKind);
        Assert.DoesNotContain("synthetic-secret", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("synthetic-source-secret", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("tar", "true")]
    [InlineData("tar.gz", "true")]
    [InlineData("7z", "not-a-boolean")]
    public void InvalidEncryptionChoicesNeverReplayAsAnUnencryptedOutput(string format, string flag)
    {
        var item = Item(format);
        item.Options[ArchiveHistoryReplayService.EncryptOutputKey] = flag;
        Assert.Throws<InvalidDataException>(() => ArchiveHistoryReplayService.ReadOptions(item));
    }

    [Fact]
    public void ArchiveHistoryIsRoutedSeparatelyFromMediaAndUnknownFormatsFailClearly()
    {
        Assert.True(ArchiveHistoryReplayService.IsArchive(Item("zip")));
        Assert.False(ArchiveHistoryReplayService.IsArchive(new() { ContentKind = ContentKind.Video, EngineId = "ffmpeg" }));
        Assert.Throws<InvalidDataException>(() => ArchiveHistoryReplayService.ReadOptions(Item("rar")));
    }

    private static ConversionHistoryItem Item(string format) => new()
    {
        TaskId = Guid.NewGuid(), ContentKind = ContentKind.Archive, EngineId = "archive",
        TargetFormatId = format, SnapshotVersion = 1, CompletedAtUtc = DateTimeOffset.UtcNow
    };
}
