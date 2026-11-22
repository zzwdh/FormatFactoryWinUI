using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class PerformanceTuningTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(4, 4, 1)]
    [InlineData(8, 4, 1)]
    [InlineData(8, 2, 3)]
    [InlineData(16, 4, 3)]
    public void ResolveCpuThreads_AutoRespectsAvailableBudget(int processors, int parallel, int expected)
    {
        var settings = new AppSettings { CpuThreadsPerTask = 0, ParallelCount = parallel };

        Assert.Equal(expected, PerformanceTuning.ResolveCpuThreads(settings, processors));
    }

    [Fact]
    public void ResolveCpuThreads_ExplicitSettingRetainsPrecedenceWithInjectedProcessorCount()
    {
        var settings = new AppSettings { CpuThreadsPerTask = 4, ParallelCount = 4 };

        Assert.Equal(4, PerformanceTuning.ResolveCpuThreads(settings, 4));
    }

    [Fact]
    public void ResolveCpuThreads_ExplicitValueOverridesParallelCount()
    {
        var settings = new AppSettings
        {
            CpuThreadsPerTask = 1,
            ParallelCount = 4
        };

        var result = PerformanceTuning.ResolveCpuThreads(settings);

        Assert.Equal(1, result);
    }

    [Fact]
    public void ResolveCpuThreads_ExplicitValueIsClampedToAvailableProcessors()
    {
        var settings = new AppSettings
        {
            CpuThreadsPerTask = int.MaxValue,
            ParallelCount = 1
        };

        var result = PerformanceTuning.ResolveCpuThreads(settings);

        Assert.Equal(Math.Max(1, Environment.ProcessorCount), result);
    }
}
