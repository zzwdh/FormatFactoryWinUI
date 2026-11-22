using FormatFactoryWinUI.Services;
using System.Diagnostics;
using Xunit.Abstractions;

namespace FormatFactoryWinUI.Tests;

public sealed class LegacyAppDataMigrationTests(ITestOutputHelper output)
{
    [Fact]
    public void RepeatedLaunchBenchmark_CompletedMigrationAvoidsAllLegacyFileVisits()
    {
        using var directory = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(directory.Path, "legacy")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(directory.Path, "current")).FullName;
        const int files = 1000;
        const int launches = 20;
        for (var i = 0; i < files; i++) File.WriteAllText(Path.Combine(source, $"task-{i}.log"), "diagnostic");
        LegacyAppDataMigration.Run(source, destination);

        // Reproduce the previous repeat-launch path against the same already-copied fixture.
        var timer = Stopwatch.StartNew();
        var previousVisits = 0;
        for (var launch = 0; launch < launches; launch++)
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            previousVisits++;
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(file, target);
        }
        var previousMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var currentVisits = 0;
        for (var launch = 0; launch < launches; launch++) currentVisits += LegacyAppDataMigration.Run(source, destination).Examined;
        var currentMs = timer.Elapsed.TotalMilliseconds;
        output.WriteLine($"Migration repeat-launch benchmark: files={files}, launches={launches}, previous_visits={previousVisits}, current_visits={currentVisits}, previous_ms={previousMs:F3}, current_ms={currentMs:F3}");
        Assert.Equal(files * launches, previousVisits);
        Assert.Equal(0, currentVisits); // Correctness is deterministic; timing is reported without a machine-dependent threshold.
    }

    [Fact]
    public void SuccessfulMigrationSkipsLaterEnumerationAndNeverOverwritesCurrentData()
    {
        using var directory = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(directory.Path, "legacy")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(directory.Path, "current")).FullName;
        File.WriteAllText(Path.Combine(source, "settings.json"), "legacy settings");
        File.WriteAllText(Path.Combine(destination, "settings.json"), "current settings");
        Directory.CreateDirectory(Path.Combine(source, "Logs"));
        File.WriteAllText(Path.Combine(source, "Logs", "task.log"), "diagnostic");

        Assert.Equal((2, 1), LegacyAppDataMigration.Run(source, destination));
        File.WriteAllText(Path.Combine(source, "later.json"), "new legacy data");
        Assert.Equal((0, 0), LegacyAppDataMigration.Run(source, destination));
        Assert.Equal("current settings", File.ReadAllText(Path.Combine(destination, "settings.json")));
        Assert.Equal("diagnostic", File.ReadAllText(Path.Combine(destination, "Logs", "task.log")));
        Assert.False(File.Exists(Path.Combine(destination, "later.json")));
    }

    [Fact]
    public void FailedCopyDoesNotMarkCompleteAndCanRetry()
    {
        using var directory = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(directory.Path, "legacy")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(directory.Path, "current")).FullName;
        var input = Path.Combine(source, "queue.json");
        File.WriteAllText(input, "complete queue");
        using (var locked = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => LegacyAppDataMigration.Run(source, destination));
            Assert.False(File.Exists(Path.Combine(destination, LegacyAppDataMigration.MarkerName)));
            Assert.False(File.Exists(Path.Combine(destination, "queue.json")));
            Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
        }
        Assert.Equal((1, 1), LegacyAppDataMigration.Run(source, destination));
        Assert.Equal("complete queue", File.ReadAllText(Path.Combine(destination, "queue.json")));
    }

    [Fact]
    public void InvalidOrDifferentSourceMarkerDoesNotSuppressMigration()
    {
        using var directory = new TemporaryDirectory();
        var first = Directory.CreateDirectory(Path.Combine(directory.Path, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(directory.Path, "second")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(directory.Path, "current")).FullName;
        File.WriteAllText(Path.Combine(destination, LegacyAppDataMigration.MarkerName), "incomplete");
        File.WriteAllText(Path.Combine(first, "first.json"), "first");
        Assert.Equal((1, 1), LegacyAppDataMigration.Run(first, destination));
        File.WriteAllText(Path.Combine(second, "second.json"), "second");
        Assert.Equal((1, 1), LegacyAppDataMigration.Run(second, destination));
    }
}
