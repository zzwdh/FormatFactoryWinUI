using System.Diagnostics;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowsFilterWorkerTests
{
    [Fact]
    public async Task WorkerRoundTrip_PreservesUnicodePathAndText()
    {
        using var directory = new TemporaryDirectory();
        var path = await CreateInputAsync(directory);
        var result = await Extractor("success").ExtractAsync(path);
        Assert.True(result.IsAvailable, result.Error);
        Assert.Contains(path, result.Text);
        Assert.Contains("完成", result.Text);
        Assert.Equal(WindowsFilterFailureKind.None, result.FailureKind);
    }

    [Theory]
    [InlineData("partial", WindowsFilterFailureKind.NativeFailure)]
    [InlineData("version", WindowsFilterFailureKind.InvalidResponse)]
    [InlineData("malformed", WindowsFilterFailureKind.InvalidResponse)]
    [InlineData("crash", WindowsFilterFailureKind.WorkerCrashed)]
    [InlineData("oversized", WindowsFilterFailureKind.OutputLimit)]
    public async Task WorkerFailure_NeverReturnsPartialSuccess(string behavior, WindowsFilterFailureKind expected)
    {
        using var directory = new TemporaryDirectory();
        var result = await Extractor(behavior).ExtractAsync(await CreateInputAsync(directory));
        Assert.False(result.IsAvailable);
        Assert.Empty(result.Text);
        Assert.Equal(expected, result.FailureKind);
    }

    [Fact]
    public async Task HungWorker_TimesOutAndKillsItsDescendant()
    {
        using var directory = new TemporaryDirectory();
        var childPid = Path.Combine(directory.Path, "child.pid");
        var result = await Extractor("hang", TimeSpan.FromSeconds(2), childPid).ExtractAsync(await CreateInputAsync(directory));
        Assert.Equal(WindowsFilterFailureKind.Timeout, result.FailureKind);
        Assert.Empty(result.Text);
        await RepositoryTestContext.AssertProcessExitedAsync(int.Parse(await File.ReadAllTextAsync(childPid)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledWorker_PropagatesCancellationAndKillsItsDescendant()
    {
        using var directory = new TemporaryDirectory();
        var childPid = Path.Combine(directory.Path, "child.pid");
        var input = await CreateInputAsync(directory);
        using var cancellation = new CancellationTokenSource();
        var start = Stopwatch.StartNew();
        var run = Extractor("hang", TimeSpan.FromSeconds(20), childPid)
            .ExtractAsync(input, cancellation.Token);
        start.Stop();
        Assert.True(start.Elapsed < TimeSpan.FromSeconds(5),
            $"The UI caller was blocked for {start.Elapsed} while the filter worker was running.");
        Assert.False(run.IsCompleted);
        await RepositoryTestContext.WaitForFileAsync(childPid, TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await RepositoryTestContext.AssertProcessExitedAsync(int.Parse(await File.ReadAllTextAsync(childPid)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissingWorker_PreservesFallbackAndDoesNotLoadFiltersInHost()
    {
        using var directory = new TemporaryDirectory();
        var input = await CreateInputAsync(directory);
        var extractor = new WindowsFilterTextExtractor(Path.Combine(directory.Path, "missing.exe"));
        Assert.False(extractor.IsAvailable(input));
        var result = await extractor.ExtractAsync(input);
        Assert.Equal(WindowsFilterFailureKind.WorkerMissing, result.FailureKind);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task RealWorker_UsesSingleVersionedResponseForNativeTextExtraction()
    {
        using var directory = new TemporaryDirectory();
        var path = await CreateInputAsync(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var worker = Path.Combine(RepositoryTestContext.Root, "src", "FormatFactory.FilterWorker", "bin",
            configuration, "net11.0", "FormatFactory.FilterWorker.dll");
        Assert.True(File.Exists(worker), "The isolated filter worker must be built for test runs.");
        var extractor = new WindowsFilterTextExtractor(RepositoryTestContext.DotNetHost, [worker]);
        var result = await extractor.ExtractAsync(path);
        if (result.IsAvailable) Assert.Contains("worker native text", result.Text);
        else Assert.Contains(result.FailureKind, new[] { WindowsFilterFailureKind.NotRegistered, WindowsFilterFailureKind.NativeFailure });
    }

    private static WindowsFilterTextExtractor Extractor(string behavior, TimeSpan? timeout = null, string? extra = null) =>
        new(RepositoryTestContext.DotNetHost,
            extra is null ? [RepositoryTestContext.TestHelperAssembly, "filter-worker", behavior]
                : [RepositoryTestContext.TestHelperAssembly, "filter-worker", behavior, extra], timeout);

    private static async Task<string> CreateInputAsync(TemporaryDirectory directory)
    {
        var path = Path.Combine(directory.Path, "路径 with spaces.txt");
        await File.WriteAllTextAsync(path, "worker native text");
        return path;
    }
}
