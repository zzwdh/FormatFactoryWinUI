using System.Runtime.InteropServices;
using FormatFactoryWinUI.Models;
using Windows.ApplicationModel.Background;

namespace FormatFactoryWinUI.Services;

internal static class BackgroundTaskService
{
    private const string WatchFolderTaskName = "FormatFactoryWatchFolder";

    public static async Task ConfigureWatchFolderAsync(bool enabled)
    {
        try
        {
            var registrations = BackgroundTaskRegistration.AllTasks.Values
                .Where(item => item.Name.Equals(WatchFolderTaskName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (!enabled)
            {
                foreach (var registration in registrations)
                {
                    registration.Unregister(true);
                }
                return;
            }

            if (registrations.Length > 0)
            {
                return;
            }

            await BackgroundExecutionManager.RequestAccessAsync();
            var builder = new Microsoft.Windows.ApplicationModel.Background.BackgroundTaskBuilder
            {
                Name = WatchFolderTaskName
            };
            builder.SetTrigger(new TimeTrigger(15, false));
            builder.SetTaskEntryPointClsid(typeof(FormatFactoryBackgroundTask).GUID);
            builder.Register();
            LogService.WriteApp(LocalizationService.T("OperationalLogMsixBackgroundWatchRegistered"));
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogMsixBackgroundTaskRegistrationFailed", ex.Message));
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("C7A5D3E2-91E4-4E20-8AF7-6E1F94F4B9C1")]
public sealed class FormatFactoryBackgroundTask : IBackgroundTask
{
    public async void Run(IBackgroundTaskInstance taskInstance)
    {
        var deferral = taskInstance.GetDeferral();
        using var cancellation = new CancellationTokenSource();
        BackgroundTaskCanceledEventHandler canceled = (_, _) => cancellation.Cancel();
        taskInstance.Canceled += canceled;
        try
        {
            WindowsAppDataStorage.Initialize();
            await BackgroundTaskWorker.ScanWatchFolderAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            LogService.WriteApp("MSIX background watch scan was canceled.");
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogMsixBackgroundWatchFailed", ex.Message));
        }
        finally
        {
            taskInstance.Canceled -= canceled;
            deferral.Complete();
        }
    }
}

internal static class BackgroundTaskWorker
{
    public static async Task<int> ScanWatchFolderAsync(CancellationToken cancellationToken = default)
    {
        WindowsAppDataStorage.Initialize();
        var settings = await new SettingsService().LoadAsync(cancellationToken);
        if (!settings.WatchFolderEnabled)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(settings.WatchFolderAccessToken))
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogBackgroundWatchAccessTokenMissing"));
            return 0;
        }

        var watchFolderPath = await StorageAccessService.ResolveFolderPathAsync(
            null,
            settings.WatchFolderAccessToken,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(watchFolderPath) || !Directory.Exists(watchFolderPath))
        {
            return 0;
        }

        var queueStore = new QueueStore();
        var automation = await new AutomationStore().LoadAsync(cancellationToken);
        var existing = await queueStore.LoadAsync(cancellationToken);
        var known = existing.Select(item => Path.GetFullPath(item.InputPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = new List<PersistedQueueItem>();
        foreach (var path in Directory.EnumerateFiles(watchFolderPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AutomationRuleMatcher.IsExcludedByDestinations(path, automation.Rules) ||
                AutomationRuleMatcher.FirstMatch(automation.Rules, path) is not null) continue;
            if (!File.Exists(path) || !FormatCatalog.SupportedExtensions.Contains(Path.GetExtension(path)) || !known.Add(Path.GetFullPath(path)))
            {
                continue;
            }

            additions.Add(CreatePendingItem(path, watchFolderPath, settings.WatchFolderAccessToken));
        }

        if (additions.Count > 0)
        {
            await queueStore.MergePendingAsync(additions, cancellationToken);
            LogService.WriteApp(LocalizationService.T("OperationalLogMsixBackgroundFilesDiscovered", additions.Count));
        }

        return additions.Count;
    }

    internal static PersistedQueueItem CreatePendingItem(string path, string watchFolderPath, string? watchFolderAccessToken)
    {
        var kind = GuessKind(path);
        var defaultTarget = FormatCatalog.DefaultTarget(kind);
        var targets = FormatCatalog.TargetsFor(kind.ToContentKind(), FormatCatalog.FindByExtension(path)?.Id);
        var target = targets.Contains(defaultTarget) ? defaultTarget : targets.FirstOrDefault() ?? defaultTarget;
        return new PersistedQueueItem
        {
            InputPath = path,
            SourceRoot = watchFolderPath,
            SourceRootAccessToken = watchFolderAccessToken,
            TargetFormat = target,
            ConversionMode = FormatCatalog.DefaultConversionMode
        };
    }

    private static MediaKind GuessKind(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".epub")
        {
            return MediaKind.Ebook;
        }
        if (extension is ".pdf" or ".docx" or ".doc" or ".caj" or ".hn" or ".c8" or ".kdh")
        {
            return MediaKind.Document;
        }
        if (FormatCatalog.ImageFormats.Select(FormatCatalog.ExtensionFor).Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return MediaKind.Image;
        }
        if (FormatCatalog.AudioFormats.Select(FormatCatalog.ExtensionFor).Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return MediaKind.Audio;
        }
        return MediaKind.Video;
    }
}
