using System.Text.Json;

namespace FormatFactoryWinUI.Services;

internal static class ActivationBatchStore
{
    private static string BatchDirectory =>
        Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "ShellBatches");

    public static IEnumerable<string> ReadAndDelete(string batchId)
    {
        if (!Guid.TryParseExact(batchId, "N", out var id))
        {
            return [];
        }

        var path = Path.Combine(BatchDirectory, $"{id:N}.json");
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogExplorerBatchReadFailed", ex.Message));
            return [];
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                LogService.WriteApp(LocalizationService.T("OperationalLogExplorerBatchCleanupFailed", ex.Message));
            }
        }
    }
}
