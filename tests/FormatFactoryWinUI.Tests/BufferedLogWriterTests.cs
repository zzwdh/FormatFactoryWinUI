using System.Collections.Concurrent;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class BufferedLogWriterTests
{
    [Fact]
    public void Flush_WritesEverySubmittedRecordInOrderIncludingUnicodeChunkBoundary()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "task.log");
        var text = new string('a', 8191) + "😀" + new string('中', 9000);
        using var writer = new BufferedLogWriter(capacity: 2);
        writer.Write(path, "first");
        writer.Write(path, text);
        writer.Write(path, "last");

        writer.Flush(path);

        Assert.Equal("first" + text + "last", File.ReadAllText(path));
    }

    [Fact]
    public async Task Flush_WaitsForSubmittedWriteToFinish()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writer = new BufferedLogWriter(2, (_, _) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        writer.Write("task", "record");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var flush = Task.Run(() => writer.Flush("task"));
        try
        {
            Assert.False(flush.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await flush.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WriteFailure_IsReportedByMatchingFlushAndWorkerCanRecover()
    {
        var fail = true;
        var written = new ConcurrentQueue<string>();
        using var writer = new BufferedLogWriter(2, (path, text) =>
        {
            if (path == "bad" && fail)
            {
                throw new IOException("disk temporarily unavailable");
            }
            written.Enqueue(path + ":" + text);
        });
        writer.Write("bad", "failed");
        writer.Write("good", "kept");
        writer.Flush("good");
        Assert.IsType<IOException>(writer.LastWriteError);
        Assert.Throws<IOException>(() => writer.Flush("bad"));

        fail = false;
        writer.Write("bad", "recovered");
        writer.Flush();

        Assert.Contains("good:kept", written);
        Assert.Contains("bad:recovered", written);
    }

    [Fact]
    public async Task ConcurrentProducers_WithSmallCapacityDoNotLoseRecords()
    {
        using var directory = new TemporaryDirectory();
        using var writer = new BufferedLogWriter(capacity: 2);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(producer => Task.Run(() =>
        {
            var path = Path.Combine(directory.Path, producer + ".log");
            for (var index = 0; index < 30; index++)
            {
                writer.Write(path, index + Environment.NewLine);
            }
        })));

        writer.Flush();

        for (var producer = 0; producer < 6; producer++)
        {
            Assert.Equal(Enumerable.Range(0, 30).Select(index => index.ToString()),
                File.ReadAllLines(Path.Combine(directory.Path, producer + ".log")));
        }
    }

    [Fact]
    public void Dispose_FlushesOutstandingRecords()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "task.log");
        var writer = new BufferedLogWriter();
        writer.Write(path, "last record");

        writer.Dispose();

        Assert.Equal("last record", File.ReadAllText(path));
        Assert.Throws<ObjectDisposedException>(() => writer.Write(path, "too late"));
    }
}
