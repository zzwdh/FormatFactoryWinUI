using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class StartupSettingsPersistenceTests
{
    public static TheoryData<Exception, bool> RecoverableFailures => new()
    {
        { new IOException("Synthetic disk failure."), false },
        { new IOException("Synthetic disk failure."), true },
        { new UnauthorizedAccessException("Synthetic denied write."), false },
        { new UnauthorizedAccessException("Synthetic denied write."), true },
        { new TimeoutException("Synthetic lock timeout."), false },
        { new TimeoutException("Synthetic lock timeout."), true }
    };

    [Theory]
    [MemberData(nameof(RecoverableFailures))]
    public async Task StorageFailure_ReturnsOriginalFailureWithoutRetry(Exception failure, bool asynchronous)
    {
        var attempts = 0;
        var result = await StartupSettingsPersistence.TrySaveAsync(() =>
        {
            attempts++;
            if (asynchronous) return Task.FromException(failure);
            throw failure;
        });

        Assert.Same(failure, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SuccessfulSave_IsAwaitedExactlyOnce()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var save = StartupSettingsPersistence.TrySaveAsync(() => { attempts++; return completion.Task; });
        Assert.False(save.IsCompleted);
        completion.SetResult();

        Assert.Null(await save);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_StillPropagates(bool asynchronous)
    {
        var failure = new OperationCanceledException(new CancellationToken(canceled: true));
        var result = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            StartupSettingsPersistence.TrySaveAsync(() =>
            {
                if (asynchronous) return Task.FromException(failure);
                throw failure;
            }));
        Assert.Same(failure, result);
    }

    [Fact]
    public async Task ProgrammingFailure_StillPropagates()
    {
        var failure = new InvalidOperationException("Synthetic invariant failure.");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupSettingsPersistence.TrySaveAsync(() => Task.FromException(failure))));
    }

    [Fact]
    public async Task RealDeniedWrite_PreservesDurableSettingsAndTransactionalState()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(path);
        var original = new AppSettings { PerformanceDefaultsVersion = 2, ParallelCount = 3 };
        await service.SaveAsync(original);
        var bytes = await File.ReadAllBytesAsync(path);
        var protectedPath = path;
        var coordinator = new SettingsCommitCoordinator(candidate => service.SaveAsync(candidate), (_, _) => Task.CompletedTask);
        try
        {
            File.SetAttributes(protectedPath, FileAttributes.ReadOnly);
            var failure = await StartupSettingsPersistence.TrySaveAsync(() =>
                coordinator.UpdateAsync(original, settings => settings.ParallelCount = 1));

            Assert.IsType<UnauthorizedAccessException>(failure);
            Assert.Equal(3, original.ParallelCount);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory.Path, "settings.json.*.tmp"));
            // Manual saves retain their failure contract rather than silently succeeding.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                coordinator.UpdateAsync(original, settings => settings.ParallelCount = 2));
            Assert.Equal(3, original.ParallelCount);
        }
        finally
        {
            File.SetAttributes(protectedPath, FileAttributes.Normal);
        }

        // Once storage is writable, an explicit retry still persists and commits normally.
        await coordinator.UpdateAsync(original, settings => settings.ParallelCount = 2);
        Assert.Equal(2, original.ParallelCount);
        Assert.Equal(2, (await service.LoadAsync()).ParallelCount);
    }
}