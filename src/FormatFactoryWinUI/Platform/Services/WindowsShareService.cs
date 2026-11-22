using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FormatFactoryWinUI.Services;

internal sealed class WindowsShareService : IDisposable
{
    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    private readonly IntPtr _windowHandle;
    private readonly IDataTransferManagerInterop _interop;
    private readonly DataTransferManager _manager;
    private string[] _pendingPaths = [];
    private bool _disposed;

    public WindowsShareService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var managerAbi = _interop.GetForWindow(windowHandle, ref iid);
        try
        {
            _manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(managerAbi);
        }
        finally
        {
            // FromAbi creates its own managed reference; GetForWindow's original
            // caller-owned ABI reference must be balanced independently.
            if (managerAbi != IntPtr.Zero) Marshal.Release(managerAbi);
        }
        _manager.DataRequested += Manager_DataRequested;
    }

    public void ShareFiles(IEnumerable<string> paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pendingPaths = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_pendingPaths.Length > 0)
        {
            _interop.ShowShareUIForWindow(_windowHandle);
        }
    }

    private async void Manager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            if (_disposed) return;
            var paths = _pendingPaths;
            args.Request.Data.Properties.Title = LocalizationService.T("BrandTitle");
            args.Request.Data.Properties.Description = LocalizationService.T("CoreShareConvertedMediaDescription");
            var files = new List<StorageFile>();
            foreach (var path in paths)
            {
                if (_disposed) return;
                try { files.Add(await StorageFile.GetFileFromPathAsync(path)); }
                catch (Exception ex)
                {
                    LogService.WriteApp(LocalizationService.T("OperationalLogShareFilePrepareFailed", path, ex.Message));
                }
            }
            if (!_disposed && files.Count > 0)
            {
                args.Request.Data.SetStorageItems(files);
            }
        }
        catch (Exception ex)
        {
            LogService.WriteAppError(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingPaths = [];
        _manager.DataRequested -= Manager_DataRequested;
    }
}

[ComImport]
[Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataTransferManagerInterop
{
    IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
    void ShowShareUIForWindow(IntPtr appWindow);
}
