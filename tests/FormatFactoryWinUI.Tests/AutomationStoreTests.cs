using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationStoreTests
{
    [Fact]
    public async Task RulesAndSuccessfulFingerprints_RoundTripWithPriorityAndScheduleCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "automation.json");
        var first = new AutomationRule
        {
            Name = "First route", FolderPath = directory.Path,
            OutputDirectory = Path.Combine(directory.Path, "output"),
            ArchiveDirectory = Path.Combine(directory.Path, "archive"),
            WatchEnabled = false, ScheduleEnabled = true, ScheduleTime = "07:45",
            Extensions = ".mp4;.mkv", FileNamePattern = "episode-?*",
            TargetFormatId = "mp3", ConversionMode = "quality", InputKind = ContentKind.Video,
            ArchiveSource = true
        };
        var second = first.Snapshot();
        second.Id = Guid.NewGuid();
        second.Name = "Second route";
        second.Enabled = false;
        var source = Path.Combine(directory.Path, "episode-1.mp4");
        File.WriteAllText(source, "source");
        var fingerprint = FileFingerprintService.Read(source)!;
        var output = Path.Combine(directory.Path, "converted.mp3");
        File.WriteAllText(output, "output");
        var record = new AutomationProcessedFile(first.Id, source, fingerprint, output,
            FileFingerprintService.Read(output), DateTimeOffset.UtcNow);
        var store = new AutomationStore(path);

        await store.SaveRulesAsync([first, second]);
        await store.RecordSuccessAsync(record);
        await store.MarkScheduledRunAsync(first.Id, "2026-10-03");
        var restored = await new AutomationStore(path).LoadAsync();

        Assert.Equal([first.Id, second.Id], restored.Rules.Select(rule => rule.Id));
        var rule = restored.Rules[0];
        Assert.Equal(first.Name, rule.Name);
        Assert.Equal(first.OutputDirectory, rule.OutputDirectory);
        Assert.Equal(first.ArchiveDirectory, rule.ArchiveDirectory);
        Assert.Equal(first.Extensions, rule.Extensions);
        Assert.Equal(first.FileNamePattern, rule.FileNamePattern);
        Assert.Equal("quality", rule.ConversionMode);
        Assert.Equal("mp3", rule.TargetFormatId);
        Assert.Equal(ContentKind.Video, rule.InputKind);
        Assert.True(rule.ArchiveSource);
        Assert.False(rule.WatchEnabled);
        Assert.True(rule.ScheduleEnabled);
        Assert.Equal("07:45", rule.ScheduleTime);
        Assert.False(restored.Rules[1].Enabled);
        Assert.Equal(record, Assert.Single(restored.Records));
        Assert.Equal("2026-10-03", restored.ScheduleRuns[first.Id]);
    }

    [Fact]
    public async Task EditingRules_PreservesSuccessfulFingerprintAndScheduleCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "automation.json");
        var rule = new AutomationRule
        {
            Name = "Before", FolderPath = directory.Path,
            OutputDirectory = Path.Combine(directory.Path, "output")
        };
        var source = Path.Combine(directory.Path, "source.mp4");
        File.WriteAllText(source, "source");
        var record = new AutomationProcessedFile(rule.Id, source, FileFingerprintService.Read(source)!,
            Path.Combine(directory.Path, "converted.mp4"), null, DateTimeOffset.UtcNow);
        var store = new AutomationStore(path);
        await store.SaveRulesAsync([rule]);
        await store.RecordSuccessAsync(record);
        await store.MarkScheduledRunAsync(rule.Id, "2026-10-03");

        rule.Name = "After";
        await store.SaveRulesAsync([rule]);
        var restored = await new AutomationStore(path).LoadAsync();

        Assert.Equal("After", Assert.Single(restored.Rules).Name);
        Assert.Equal(record, Assert.Single(restored.Records));
        Assert.Equal("2026-10-03", restored.ScheduleRuns[rule.Id]);
    }

    [Theory]
    [InlineData("{broken automation bytes")]
    [InlineData("{\"Version\":99,\"Rules\":[],\"Records\":[],\"ScheduleRuns\":{}}")]
    [InlineData("{\"version\":99,\"rules\":[],\"records\":[],\"scheduleRuns\":{}}")]
    [InlineData("{}")]
    [InlineData("{\"Rules\":[]}")]
    public async Task UnreadableDocument_CannotBeSilentlyReplacedBySavingRules(string original)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "automation.json");
        await File.WriteAllTextAsync(path, original);
        var store = new AutomationStore(path);

        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveRulesAsync([]));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveWithoutLoading_CannotOverwriteCorruptExistingDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "automation.json");
        const string original = "[incomplete automation document";
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAnyAsync<Exception>(() => new AutomationStore(path).SaveRulesAsync([]));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LoadedRuleOptions_AreIsolatedFromOtherSnapshots()
    {
        using var directory = new TemporaryDirectory();
        var store = new AutomationStore(Path.Combine(directory.Path, "automation.json"));
        var rule = new AutomationRule
        {
            Name = "Parameters", FolderPath = directory.Path,
            OutputDirectory = Path.Combine(directory.Path, "output")
        };
        rule.Options["profile"] = "balanced";
        await store.SaveRulesAsync([rule]);

        var loaded = await store.LoadAsync();
        loaded.Rules[0].Options["profile"] = "changed";
        loaded.Rules[0].Name = "Changed";
        var reread = await store.LoadAsync();

        Assert.Equal("balanced", Assert.Single(reread.Rules).Options["profile"]);
        Assert.Equal("Parameters", reread.Rules[0].Name);
    }
}
