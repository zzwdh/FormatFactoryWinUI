using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class StoreUpdateSettingsTests
{
    [Fact]
    public async Task LegacySettingsEnableUpdatesAndOptOutSurvivesReload()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"Theme\":\"dark\"}");
        var service = new SettingsService(path);
        var settings = await service.LoadAsync();
        Assert.True(settings.EnableAutomaticUpdates);

        settings.EnableAutomaticUpdates = false;
        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();
        Assert.False(loaded.EnableAutomaticUpdates);
        Assert.Equal("dark", loaded.Theme);
    }

    [Fact]
    public async Task EarlierConversionDraftDoesNotUndoUpdateOptOut()
    {
        var settings = new AppSettings();
        var draft = SettingsDraft.From(settings) with { ParallelCount = 4 };
        AppSettings? persisted = null;
        var commits = new SettingsCommitCoordinator(
            candidate => { persisted = candidate.Snapshot(); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);

        await commits.UpdateAsync(settings, candidate => candidate.EnableAutomaticUpdates = false);
        await commits.CommitDraftAsync(settings, draft, _ => Task.CompletedTask);

        Assert.False(settings.EnableAutomaticUpdates);
        Assert.False(persisted!.EnableAutomaticUpdates);
        Assert.Equal(4, settings.ParallelCount);
    }
}
