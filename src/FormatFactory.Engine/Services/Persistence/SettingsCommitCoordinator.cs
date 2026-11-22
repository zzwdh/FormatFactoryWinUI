using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class SettingsCommitCoordinator(
    Func<AppSettings, Task> persist,
    Func<bool, bool, Task> configureStartup)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task CommitDraftAsync(
        AppSettings settings,
        SettingsDraft draft,
        Func<SettingsDraft, Task> validate)
    {
        await _gate.WaitAsync();
        try
        {
            await validate(draft);
            var candidate = Clone(settings);
            draft.ApplyTo(candidate);
            await PersistAndPublishAsync(settings, candidate);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            await PersistAndPublishAsync(settings, AppSettings.CreateDefaultsPreservingFolders(settings));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(AppSettings settings, Action<AppSettings> change)
    {
        await _gate.WaitAsync();
        try
        {
            var candidate = Clone(settings);
            change(candidate);
            await persist(candidate);
            settings.CopyFrom(candidate);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAndPublishAsync(AppSettings settings, AppSettings candidate)
    {
        var startupChanged = candidate.StartWithWindows != settings.StartWithWindows
            || candidate.StartInTray != settings.StartInTray;
        try
        {
            if (startupChanged)
                await configureStartup(candidate.StartWithWindows, candidate.StartInTray);
            await persist(candidate);
        }
        catch (Exception failure)
        {
            if (startupChanged)
            {
                try
                {
                    await configureStartup(settings.StartWithWindows, settings.StartInTray);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(failure, rollbackFailure);
                }
            }
            throw;
        }
        settings.CopyFrom(candidate);
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
}
