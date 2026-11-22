using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class TelemetrySettingsTests
{
    [Fact]
    public async Task LegacySettingsWithoutDiagnosticPreferencesUseEnabledDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"Theme\":\"dark\"}");

        var settings = await new SettingsService(path).LoadAsync();

        Assert.True(settings.EnableUsageTelemetry);
        Assert.True(settings.EnableAutomaticErrorReporting);
        Assert.Equal("dark", settings.Theme);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task DiagnosticPreferencesPersistIndependently(bool usage, bool errors)
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(Path.Combine(directory.Path, "settings.json"));
        var settings = new AppSettings
        {
            EnableUsageTelemetry = usage,
            EnableAutomaticErrorReporting = errors
        };

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        Assert.Equal(usage, loaded.EnableUsageTelemetry);
        Assert.Equal(errors, loaded.EnableAutomaticErrorReporting);
    }

    [Fact]
    public async Task SavingEarlierDraftPreservesLatestImmediateDiagnosticPreferences()
    {
        var settings = new AppSettings();
        var state = new SettingsPageViewModel();
        state.InitializeDraft(settings);
        var captured = state.DraftSession!.Current with { ParallelCount = 4 };
        state.Edit(captured);
        AppSettings? persisted = null;
        var coordinator = new SettingsCommitCoordinator(
            candidate => { persisted = candidate.Snapshot(); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);

        await coordinator.UpdateAsync(settings, candidate => candidate.EnableUsageTelemetry = false);
        await coordinator.UpdateAsync(settings, candidate => candidate.EnableAutomaticErrorReporting = false);
        await coordinator.CommitDraftAsync(settings, captured, _ => Task.CompletedTask);

        Assert.False(settings.EnableUsageTelemetry);
        Assert.False(settings.EnableAutomaticErrorReporting);
        Assert.False(persisted!.EnableUsageTelemetry);
        Assert.False(persisted.EnableAutomaticErrorReporting);
        Assert.Equal(4, settings.ParallelCount);
        Assert.Equal(captured, state.DraftSession.Current);
    }

    [Fact]
    public async Task FailedImmediatePreferenceSaveDoesNotPublishOrDiscardDraftEdits()
    {
        var settings = new AppSettings();
        var state = new SettingsPageViewModel();
        state.InitializeDraft(settings);
        state.Edit(state.DraftSession!.Current with { ParallelCount = 4 });
        var coordinator = new SettingsCommitCoordinator(
            _ => throw new IOException("disk"), (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => coordinator.UpdateAsync(
            settings, candidate => candidate.EnableUsageTelemetry = false));

        Assert.True(settings.EnableUsageTelemetry);
        Assert.True(settings.EnableAutomaticErrorReporting);
        Assert.Equal(4, state.DraftSession.Current.ParallelCount);
        Assert.True(state.DraftSession.IsDirty);
    }

    [Fact]
    public async Task ResetRestoresBothDiagnosticDefaultsInRuntimeAndPersistence()
    {
        var settings = new AppSettings
        {
            EnableUsageTelemetry = false,
            EnableAutomaticErrorReporting = false
        };
        AppSettings? persisted = null;
        var coordinator = new SettingsCommitCoordinator(
            candidate => { persisted = candidate.Snapshot(); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);

        await coordinator.ResetAsync(settings);

        Assert.True(settings.EnableUsageTelemetry);
        Assert.True(settings.EnableAutomaticErrorReporting);
        Assert.True(persisted!.EnableUsageTelemetry);
        Assert.True(persisted.EnableAutomaticErrorReporting);
        Assert.Equal(JsonSerializer.Serialize(new AppSettings
        {
            PerformanceDefaultsVersion = AppSettings.CurrentPerformanceDefaultsVersion
        }), JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void DiagnosticPreferencesAreIndependentOfExplicitSaveDraft()
    {
        var state = new SettingsPageViewModel();
        var settings = new AppSettings();
        state.InitializeDraft(settings);
        var before = state.DraftSession!.Current;

        settings.EnableUsageTelemetry = false;
        settings.EnableAutomaticErrorReporting = false;

        Assert.Equal(before, SettingsDraft.From(settings));
        Assert.False(state.DraftSession.IsDirty);
        Assert.Equal("ConversionUiSettingsSaved", state.SaveStateKey);
    }
}
