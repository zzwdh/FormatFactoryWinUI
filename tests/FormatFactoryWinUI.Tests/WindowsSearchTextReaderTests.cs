using System.Diagnostics;
using FormatFactoryWinUI.Documents;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowsSearchTextReaderTests
{
    [Fact]
    public async Task HungFilter_ReturnsPendingReadImmediatelyAndHonorsCancellation()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseStartup = new ManualResetEventSlim();
        var reader = new WindowsSearchTextReader(async (_, token) =>
        {
            invoked.TrySetResult();
            // Model a third-party filter/process launch that blocks before its first await.
            releaseStartup.Wait(TimeSpan.FromSeconds(5));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new WindowsFilterExtractionResult(true, "unreachable");
        });
        using var cancellation = new CancellationTokenSource();

        var start = Stopwatch.StartNew();
        var read = reader.ReadAsync("document.epub", cancellation.Token);
        start.Stop();

        try
        {
            Assert.True(start.Elapsed < TimeSpan.FromSeconds(2),
                $"Search text extraction blocked its caller for {start.Elapsed}.");
            await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(read.IsCompleted);
        }
        finally
        {
            releaseStartup.Set();
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task PlainText_IsReadAsynchronouslyAndBoundedWithoutStartingFilter()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "large.TXT");
        await File.WriteAllTextAsync(path, new string('x', WindowsSearchTextReader.MaximumIndexedCharacters + 100));
        var reader = new WindowsSearchTextReader((_, _) =>
            throw new InvalidOperationException("Plain text must not start an IFilter worker."));

        var text = await reader.ReadAsync(path);

        Assert.Equal(WindowsSearchTextReader.MaximumIndexedCharacters, text!.Length);
        Assert.Equal(new string('x', WindowsSearchTextReader.MaximumIndexedCharacters), text);
    }
}
