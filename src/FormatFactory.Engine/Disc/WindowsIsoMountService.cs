using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FormatFactoryWinUI.Services;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Disc;

public sealed class MountedIso : IDisposable
{
    private Action? _detach;

    internal MountedIso(string imagePath, string physicalPath, Action detach)
    {
        ImagePath = imagePath;
        PhysicalPath = physicalPath;
        _detach = detach;
    }

    public string ImagePath { get; }

    public string PhysicalPath { get; }

    public bool IsMounted => Volatile.Read(ref _detach) is not null;

    public void Dispose() => Interlocked.Exchange(ref _detach, null)?.Invoke();
}

public sealed class WindowsIsoMountService : IWindowsIsoMountService
{
    private readonly object _lifecycleGate = new();
    private readonly ConcurrentDictionary<string, MountedIso> _mounts =
        new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public Task<MountedIso> MountAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fullPath = ValidateIsoPath(imagePath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                if (_mounts.TryGetValue(fullPath, out var existing) && existing.IsMounted)
                {
                    return existing;
                }
            }

            var handle = WindowsVirtualDisk.OpenIso(fullPath);
            try
            {
                WindowsVirtualDisk.AttachReadOnly(handle);
                cancellationToken.ThrowIfCancellationRequested();
                var physicalPath = WindowsVirtualDisk.GetPhysicalPath(handle);
                var mountedHandle = handle;
                MountedIso? mounted = null;
                mounted = new MountedIso(fullPath, physicalPath, () =>
                {
                    _mounts.TryRemove(
                        new KeyValuePair<string, MountedIso>(fullPath, mounted!));
                    try
                    {
                        WindowsVirtualDisk.Detach(mountedHandle);
                    }
                    finally
                    {
                        mountedHandle.Dispose();
                    }
                });

                MountedIso? existingMount = null;
                var disposed = false;
                lock (_lifecycleGate)
                {
                    disposed = _disposed != 0;
                    if (!disposed && !_mounts.TryAdd(fullPath, mounted))
                    {
                        _mounts.TryGetValue(fullPath, out existingMount);
                    }
                    else if (!disposed)
                    {
                        handle = null!;
                        return mounted;
                    }
                }

                mounted.Dispose();
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(WindowsIsoMountService));
                }

                return existingMount is { IsMounted: true }
                    ? existingMount
                    : throw new InvalidOperationException(LocalizationService.T("ConversionUiIsoMountStateChanged"));
            }
            finally
            {
                handle?.Dispose();
            }
        }, CancellationToken.None);
    }

    public Task UnmountAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(imagePath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            MountedIso? mounted;
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                _mounts.TryRemove(fullPath, out mounted);
            }

            if (mounted is not null)
            {
                mounted.Dispose();
                return;
            }

            using var handle = WindowsVirtualDisk.OpenIso(fullPath);
            WindowsVirtualDisk.Detach(handle);
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        MountedIso[] mounts;
        lock (_lifecycleGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            mounts = _mounts.Values.ToArray();
            _mounts.Clear();
        }

        foreach (var mounted in mounts)
        {
            try
            {
                mounted.Dispose();
            }
            catch
            {
                // The native handle is still closed by MountedIso.
            }
        }
    }

    private static string ValidateIsoPath(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        if (!Path.GetExtension(fullPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiSystemMountIsoOnly"), nameof(imagePath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiIsoToMountMissing"), fullPath);
        }

        return fullPath;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal sealed class SafeVirtualDiskHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeVirtualDiskHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => WindowsVirtualDisk.CloseHandle(handle);
}

internal static class WindowsVirtualDisk
{
    private const uint DeviceIso = 1;
    private const uint OpenVersion1 = 1;
    private const uint AttachVersion1 = 1;
    private const uint AccessAttachReadOnly = 0x00010000;
    private const uint AccessDetach = 0x00040000;
    private const uint AccessGetInfo = 0x00080000;
    private const uint AttachFlagReadOnly = 0x00000001;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    public static SafeVirtualDiskHandle OpenIso(string imagePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(LocalizationService.T("ConversionUiVirtualDiskWindowsOnly"));
        }

        var storageType = new VirtualStorageType
        {
            DeviceId = DeviceIso,
            VendorId = MicrosoftVendor
        };
        var parameters = new OpenVirtualDiskParameters
        {
            Version = OpenVersion1,
            ReadWriteDepth = 1
        };
        var result = OpenVirtualDisk(
            ref storageType,
            imagePath,
            AccessAttachReadOnly | AccessDetach | AccessGetInfo,
            0,
            ref parameters,
            out var handle);
        if (result != 0)
        {
            handle?.Dispose();
            throw new Win32Exception(checked((int)result), LocalizationService.T("ConversionUiCannotOpenIsoVirtualDisk"));
        }

        return handle;
    }

    public static void AttachReadOnly(SafeVirtualDiskHandle handle)
    {
        var parameters = new AttachVirtualDiskParameters
        {
            Version = AttachVersion1,
            Reserved = 0
        };
        var result = AttachVirtualDisk(
            handle,
            IntPtr.Zero,
            AttachFlagReadOnly,
            0,
            ref parameters,
            IntPtr.Zero);
        if (result != 0)
        {
            throw new Win32Exception(checked((int)result), LocalizationService.T("ConversionUiCannotMountIso"));
        }
    }

    public static void Detach(SafeVirtualDiskHandle handle)
    {
        var result = DetachVirtualDisk(handle, 0, 0);
        if (result != 0)
        {
            throw new Win32Exception(checked((int)result), LocalizationService.T("ConversionUiCannotUnmountIso"));
        }
    }

    public static string GetPhysicalPath(SafeVirtualDiskHandle handle)
    {
        uint sizeInBytes = 1024;
        var buffer = new StringBuilder(checked((int)(sizeInBytes / sizeof(char))));
        var result = GetVirtualDiskPhysicalPath(handle, ref sizeInBytes, buffer);
        if (result == ErrorInsufficientBuffer)
        {
            buffer = new StringBuilder(checked((int)(sizeInBytes / sizeof(char))));
            result = GetVirtualDiskPhysicalPath(handle, ref sizeInBytes, buffer);
        }

        if (result != 0)
        {
            throw new Win32Exception(checked((int)result), LocalizationService.T("ConversionUiCannotGetIsoMountPath"));
        }

        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenVirtualDiskParameters
    {
        public uint Version;
        public uint ReadWriteDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttachVirtualDiskParameters
    {
        public uint Version;
        public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        ref OpenVirtualDiskParameters parameters,
        out SafeVirtualDiskHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(
        SafeVirtualDiskHandle virtualDiskHandle,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref AttachVirtualDiskParameters parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(
        SafeVirtualDiskHandle virtualDiskHandle,
        uint flags,
        uint providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(
        SafeVirtualDiskHandle virtualDiskHandle,
        ref uint diskPathSizeInBytes,
        StringBuilder diskPath);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
