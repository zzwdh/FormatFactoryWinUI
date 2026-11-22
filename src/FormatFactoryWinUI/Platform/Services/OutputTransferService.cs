using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FormatFactoryWinUI.Services;

internal static class OutputTransferService
{
    internal const string OutputDragMarker = "FormatFactory.ConvertedOutput";

    public static async Task<IReadOnlyList<IStorageItem>> ResolveAsync(IEnumerable<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path)) items.Add(await StorageFile.GetFileFromPathAsync(path));
            else if (Directory.Exists(path)) items.Add(await StorageFolder.GetFolderFromPathAsync(path));
            else throw new FileNotFoundException(LocalizationService.T("NoOutput"), path);
        }
        return items;
    }

    public static async Task CopyAsync(IEnumerable<string> paths)
    {
        var items = await ResolveAsync(paths);
        if (items.Count == 0) return;
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetStorageItems(items, readOnly: true);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    public static void PrepareDrag(DataPackage data, IReadOnlyList<string> paths)
    {
        data.RequestedOperation = DataPackageOperation.Copy;
        data.Properties[OutputDragMarker] = true;
        // ListView's DragItemsStarting has no asynchronous deferral. Resolve files
        // only when Windows requests the payload, with a data-provider deferral.
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try { request.SetData(await ResolveAsync(paths)); }
            catch (Exception ex) { LogService.WriteAppError(ex); }
            finally { deferral.Complete(); }
        });
    }
}
