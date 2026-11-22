using System.Security.AccessControl;
using System.Security.Principal;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class PersistenceLockPermissionTests
{
    [Fact]
    public async Task Settings_ReadOnlyExistingLock_PersistsWithoutChangingLockAttributes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(path);
        await service.SaveAsync(new AppSettings { ParallelCount = 1 });
        var lockPath = path + ".lock";
        try
        {
            File.SetAttributes(lockPath, FileAttributes.ReadOnly);
            await service.SaveAsync(new AppSettings { ParallelCount = 3 });
            Assert.Equal(3, (await service.LoadAsync()).ParallelCount);
            Assert.True(File.GetAttributes(lockPath).HasFlag(FileAttributes.ReadOnly));
        }
        finally { File.SetAttributes(lockPath, FileAttributes.Normal); }
    }

    [Fact]
    public async Task Review_ReadOnlyExistingLock_PreservesLaunchMilestone()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "review.json");
        var service = new ReviewReminderService(path);
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var launch = 0; launch < 9; launch++) Assert.False(await service.RegisterLaunchAsync(now));
        try
        {
            File.SetAttributes(path + ".lock", FileAttributes.ReadOnly);
            Assert.True(await service.RegisterLaunchAsync(now));
            await service.StopRemindersAsync();
            Assert.False(await service.IsPromptDueAsync(now));
            Assert.True(File.GetAttributes(path + ".lock").HasFlag(FileAttributes.ReadOnly));
        }
        finally { File.SetAttributes(path + ".lock", FileAttributes.Normal); }
    }

    [Fact]
    public async Task Queue_ExistingReadOnlyLock_ShutdownActuallyPersistsNewTask()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var store = new QueueStore(path, () => []);
        await store.SaveAsync(Array.Empty<MediaTask>());
        using var session = new QueueSession(store,
            (_, _) => throw new InvalidOperationException("An empty restore never probes."),
            (_, _, _) => throw new InvalidOperationException("No conversion is started."), (_, _) => Task.CompletedTask);
        await session.RestoreAsync();
        var task = new MediaTask { InputPath = Path.Combine(directory.Path, "synthetic.wav"),
            SourceFormatId = "wav", TargetFormatId = "mp3", State = ConversionState.Failed };
        session.Tasks.Add(task);
        try
        {
            // Match the E3 boundary: the lock becomes read-only after restore, before shutdown.
            File.SetAttributes(path + ".lock", FileAttributes.ReadOnly);
            var shutdown = await session.ShutdownAsync();
            Assert.True(shutdown.CanClose, shutdown.SaveError?.GetType().Name);
            var saved = Assert.Single(await store.LoadAsync());
            Assert.Equal(task.Id, saved.TaskId);
            Assert.True(File.GetAttributes(path + ".lock").HasFlag(FileAttributes.ReadOnly));
        }
        finally { File.SetAttributes(path + ".lock", FileAttributes.Normal); }
    }
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task InterprocessLock_ExcludesNewAndLegacyWriters(bool legacyParent, bool legacyChild, bool readOnly)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json.lock");
        var ready = Path.Combine(directory.Path, "ready");
        var release = Path.Combine(directory.Path, "release");
        await File.WriteAllTextAsync(path, "Synthetic lock sentinel.");
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        FileStream? held = null;
        System.Diagnostics.Process? process = null;
        try
        {
            held = legacyParent
                ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : PersistenceFileLock.Open(path, "settings");
            process = RepositoryTestContext.StartTestHelper("persistence-lock-hold", path,
                legacyChild ? "legacy" : "current", ready, release);
            await Task.Delay(200);
            Assert.False(File.Exists(ready));
            Assert.False(process.HasExited);
            held.Dispose();
            held = null;
            await RepositoryTestContext.WaitForFileAsync(ready, TimeSpan.FromSeconds(10));
            Assert.ThrowsAny<IOException>(() => PersistenceFileLock.Open(path, "settings").Dispose());
            await File.WriteAllTextAsync(release, "release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
            Assert.Equal("Synthetic lock sentinel.", await File.ReadAllTextAsync(path));
            Assert.Equal(readOnly, File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            held?.Dispose();
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task WaitingForExclusiveLock_StillHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await new SettingsService(path).SaveAsync(new AppSettings());
        await using var held = PersistenceFileLock.Open(path + ".lock", "settings");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SettingsService(path).SaveAsync(new AppSettings { ParallelCount = 4 }, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LockAclDeniesWriteDataOnly_SettingsStillPersistsWithoutChangingAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(path);
        await service.SaveAsync(new AppSettings { ParallelCount = 1 });
        var info = new FileInfo(path + ".lock");
        var original = info.GetAccessControl();
        var denied = info.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.WriteData | FileSystemRights.AppendData, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(denied);
            var expected = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            await service.SaveAsync(new AppSettings { ParallelCount = 3 });
            Assert.Equal(3, (await service.LoadAsync()).ParallelCount);
            Assert.Equal(expected, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            // Mark the access section modified; a freshly read FileSecurity has no dirty sections.
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            info.SetAccessControl(restored);
            Assert.Equal(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
    }

    [Fact]
    public async Task LockAclDeniesRead_ShutdownFailsHonestlyAndExplicitRetryPreservesNewTask()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var store = new QueueStore(path, () => []);
        await store.SaveAsync(Array.Empty<MediaTask>());
        using var session = new QueueSession(store,
            (_, _) => throw new InvalidOperationException("An empty restore never probes."),
            (_, _, _) => throw new InvalidOperationException("No conversion is started."), (_, _) => Task.CompletedTask);
        await session.RestoreAsync();
        // Empty queues are normally removed; retain a valid legacy empty snapshot for the byte-preservation assertion.
        await File.WriteAllTextAsync(path, "[]");
        var before = await File.ReadAllBytesAsync(path);
        var task = new MediaTask { InputPath = Path.Combine(directory.Path, "synthetic.wav"), State = ConversionState.Failed };
        session.Tasks.Add(task);
        var info = new FileInfo(path + ".lock");
        var original = info.GetAccessControl();
        var denied = info.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(denied);
            var result = await session.ShutdownAsync();
            Assert.False(result.CanClose);
            Assert.IsType<UnauthorizedAccessException>(result.SaveError);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Equal(task.Id, Assert.Single(session.Tasks).Id);
        }
        finally
        {
            // Mark the access section modified; a freshly read FileSecurity has no dirty sections.
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            info.SetAccessControl(restored);
            Assert.Equal(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        session.ContinueAfterFailedShutdown();
        var retry = await session.ShutdownAsync();
        Assert.True(retry.CanClose, retry.SaveError?.ToString());
        Assert.Equal(task.Id, Assert.Single(await store.LoadAsync()).TaskId);
    }

    [Fact]
    public void AccessDiagnostic_RecordsOnlyFixedFactsWithoutPathOrExceptionMessage()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "synthetic-private-name.json");
        File.WriteAllText(path, "Synthetic private contents.");
        File.WriteAllText(path + ".lock", "");
        try
        {
            File.SetAttributes(path + ".lock", FileAttributes.ReadOnly);
            var facts = PersistenceFileLock.DescribeAccessFailure(path + ".lock", "queue", "open-existing",
                new UnauthorizedAccessException("Synthetic sensitive exception message."));
            Assert.Contains("store=queue stage=open-existing hresult=0x80070005", facts);
            Assert.Contains("lock=ReadOnly", facts);
            Assert.DoesNotContain(directory.Path, facts);
            Assert.DoesNotContain("synthetic-private-name", facts);
            Assert.DoesNotContain("sensitive", facts);
            Assert.DoesNotContain("private contents", facts);
        }
        finally { File.SetAttributes(path + ".lock", FileAttributes.Normal); }
    }
}