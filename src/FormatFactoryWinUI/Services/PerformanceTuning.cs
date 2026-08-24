using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class PerformanceTuning
{
    public static int RecommendedParallelCount =>
        Math.Clamp(Environment.ProcessorCount / 8, 1, 4);

    public static int ResolveCpuThreads(AppSettings settings)
    {
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        if (settings.CpuThreadsPerTask > 0)
        {
            return Math.Clamp(settings.CpuThreadsPerTask, 1, processorCount);
        }

        var parallel = Math.Clamp(settings.ParallelCount, 1, 4);
        var reserve = processorCount >= 16 ? 2 : 1;
        return Math.Max(2, (processorCount - reserve) / parallel);
    }

    public static string DescribeCpuStrategy(AppSettings settings) =>
        settings.CpuThreadsPerTask > 0
            ? $"每个任务固定 {ResolveCpuThreads(settings)} 个 CPU 线程"
            : $"自动分配 · 每个任务约 {ResolveCpuThreads(settings)} 个 CPU 线程";
}
