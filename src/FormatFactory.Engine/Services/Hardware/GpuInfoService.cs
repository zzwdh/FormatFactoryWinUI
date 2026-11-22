namespace FormatFactoryWinUI.Services;

internal static class GpuInfoService
{
    public static string GetSummary()
    {
        var adapters = HardwareInventoryService.EnumerateGpus();
        return adapters.Count == 0
            ? LocalizationService.T("NativeCapabilityGpuUnavailable")
            : string.Join(Environment.NewLine, adapters.Select(adapter =>
                LocalizationService.T(
                    "NativeCapabilityGpuAdapterSummary",
                    string.IsNullOrWhiteSpace(adapter.Name)
                        ? LocalizationService.T("NativeCapabilityGpuUnknown")
                        : adapter.Name,
                    adapter.DedicatedVideoMemory / 1024d / 1024d / 1024d)));
    }
}
