using System.ComponentModel;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Disc;

public sealed class WindowsOpticalDeviceController : IWindowsOpticalDeviceController
{
    public Task<IOpticalVolumeLock> LockVolumeAsync(
        string rootOrDevicePath,
        bool dismount = false,
        CancellationToken cancellationToken = default)
    {
        var devicePath = OpticalDevicePath.Normalize(rootOrDevicePath);
        return Task.Run<IOpticalVolumeLock>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = WindowsOpticalNative.OpenDevice(
                devicePath,
                WindowsOpticalNative.GenericRead | WindowsOpticalNative.GenericWrite);
            try
            {
                WindowsOpticalNative.Control(
                    handle,
                    WindowsOpticalNative.FsctlLockVolume,
                    LocalizationService.T("ConversionUiCannotLockOpticalVolume", devicePath));
                if (dismount)
                {
                    WindowsOpticalNative.Control(
                        handle,
                        WindowsOpticalNative.FsctlDismountVolume,
                        LocalizationService.T("ConversionUiCannotDismountOpticalVolume", devicePath));
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new OpticalVolumeLock(devicePath, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }, CancellationToken.None);
    }

    public Task EjectAsync(
        string rootOrDevicePath,
        CancellationToken cancellationToken = default)
    {
        var devicePath = OpticalDevicePath.Normalize(rootOrDevicePath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = WindowsOpticalNative.OpenDevice(
                devicePath,
                WindowsOpticalNative.GenericRead | WindowsOpticalNative.GenericWrite);

            // FALSE permits removal. Some drives accept EJECT without this call,
            // so only a failed eject is surfaced to the caller.
            _ = WindowsOpticalNative.DeviceIoControl(
                handle,
                WindowsOpticalNative.IoctlStorageMediaRemoval,
                [0],
                1,
                null,
                0,
                out _,
                IntPtr.Zero);
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsOpticalNative.DeviceIoControl(
                    handle,
                    WindowsOpticalNative.IoctlStorageEjectMedia,
                    null,
                    0,
                    null,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    LocalizationService.T("ConversionUiCannotEjectDisc", devicePath));
            }
        }, CancellationToken.None);
    }

    private sealed class OpticalVolumeLock : IOpticalVolumeLock
    {
        private SafeFileHandle? _handle;

        public OpticalVolumeLock(string devicePath, SafeFileHandle handle)
        {
            DevicePath = devicePath;
            _handle = handle;
        }

        public string DevicePath { get; }

        public bool IsLocked => _handle is { IsClosed: false, IsInvalid: false };

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle is null)
            {
                return;
            }

            try
            {
                _ = WindowsOpticalNative.DeviceIoControl(
                    handle,
                    WindowsOpticalNative.FsctlUnlockVolume,
                    null,
                    0,
                    null,
                    0,
                    out _,
                    IntPtr.Zero);
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
}
