using System.Text.Json;
using System.Diagnostics;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task ResetAsync_PersistsCompleteDefaultsAndRetainedDisabledWatchFolder()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(Path.Combine(directory.Path, "settings.json"));
        var settings = new AppSettings();
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (property.PropertyType == typeof(bool))
                property.SetValue(settings, !(bool)property.GetValue(settings)!);
            else if (property.PropertyType == typeof(int))
                property.SetValue(settings, (int)property.GetValue(settings)! + 5);
            else if (property.PropertyType == typeof(string))
                property.SetValue(settings, $"custom-{property.Name}");
            else
                throw new InvalidOperationException($"Populate the new setting {property.Name} before testing reset.");
        }
        await service.SaveAsync(settings);
        var expected = new AppSettings
        {
            PerformanceDefaultsVersion = 2,
            WatchFolderPath = settings.WatchFolderPath,
            WatchFolderAccessToken = settings.WatchFolderAccessToken
        };
        var coordinator = new SettingsCommitCoordinator(candidate => service.SaveAsync(candidate), (_, _) => Task.CompletedTask);

        await coordinator.ResetAsync(settings);
        var reloaded = await service.LoadAsync();

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(reloaded));
        Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(reloaded));
        Assert.False(reloaded.WatchFolderEnabled);
        Assert.Equal(string.Empty, reloaded.FfmpegPath);
        Assert.Equal("system", reloaded.Theme);
        Assert.Equal("auto", reloaded.Language);
        Assert.False(reloaded.HardwareDecodeTestCompleted);
    }

    [Fact]
    public async Task ConcurrentSaves_ProduceOneCompleteJsonDocumentAndNoUniqueTempFiles()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var values = Enumerable.Range(1, 48).ToArray();

        await Task.WhenAll(values.Select(value =>
            new SettingsService(settingsPath).SaveAsync(new AppSettings
            {
                CpuThreadsPerTask = value,
                ParallelCount = (value % 4) + 1,
                WatchFolderPath = new string((char)('a' + value % 26), 4096)
            })));

        var json = await File.ReadAllTextAsync(settingsPath);
        using var document = JsonDocument.Parse(json);
        var savedValue = document.RootElement.GetProperty(nameof(AppSettings.CpuThreadsPerTask)).GetInt32();

        Assert.Contains(savedValue, values);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "settings.json.*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_DoesNotUseOrOverwriteLegacyFixedTempPath()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var fixedTempPath = settingsPath + ".tmp";
        const string sentinel = "owned-by-another-writer";
        await File.WriteAllTextAsync(fixedTempPath, sentinel);

        await new SettingsService(settingsPath).SaveAsync(new AppSettings { CpuThreadsPerTask = 7 });

        Assert.Equal(sentinel, await File.ReadAllTextAsync(fixedTempPath));
        Assert.Equal(7, (await new SettingsService(settingsPath).LoadAsync()).CpuThreadsPerTask);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "settings.json.*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_SerializesImmutableSnapshotBeforeWaitingForLock()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new AppSettings
        {
            CpuThreadsPerTask = 3,
            WatchFolderPath = new string('x', 1_000_000)
        };

        var save = new SettingsService(settingsPath).SaveAsync(settings);
        settings.CpuThreadsPerTask = 99;
        settings.WatchFolderPath = "mutated";
        await save;

        var loaded = await new SettingsService(settingsPath).LoadAsync();
        Assert.Equal(3, loaded.CpuThreadsPerTask);
        Assert.Equal(1_000_000, loaded.WatchFolderPath.Length);
    }

    [Fact]
    public async Task ConcurrentProcesses_ProduceCompleteLastWriterSnapshotWithoutTempLeaks()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var startFlag = Path.Combine(directory.Path, "start.flag");
        const int writerCount = 4;
        const int iterations = 10;
        var processes = new List<Process>();
        try
        {
            for (var writerId = 1; writerId <= writerCount; writerId++)
            {
                var readyFlag = Path.Combine(directory.Path, $"writer-{writerId}.ready");
                processes.Add(RepositoryTestContext.StartTestHelper(
                    "settings-writer",
                    settingsPath,
                    writerId.ToString(),
                    iterations.ToString(),
                    startFlag,
                    readyFlag));
            }

            await Task.WhenAll(Enumerable.Range(1, writerCount).Select(writerId =>
                RepositoryTestContext.WaitForFileAsync(
                    Path.Combine(directory.Path, $"writer-{writerId}.ready"),
                    TimeSpan.FromSeconds(10))));
            await File.WriteAllTextAsync(startFlag, "go");

            foreach (var process in processes)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var standardError = await process.StandardError.ReadToEndAsync();
                Assert.True(process.ExitCode == 0, $"Writer exited with {process.ExitCode}: {standardError}");
            }

            var json = await File.ReadAllTextAsync(settingsPath);
            using var document = JsonDocument.Parse(json);
            var savedThreads = document.RootElement
                .GetProperty(nameof(AppSettings.CpuThreadsPerTask))
                .GetInt32();
            var savedPath = document.RootElement
                .GetProperty(nameof(AppSettings.WatchFolderPath))
                .GetString();

            var winningWriter = savedThreads / 10_000;
            var winningIteration = savedThreads % 10_000;
            Assert.InRange(winningWriter, 1, writerCount);
            Assert.InRange(winningIteration, 0, iterations - 1);
            Assert.StartsWith(
                $"writer-{winningWriter:D2}-iteration-{winningIteration:D2}-",
                savedPath,
                StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory.Path, "settings.json.*.tmp"));

            var loaded = await new SettingsService(settingsPath).LoadAsync();
            Assert.Equal(savedThreads, loaded.CpuThreadsPerTask);
            Assert.Equal(savedPath, loaded.WatchFolderPath);
        }
        finally
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup for a failed test helper.
                }
                process.Dispose();
            }
        }
    }
}
