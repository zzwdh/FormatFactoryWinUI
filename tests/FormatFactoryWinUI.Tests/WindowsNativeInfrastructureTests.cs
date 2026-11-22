using System.Buffers.Binary;
using System.Collections.Concurrent;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowsNativeInfrastructureTests
{
    [Fact]
    public void OpticalDeviceWatcher_ForwardsBackendChangesOnlyWhileRunning()
    {
        var backend = new FakeOpticalBackend();
        using var watcher = new OpticalDeviceWatcher(backend);
        var changes = new List<OpticalDeviceChangeEventArgs>();
        watcher.Changed += (_, args) => changes.Add(args);

        Assert.True(watcher.Start());
        backend.Publish(OpticalDeviceChangeKind.Arrived, @"\\?\CdRom0");
        watcher.Stop();
        backend.Publish(OpticalDeviceChangeKind.Removed, @"\\?\CdRom0");

        var change = Assert.Single(changes);
        Assert.Equal(OpticalDeviceChangeKind.Arrived, change.Kind);
        Assert.Equal(@"\\?\CdRom0", change.DevicePath);
        Assert.Equal(1, backend.RegisterCount);
        Assert.Equal(1, backend.UnregisterCount);
    }

    [Fact]
    public void SystemPowerStateService_PublishesValueChangesAndRefreshesOnResume()
    {
        var initial = Snapshot(SystemPowerSource.Ac, 80, false);
        var battery = Snapshot(SystemPowerSource.Battery, 70, true);
        var provider = new FakePowerStatusProvider(initial);
        var transitions = new FakeSuspendResumeSource();
        using var service = new SystemPowerStateService(provider, transitions);
        var states = new List<SystemPowerSnapshot>();
        var lifecycle = new List<SystemPowerTransition>();
        service.StateChanged += (_, args) => states.Add(args.Current);
        service.Transitioned += (_, args) => lifecycle.Add(args.Transition);

        Assert.True(service.Refresh());
        provider.Current = initial with { CapturedAt = initial.CapturedAt.AddSeconds(5) };
        Assert.True(service.Refresh());
        provider.Current = battery;
        transitions.Publish(SystemPowerTransition.Resumed);

        Assert.Equal(2, states.Count);
        Assert.Equal(SystemPowerSource.Ac, states[0].Source);
        Assert.Equal(SystemPowerSource.Battery, states[1].Source);
        Assert.True(states[1].EnergySaverEnabled);
        Assert.Equal([SystemPowerTransition.Resumed], lifecycle);
    }

    [Fact]
    public void PerformanceTuning_ReducesParallelismOnBatteryAndEnergySaver()
    {
        var settings = new AppSettings { ParallelCount = 4 };

        Assert.Equal(4, PerformanceTuning.ResolveParallelCount(settings, Snapshot(SystemPowerSource.Ac, 90, false)));
        Assert.Equal(2, PerformanceTuning.ResolveParallelCount(settings, Snapshot(SystemPowerSource.Battery, 70, false)));
        Assert.Equal(1, PerformanceTuning.ResolveParallelCount(settings, Snapshot(SystemPowerSource.Battery, 40, true)));
    }

    [Fact]
    public void PowerRequestManager_ReferenceCountsNativeRequest()
    {
        var platform = new FakePowerRequestPlatform();
        var manager = new ConversionPowerRequestManager(platform);

        var first = manager.Acquire("conversion");
        var second = manager.Acquire("conversion");
        Assert.Equal(1, platform.CreateCount);
        Assert.Equal(
            [PowerRequestType.SystemRequired, PowerRequestType.ExecutionRequired],
            platform.SetRequests);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, manager.ActiveLeaseCount);
        Assert.Empty(platform.ClearRequests);

        second.Dispose();
        Assert.Equal(0, manager.ActiveLeaseCount);
        Assert.Equal(
            [PowerRequestType.SystemRequired, PowerRequestType.ExecutionRequired],
            platform.ClearRequests);
        Assert.Equal(1, platform.CloseCount);
    }

    [Fact]
    public void PowerRequestManager_RejectsInvalidNativeHandle()
    {
        var platform = new FakePowerRequestPlatform { Handle = new nint(-1) };
        var manager = new ConversionPowerRequestManager(platform);

        using var lease = manager.Acquire("conversion");

        Assert.Equal(1, manager.ActiveLeaseCount);
        Assert.Empty(platform.SetRequests);
        Assert.Equal(0, platform.CloseCount);
    }

    [Fact]
    public void RecoveryRegistrar_RegistersInvokesCheckpointAndUnregisters()
    {
        var platform = new FakeRecoveryPlatform();
        var checkpointCount = 0;
        var registrar = new ApplicationRecoveryRegistrar(
            platform,
            () => checkpointCount++);

        var result = registrar.Register("--restore");
        platform.InvokeRecovery();
        registrar.Unregister();

        Assert.True(result.RestartRegistered);
        Assert.True(result.RecoveryRegistered);
        Assert.Equal("--restore", platform.RestartCommandLine);
        Assert.Equal(1, checkpointCount);
        Assert.True(platform.RecoverySucceeded);
        Assert.Equal(1, platform.UnregisterRestartCount);
        Assert.Equal(1, platform.UnregisterRecoveryCount);
    }

    [Fact]
    public void ReadDirectoryChangesParser_ParsesRenamePairAndUnicodeName()
    {
        var buffer = BuildDirectoryChanges(
            (NativeDirectoryChangeAction.RenamedOldName, "旧名称.docx"),
            (NativeDirectoryChangeAction.RenamedNewName, "新名称.docx"));

        var result = ReadDirectoryChangesBackend.ParseNotifications(buffer);

        Assert.Equal(2, result.Count);
        Assert.Equal(NativeDirectoryChangeAction.RenamedOldName, result[0].Action);
        Assert.Equal("旧名称.docx", result[0].RelativePath);
        Assert.Equal(NativeDirectoryChangeAction.RenamedNewName, result[1].Action);
        Assert.Equal("新名称.docx", result[1].RelativePath);
    }

    [Fact]
    public async Task NativeDirectoryWatcher_CombinesRenamePair()
    {
        using var directory = new TemporaryDirectory();
        var backend = new FakeDirectoryChangeBackend();
        using var watcher = new NativeDirectoryWatcher(backend);
        var completion = new TaskCompletionSource<DirectoryChangeEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, args) => completion.TrySetResult(args);

        Assert.True(watcher.Start(directory.Path));
        backend.Publish(
            new NativeDirectoryChange(NativeDirectoryChangeAction.RenamedOldName, "old.pdf"),
            new NativeDirectoryChange(NativeDirectoryChangeAction.RenamedNewName, "new.pdf"));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DirectoryChangeKind.Renamed, result.Kind);
        Assert.Equal(Path.Combine(directory.Path, "new.pdf"), result.FullPath);
        Assert.Equal(Path.Combine(directory.Path, "old.pdf"), result.OldFullPath);
    }

    private static SystemPowerSnapshot Snapshot(
        SystemPowerSource source,
        int batteryPercent,
        bool energySaver) => new(
        source,
        true,
        batteryPercent,
        energySaver,
        TimeSpan.FromHours(2),
        DateTimeOffset.UtcNow);

    private static byte[] BuildDirectoryChanges(
        params (NativeDirectoryChangeAction Action, string Name)[] changes)
    {
        var records = changes.Select(change =>
        {
            var name = System.Text.Encoding.Unicode.GetBytes(change.Name);
            var length = Align4(12 + name.Length);
            return (change.Action, Name: name, Length: length);
        }).ToArray();
        var buffer = new byte[records.Sum(record => record.Length)];
        var offset = 0;
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(offset, 4),
                index == records.Length - 1 ? 0u : checked((uint)record.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(offset + 4, 4),
                (uint)record.Action);
            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(offset + 8, 4),
                checked((uint)record.Name.Length));
            record.Name.CopyTo(buffer.AsSpan(offset + 12));
            offset += record.Length;
        }
        return buffer;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private sealed class FakeOpticalBackend : IOpticalDeviceNotificationBackend
    {
        public event EventHandler<OpticalDeviceChangeEventArgs>? Changed;
        public int LastErrorCode => 0;
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }

        public bool TryRegister()
        {
            RegisterCount++;
            return true;
        }

        public void Unregister() => UnregisterCount++;
        public void Dispose()
        {
        }

        public void Publish(OpticalDeviceChangeKind kind, string path) =>
            Changed?.Invoke(this, new OpticalDeviceChangeEventArgs(kind, path, DateTimeOffset.UtcNow));
    }

    private sealed class FakePowerStatusProvider(SystemPowerSnapshot current)
        : ISystemPowerStatusProvider
    {
        public SystemPowerSnapshot Current { get; set; } = current;

        public bool TryRead(out SystemPowerSnapshot snapshot)
        {
            snapshot = Current;
            return true;
        }
    }

    private sealed class FakeSuspendResumeSource : ISuspendResumeNotificationSource
    {
        public event EventHandler<SystemPowerTransitionEventArgs>? Transitioned;
        public bool IsRunning { get; private set; }
        public bool Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
        public void Publish(SystemPowerTransition transition) =>
            Transitioned?.Invoke(this, new SystemPowerTransitionEventArgs(transition));
    }

    private sealed class FakePowerRequestPlatform : IPowerRequestPlatform
    {
        public nint Handle { get; init; } = 42;
        public int CreateCount { get; private set; }
        public int CloseCount { get; private set; }
        public List<PowerRequestType> SetRequests { get; } = [];
        public List<PowerRequestType> ClearRequests { get; } = [];

        public nint Create(string reason)
        {
            CreateCount++;
            return Handle;
        }

        public bool Set(nint handle, PowerRequestType requestType)
        {
            SetRequests.Add(requestType);
            return true;
        }

        public bool Clear(nint handle, PowerRequestType requestType)
        {
            ClearRequests.Add(requestType);
            return true;
        }

        public void Close(nint handle) => CloseCount++;
    }

    private sealed class FakeRecoveryPlatform : IApplicationRecoveryPlatform
    {
        private ApplicationRecoveryCallback? _callback;
        public string? RestartCommandLine { get; private set; }
        public bool RecoverySucceeded { get; private set; }
        public int UnregisterRestartCount { get; private set; }
        public int UnregisterRecoveryCount { get; private set; }

        public int RegisterRestart(string commandLine, uint flags)
        {
            RestartCommandLine = commandLine;
            return 0;
        }

        public int UnregisterRestart()
        {
            UnregisterRestartCount++;
            return 0;
        }

        public int RegisterRecovery(
            ApplicationRecoveryCallback callback,
            nint parameter,
            uint pingIntervalMilliseconds,
            uint flags)
        {
            _callback = callback;
            return 0;
        }

        public int UnregisterRecovery()
        {
            UnregisterRecoveryCount++;
            return 0;
        }

        public void RecoveryFinished(bool success) => RecoverySucceeded = success;
        public void InvokeRecovery()
        {
            var callback = Assert.IsType<ApplicationRecoveryCallback>(_callback);
            callback(0);
        }
    }

    private sealed class FakeDirectoryChangeBackend : IDirectoryChangeBackend
    {
        private readonly ConcurrentQueue<IReadOnlyList<NativeDirectoryChange>> _changes = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void Open(string folderPath, bool includeSubdirectories)
        {
        }

        public IReadOnlyList<NativeDirectoryChange> Read(CancellationToken cancellationToken)
        {
            _signal.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return _changes.TryDequeue(out var changes) ? changes : [];
        }

        public void Cancel() => _signal.Release();
        public void Close()
        {
        }
        public void Dispose() => _signal.Dispose();

        public void Publish(params NativeDirectoryChange[] changes)
        {
            _changes.Enqueue(changes);
            _signal.Release();
        }
    }
}
