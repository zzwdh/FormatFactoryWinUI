using System.Text.Json;

namespace FormatFactoryWinUI.Services;

internal static class RecoveryService
{
    private static readonly object Gate = new();
    private static readonly ApplicationRecoveryRegistrar Registration = new(
        new NativeApplicationRecoveryPlatform(),
        SaveCheckpoint);
    private static PersistedQueueItem[] _checkpoint = [];

    public static void Register()
    {
        try
        {
            var result = Registration.Register("--restore");
            if (!result.RestartRegistered)
            {
                SafeDiagnostics.Write(LocalizationService.T("OperationalLogAppRecoveryRegistrationHresultFailed", result.RestartResult));
            }

            if (!result.RecoveryRegistered)
            {
                SafeDiagnostics.Write(LocalizationService.T("OperationalLogCrashCheckpointRegistrationHresultFailed", result.RecoveryResult));
            }
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogAppRecoveryRegistrationFailed", ex.Message));
        }
    }

    public static void UpdateCheckpoint(IEnumerable<PersistedQueueItem> items)
    {
        lock (Gate)
        {
            _checkpoint = items.ToArray();
        }
    }

    public static IReadOnlyList<PersistedQueueItem> LoadCheckpoint()
    {
        var path = Path.Combine(LogService.AppDataRoot, "recovery-checkpoint.json");
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<PersistedQueueItem[]>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogCrashCheckpointReadFailed", ex.Message));
            return [];
        }
    }

    public static void ClearCheckpoint()
    {
        var path = Path.Combine(LogService.AppDataRoot, "recovery-checkpoint.json");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogCrashCheckpointCleanupFailed", ex.Message));
        }
    }
    public static void Unregister()
    {
        try
        {
            Registration.Unregister();
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogAppRecoveryUnregisterFailed", ex.Message));
        }
    }

    private static void SaveCheckpoint()
    {
        PersistedQueueItem[] snapshot;
        lock (Gate)
        {
            snapshot = _checkpoint.ToArray();
        }

        Directory.CreateDirectory(LogService.AppDataRoot);
        var path = Path.Combine(LogService.AppDataRoot, "recovery-checkpoint.json");
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // 崩溃回调中尽量保留已写入的检查点。
            }
        }
    }

}
