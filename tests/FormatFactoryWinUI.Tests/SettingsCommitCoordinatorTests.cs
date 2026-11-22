using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class SettingsCommitCoordinatorTests
{
    [Fact]
    public async Task ResetRestoresAllPreferencesAndPreservesDisabledWatchFolder()
    {
        var settings = NonDefaultSettings();
        var originalReference = settings;
        var expected = new AppSettings
        {
            PerformanceDefaultsVersion = 2,
            WatchFolderPath = settings.WatchFolderPath,
            WatchFolderAccessToken = settings.WatchFolderAccessToken
        };
        AppSettings? persisted = null;
        var startupCalls = new List<(bool, bool)>();
        var coordinator = new SettingsCommitCoordinator(
            candidate => { persisted = candidate.Snapshot(); return Task.CompletedTask; },
            (enabled, tray) => { startupCalls.Add((enabled, tray)); return Task.CompletedTask; });

        await coordinator.ResetAsync(settings);

        Assert.Same(originalReference, settings);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(settings));
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(persisted));
        Assert.False(settings.WatchFolderEnabled);
        Assert.False(settings.HardwareDecodeTestCompleted);
        Assert.Equal([(false, true)], startupCalls);
    }

    [Fact]
    public async Task FailedResetRestoresStartupWithoutPublishingAnyPreference()
    {
        var settings = NonDefaultSettings();
        var before = JsonSerializer.Serialize(settings);
        var startupCalls = new List<(bool, bool)>();
        var coordinator = new SettingsCommitCoordinator(
            _ => throw new IOException("disk"),
            (enabled, tray) => { startupCalls.Add((enabled, tray)); return Task.CompletedTask; });

        await Assert.ThrowsAsync<IOException>(() => coordinator.ResetAsync(settings));

        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal([(false, true), (true, false)], startupCalls);
    }

    [Fact]
    public async Task ResetStartupFailureDoesNotPersistOrPublishSettings()
    {
        var settings = NonDefaultSettings();
        var before = JsonSerializer.Serialize(settings);
        var saves = 0;
        var startupCalls = new List<(bool, bool)>();
        var coordinator = new SettingsCommitCoordinator(
            _ => { saves++; return Task.CompletedTask; },
            (enabled, tray) =>
            {
                startupCalls.Add((enabled, tray));
                if (!enabled) throw new IOException("startup");
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IOException>(() => coordinator.ResetAsync(settings));

        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal([(false, true), (true, false)], startupCalls);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task ResetRollbackFailureReportsBothFailuresWithoutPublishing()
    {
        var settings = NonDefaultSettings();
        var before = JsonSerializer.Serialize(settings);
        var coordinator = new SettingsCommitCoordinator(
            _ => throw new IOException("disk"),
            (enabled, _) => !enabled ? Task.CompletedTask : throw new InvalidOperationException("rollback"));

        var error = await Assert.ThrowsAsync<AggregateException>(() => coordinator.ResetAsync(settings));

        Assert.IsType<IOException>(error.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(error.InnerExceptions[1]);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public async Task ResetSharesSaveGateAndPreservesMostRecentlyCommittedWatchFolder()
    {
        var settings = NonDefaultSettings();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new List<AppSettings>();
        var coordinator = new SettingsCommitCoordinator(async candidate =>
        {
            if (snapshots.Count == 0) { entered.SetResult(); await release.Task; }
            snapshots.Add(candidate.Snapshot());
        }, (_, _) => Task.CompletedTask);
        var savedDraft = SettingsDraft.From(settings) with
        {
            ParallelCount = 4,
            WatchFolderPath = "latest-folder",
            WatchFolderAccessToken = "latest-token"
        };

        var save = coordinator.CommitDraftAsync(settings, savedDraft, _ => Task.CompletedTask);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reset = coordinator.ResetAsync(settings);
        var language = coordinator.UpdateAsync(settings, candidate => candidate.Language = "de-DE");
        Assert.False(reset.IsCompleted);
        Assert.False(language.IsCompleted);
        release.SetResult();
        await Task.WhenAll(save, reset, language).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(4, snapshots[0].ParallelCount);
        Assert.Equal(2, snapshots[1].ParallelCount);
        Assert.Equal("auto", snapshots[1].Language);
        Assert.Equal("de-DE", snapshots[2].Language);
        Assert.Equal("latest-folder", settings.WatchFolderPath);
        Assert.Equal("latest-token", settings.WatchFolderAccessToken);
        Assert.False(settings.WatchFolderEnabled);
        Assert.Equal(string.Empty, settings.FfmpegPath);
        Assert.Equal("de-DE", settings.Language);
    }

    private static AppSettings NonDefaultSettings()
    {
        var settings = new AppSettings();
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (property.PropertyType == typeof(bool))
                property.SetValue(settings, !(bool)property.GetValue(settings)!);
            else if (property.PropertyType == typeof(int))
                property.SetValue(settings, (int)property.GetValue(settings)! + 5);
            else if (property.PropertyType == typeof(string))
                property.SetValue(settings, $"custom-{property.Name}");
            else
                throw new InvalidOperationException($"Populate the new setting {property.Name} before testing reset.");
        }
        return settings;
    }

    [Fact]
    public async Task InvalidDraftDoesNotChangeSharedOrPersistedSettings()
    {
        var settings = new AppSettings();
        var before = SettingsDraft.From(settings);
        var saves = 0;
        var coordinator = new SettingsCommitCoordinator(_ => { saves++; return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitDraftAsync(
            settings, before with { WatchFolderEnabled = true, WatchFolderPath = "missing", ParallelCount = 4 },
            _ => throw new InvalidOperationException("invalid path")));
        Assert.Equal(before, SettingsDraft.From(settings));
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task PersistenceFailureRestoresStartupAndDoesNotPolluteImmediateSave()
    {
        var settings = new AppSettings();
        var startupCalls = new List<(bool, bool)>();
        var fail = true;
        AppSettings? persisted = null;
        var coordinator = new SettingsCommitCoordinator(
            candidate => { if (fail) throw new IOException("disk"); persisted = candidate; return Task.CompletedTask; },
            (enabled, tray) => { startupCalls.Add((enabled, tray)); return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => coordinator.CommitDraftAsync(
            settings, SettingsDraft.From(settings) with { StartWithWindows = true, ParallelCount = 4 }, _ => Task.CompletedTask));
        Assert.Equal([(true, true), (false, true)], startupCalls);
        Assert.False(settings.StartWithWindows);
        Assert.Equal(2, settings.ParallelCount);
        fail = false;
        await coordinator.UpdateAsync(settings, candidate => candidate.Theme = "dark");
        Assert.Equal(2, persisted!.ParallelCount);
        Assert.False(persisted.StartWithWindows);
        Assert.Equal("dark", settings.Theme);
    }

    [Fact]
    public async Task StartupFailureRollsBackWithoutSavingDraft()
    {
        var settings = new AppSettings();
        var saves = 0;
        var calls = new List<bool>();
        var coordinator = new SettingsCommitCoordinator(
            _ => { saves++; return Task.CompletedTask; },
            (enabled, _) => { calls.Add(enabled); if (enabled) throw new IOException("startup"); return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => coordinator.CommitDraftAsync(
            settings, SettingsDraft.From(settings) with { StartWithWindows = true }, _ => Task.CompletedTask));
        Assert.Equal([true, false], calls);
        Assert.False(settings.StartWithWindows);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task DelayedDraftSavePreservesQueuedImmediateAndBackgroundChanges()
    {
        var settings = new AppSettings();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppSettings? persisted = null;
        var count = 0;
        var coordinator = new SettingsCommitCoordinator(async candidate =>
        {
            if (Interlocked.Increment(ref count) == 1) { entered.SetResult(); await release.Task; }
            persisted = candidate;
        }, (_, _) => Task.CompletedTask);
        var save = coordinator.CommitDraftAsync(settings, SettingsDraft.From(settings) with { ParallelCount = 4 }, _ => Task.CompletedTask);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var theme = coordinator.UpdateAsync(settings, candidate => candidate.Theme = "dark");
        var language = coordinator.UpdateAsync(settings, candidate => candidate.Language = "de-DE");
        var background = coordinator.UpdateAsync(settings, candidate => candidate.HardwareDecodeTestCompleted = true);
        release.SetResult();
        await Task.WhenAll(save, theme, language, background).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, settings.ParallelCount);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal("de-DE", settings.Language);
        Assert.True(settings.HardwareDecodeTestCompleted);
        Assert.Equal(settings.Language, persisted!.Language);
        Assert.True(persisted.HardwareDecodeTestCompleted);
    }

    [Fact]
    public async Task FailedImmediateSaveDoesNotChangeSharedPreferencesOrExistingDraft()
    {
        var settings = new AppSettings();
        var session = new SettingsDraftSession(SettingsDraft.From(settings));
        session.Edit(session.Current with { ParallelCount = 4, WatchFolderPath = "not-yet-saved" });
        var coordinator = new SettingsCommitCoordinator(
            _ => throw new IOException("disk"), (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => coordinator.UpdateAsync(settings, candidate => candidate.Theme = "dark"));
        Assert.Equal("system", settings.Theme);
        Assert.Equal(2, settings.ParallelCount);
        Assert.Equal(string.Empty, settings.WatchFolderPath);
        Assert.True(session.IsDirty);
        Assert.Equal(4, session.Current.ParallelCount);
    }

    [Fact]
    public async Task FailedStartupRollbackReportsBothFailuresWithoutPublishingDraft()
    {
        var settings = new AppSettings();
        var coordinator = new SettingsCommitCoordinator(
            _ => throw new IOException("disk"),
            (enabled, _) => enabled ? Task.CompletedTask : throw new InvalidOperationException("rollback"));
        var error = await Assert.ThrowsAsync<AggregateException>(() => coordinator.CommitDraftAsync(
            settings, SettingsDraft.From(settings) with { StartWithWindows = true, ParallelCount = 4 },
            _ => Task.CompletedTask));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.IsType<IOException>(error.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(error.InnerExceptions[1]);
        Assert.False(settings.StartWithWindows);
        Assert.Equal(2, settings.ParallelCount);
    }

    [Fact]
    public void InitialDiscoveryBaselinePreservesConcurrentUserEdits()
    {
        var original = SettingsDraft.From(new AppSettings());
        var session = new SettingsDraftSession(original);
        var detected = original with { FfmpegPath = "detected.exe" };
        session.Edit(detected);
        session.AcceptSaved(detected);
        Assert.False(session.IsDirty);
        session.Edit(detected with { FfmpegPath = "user-choice.exe", Notifications = false });
        session.AcceptSaved(detected with { LibreOfficePath = "office.exe" });
        Assert.True(session.IsDirty);
        Assert.Equal("user-choice.exe", session.Current.FfmpegPath);
        Assert.False(session.Current.Notifications);
    }

    [Fact]
    public async Task NormalizedPathsArePersistedWithoutChangingTheInputBaseline()
    {
        var settings = new AppSettings();
        var captured = SettingsDraft.From(settings) with
        {
            FfmpegPath = "link-ffmpeg.exe",
            LibreOfficePath = Path.Combine("tools", "office.exe"),
            CalibreEbookConvertPath = Path.Combine("tools", "ebook-convert.exe"),
            Caj2PdfPath = Path.Combine("tools", "caj.exe"),
            MakeMkvPath = Path.Combine("tools", "makemkv.exe"),
            AudioCdRipperPath = Path.Combine("tools", "ripper.exe"),
            ReadomPath = Path.Combine("tools", "readom.exe")
        };
        var session = new SettingsDraftSession(SettingsDraft.From(settings));
        session.Edit(captured);
        var normalized = captured.NormalizeToolPaths(_ => Path.Combine("resolved", "ffmpeg.exe"));
        AppSettings? persisted = null;
        var coordinator = new SettingsCommitCoordinator(
            candidate => { persisted = candidate; return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        await coordinator.CommitDraftAsync(settings, normalized, _ => Task.CompletedTask);
        session.AcceptSaved(captured);
        Assert.False(session.IsDirty);
        Assert.Equal("link-ffmpeg.exe", session.Current.FfmpegPath);
        Assert.Equal(Path.GetFullPath(Path.Combine("resolved", "ffmpeg.exe")), persisted!.FfmpegPath);
        Assert.Equal(Path.GetFullPath(captured.LibreOfficePath), settings.LibreOfficePath);
        Assert.All(new[] { settings.FfmpegPath, settings.CalibreEbookConvertPath, settings.Caj2PdfPath,
            settings.MakeMkvPath, settings.AudioCdRipperPath, settings.ReadomPath }, path => Assert.True(Path.IsPathFullyQualified(path)));
        Assert.Equal(string.Empty, new SettingsDraft().NormalizeToolPaths(_ => throw new Exception("empty path")).FfmpegPath);
    }

    [Fact]
    public void SavingEarlierSnapshotKeepsLaterEditsDirty()
    {
        var original = SettingsDraft.From(new AppSettings());
        var session = new SettingsDraftSession(original);
        var captured = original with { ParallelCount = 3 };
        session.Edit(captured);
        session.Edit(captured with { Notifications = false });
        session.AcceptSaved(captured);
        Assert.True(session.IsDirty);
        Assert.False(session.Current.Notifications);
        Assert.True(session.Baseline.Notifications);
        session.AcceptSaved(session.Current);
        Assert.False(session.IsDirty);
    }
}
