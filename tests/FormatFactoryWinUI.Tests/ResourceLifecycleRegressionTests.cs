using System.Diagnostics;
using System.Text.Json;
using FormatFactoryWinUI.Services;
using Xunit.Abstractions;

namespace FormatFactoryWinUI.Tests;

[CollectionDefinition(nameof(ResourceLifecycleCollection), DisableParallelization = true)]
public sealed class ResourceLifecycleCollection { }

[Collection(nameof(ResourceLifecycleCollection))]
public sealed class ResourceLifecycleRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RepeatedWatcherAndCancelledProcessLifetimesReleaseResources()
    {
        using var directory = new TemporaryDirectory();
        var samples = new List<ResourceSample>();
        var cycles = int.TryParse(Environment.GetEnvironmentVariable("FORMAT_FACTORY_STRESS_CYCLES"), out var requested)
            ? Math.Clamp(requested, 20, 1000) : 20;
        for (var cycle = 0; cycle < cycles + 5; cycle++)
        {
            await Task.Run(() =>
            {
                using var watcher = new NativeDirectoryWatcher();
                Assert.True(watcher.Start(directory.Path));
                watcher.Stop();
            });
            var pidPath = Path.Combine(directory.Path, $"{cycle}.pid");
            using var cancellation = new CancellationTokenSource();
            var run = ProcessRunner.RunAsync(RepositoryTestContext.DotNetHost,
                new[] { RepositoryTestContext.TestHelperAssembly, "progress-and-wait", pidPath },
                TimeSpan.FromSeconds(15), cancellation.Token);
            try { await RepositoryTestContext.WaitForFileAsync(pidPath, TimeSpan.FromSeconds(10)); }
            finally { cancellation.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await RepositoryTestContext.AssertProcessExitedAsync(int.Parse(await File.ReadAllTextAsync(pidPath)), TimeSpan.FromSeconds(5));
            if (cycle < 5) continue; // Warm JIT, logging and thread-pool allocations.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var current = Process.GetCurrentProcess();
            samples.Add(new(cycle - 5, current.HandleCount, current.PrivateMemorySize64, GC.GetTotalMemory(false)));
        }
        var json = JsonSerializer.Serialize(new { cycles, samples }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("FORMAT_FACTORY_PERFORMANCE_REPORT_DIR") is { Length: > 0 } reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "resource-lifecycle.json"), json);
        }
        var first = samples.Take(5).OrderBy(sample => sample.Handles).ElementAt(2);
        var last = samples.TakeLast(5).OrderBy(sample => sample.Handles).ElementAt(2);
        Assert.True(last.Handles - first.Handles < 64, $"Handle growth after warmup: {last.Handles - first.Handles}");
        Assert.True(samples.TakeLast(5).Average(sample => sample.ManagedBytes) - samples.Take(5).Average(sample => sample.ManagedBytes) < 16 * 1024 * 1024,
            "Retained managed memory grew by more than 16 MiB after warmup.");
    }

    private sealed record ResourceSample(int Cycle, int Handles, long PrivateBytes, long ManagedBytes);
}
