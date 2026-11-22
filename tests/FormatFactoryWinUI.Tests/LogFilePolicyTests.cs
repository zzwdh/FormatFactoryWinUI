using System.Text;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class LogFilePolicyTests
{
    [Fact]
    public void BufferedWriterRotatesBoundedSegmentsAndKeepsLatestBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.log");
        var policy = new LogFilePolicy(directory.Path, maximumFileBytes: 32);
        using var writer = new BufferedLogWriter(capacity: 2, policy: policy);
        var text = new string('a', 120) + "LATEST";
        writer.Write(path, text);
        writer.Flush(path);

        Assert.All(Directory.EnumerateFiles(directory.Path), file => Assert.InRange(new FileInfo(file).Length, 1, 32));
        var retained = File.ReadAllText(path + ".2") + File.ReadAllText(path + ".1") + File.ReadAllText(path);
        Assert.EndsWith("LATEST", retained);
        Assert.Equal(text[^retained.Length..], retained);
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task ProcessOutputContinuesDrainingAfterManyRotationsAndPreservesTail()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "converter.log");
        var policy = new LogFilePolicy(directory.Path, maximumFileBytes: 128);
        var data = Encoding.UTF8.GetBytes(new string('x', 20000) + "END-OUT");
        using var source = new MemoryStream(data);

        var result = await BoundedProcessOutput.ReadAsync(source, "stdout", 32, false, path, policy);

        Assert.Equal(data.Length, source.Position);
        Assert.EndsWith("END-OUT", result.Text);
        Assert.True(result.Truncated);
        Assert.EndsWith("END-OUT", File.ReadAllText(path));
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
        Assert.All(Directory.EnumerateFiles(directory.Path), file => Assert.InRange(new FileInfo(file).Length, 1, 128));
    }

    [Fact]
    public void RetentionDeletesOnlyOwnedInactiveFilesAndHonorsBudget()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var policy = new LogFilePolicy(directory.Path, directoryBudgetBytes: 25, clock: () => now);
        var expired = TaskLog(directory.Path, "1");
        var oldest = TaskLog(directory.Path, "2");
        var recent = TaskLog(directory.Path, "3");
        var active = TaskLog(directory.Path, "4");
        var unknown = Path.Combine(directory.Path, "user-notes.log");
        var application = Path.Combine(directory.Path, "application.log");
        foreach (var file in new[] { expired, oldest, recent, active, unknown, application }) File.WriteAllText(file, new string('x', 10));
        File.SetLastWriteTimeUtc(expired, now.AddDays(-31).UtcDateTime);
        File.SetLastWriteTimeUtc(active, now.AddDays(-32).UtcDateTime);
        File.SetLastWriteTimeUtc(oldest, now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, now.AddDays(-1).UtcDateTime);
        using var protection = policy.Protect(active);

        policy.Prune(force: true);

        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(recent)); // Only active task + current application (20 bytes) remain in the 25-byte budget.
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(application));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void OpenLogFromAnotherWriterIsNotDeletedAndMaintenanceIsThrottled()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var policy = new LogFilePolicy(directory.Path, clock: () => now);
        var open = TaskLog(directory.Path, "5");
        File.WriteAllText(open, "active");
        File.SetLastWriteTimeUtc(open, now.AddDays(-40).UtcDateTime);
        using (var handle = new FileStream(open, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            policy.Prune();
            Assert.True(File.Exists(open));
        }
        policy.Prune();
        Assert.True(File.Exists(open));
        now = now.AddMinutes(6);
        policy.Prune();
        Assert.False(File.Exists(open));
    }

    [Fact]
    public void ActiveTaskAlsoProtectsItsRotatedBackups()
    {
        using var directory = new TemporaryDirectory();
        var path = TaskLog(directory.Path, "6");
        var policy = new LogFilePolicy(directory.Path, directoryBudgetBytes: 0);
        File.WriteAllText(path, "active");
        File.WriteAllText(path + ".1", "earlier");
        using (policy.Protect(path)) policy.Prune(force: true);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        policy.Prune(force: true);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".1"));
    }

    [Fact]
    public void TextRotationNeverSplitsUtf8CharactersBetweenSegments()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.log");
        var policy = new LogFilePolicy(directory.Path, maximumFileBytes: 13);
        using var writer = new BufferedLogWriter(policy: policy);
        var text = string.Concat(Enumerable.Repeat("中文🙂", 12)) + "最新🙂";
        writer.Write(path, text);
        writer.Flush();
        var utf8 = new UTF8Encoding(false, true);
        foreach (var file in Directory.EnumerateFiles(directory.Path))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.InRange(bytes.Length, 1, 13);
            Assert.NotEmpty(utf8.GetString(bytes));
        }
        Assert.EndsWith("最新🙂", File.ReadAllText(path + ".1") + File.ReadAllText(path));
    }

    [Fact]
    public void RetentionDoesNotRecurseIntoSubdirectories()
    {
        using var directory = new TemporaryDirectory();
        var child = Directory.CreateDirectory(Path.Combine(directory.Path, "other-data")).FullName;
        var nested = TaskLog(child, "7");
        File.WriteAllText(nested, "must remain");
        File.SetLastWriteTimeUtc(nested, DateTime.UtcNow.AddDays(-50));
        new LogFilePolicy(directory.Path, directoryBudgetBytes: 0).Prune(force: true);
        Assert.Equal("must remain", File.ReadAllText(nested));
    }

    private static string TaskLog(string root, string digit) =>
        Path.Combine(root, $"task-20261112-010000-{new string(digit[0], 32)}.log");
}
