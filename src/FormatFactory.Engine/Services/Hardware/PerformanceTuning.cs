using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Services;

internal static class PerformanceTuning
{
    public static int RecommendedParallelCount =>
        Math.Clamp(Environment.ProcessorCount / 8, 1, 4);

    public static int ResolveCpuThreads(AppSettings settings) =>
        ResolveCpuThreads(settings, Environment.ProcessorCount);

    internal static int ResolveCpuThreads(AppSettings settings, int availableProcessorCount)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var processorCount = Math.Max(1, availableProcessorCount);
        if (settings.CpuThreadsPerTask > 0)
        {
            return Math.Clamp(settings.CpuThreadsPerTask, 1, processorCount);
        }

        var parallel = Math.Clamp(settings.ParallelCount, 1, 4);
        var reserve = processorCount >= 16 ? 2 : 1;
        return Math.Max(1, (processorCount - reserve) / parallel);
    }

    public static string DescribeCpuStrategy(AppSettings settings) =>
        settings.CpuThreadsPerTask > 0
            ? LocalizationService.T("CpuThreadsFixed", ResolveCpuThreads(settings))
            : LocalizationService.T("CpuThreadsAuto", ResolveCpuThreads(settings));

    public static int ResolveParallelCount(
        AppSettings settings,
        SystemPowerSnapshot? powerState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configured = Math.Clamp(settings.ParallelCount, 1, 4);
        if (powerState?.EnergySaverEnabled == true)
        {
            return 1;
        }

        return powerState?.Source == SystemPowerSource.Battery
            ? Math.Min(2, configured)
            : configured;
    }
}
