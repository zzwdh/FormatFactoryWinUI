using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed record WindowsScannerDeviceInfo(string DeviceId, string Name, string? Description, string? Port);
public enum WindowsScanSource { Flatbed, Feeder }
public enum WindowsScanColor { Color = 1, Grayscale = 2, BlackAndWhite = 4 }
public sealed record WindowsScannerCapabilities(bool HasFlatbed, bool HasFeeder, bool HasDuplex, IReadOnlyList<int> Resolutions);
public sealed record WindowsScanRequest(string DeviceId, string OutputPath, string Format = "png",
    WindowsScanSource Source = WindowsScanSource.Flatbed, bool Duplex = false, int Dpi = 300,
    WindowsScanColor Color = WindowsScanColor.Color, int MaximumPages = 100);
public sealed record WindowsScanBatchResult(IReadOnlyList<string> Paths, IReadOnlyList<string> Warnings,
    string? Error = null, bool Canceled = false);

internal sealed class ScanTransferException(IReadOnlyList<string> paths, Exception inner) : Exception(inner.Message, inner)
{
    internal IReadOnlyList<string> Paths { get; } = paths;
}
internal interface IWindowsScanSession : IDisposable
{
    WindowsScannerCapabilities Capabilities { get; }
    IReadOnlyList<string> Configure(WindowsScanRequest request);
    IReadOnlyList<string> Acquire(WindowsScanRequest request, int firstPageNumber);
}

/// <summary>WIA acquisition; a batch owns one device connection and preserves completed pages on interruption.</summary>
public sealed class WindowsScannerService
{
    private readonly Func<string, WindowsScanSource, IWindowsScanSession> _connect;
    public WindowsScannerService() : this((id, source) => new WiaScanSession(id, source)) { }
    internal WindowsScannerService(Func<string, WindowsScanSource, IWindowsScanSession> connect) => _connect = connect;

    public Task<IReadOnlyList<WindowsScannerDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(cancellationToken), cancellationToken);

    public Task<WindowsScannerCapabilities> GetCapabilitiesAsync(string deviceId,
        WindowsScanSource source = WindowsScanSource.Flatbed, CancellationToken cancellationToken = default) =>
        Task.Run(() => { using var session = _connect(deviceId, source); return session.Capabilities; }, cancellationToken);

    public async Task<string> ScanAsync(WindowsScanRequest request, CancellationToken cancellationToken = default)
    {
        var result = await ScanBatchAsync(request with { MaximumPages = request.Duplex ? 2 : 1 }, cancellationToken);
        if (result.Canceled) throw new OperationCanceledException(cancellationToken);
        if (result.Paths.Count == 0) throw new InvalidOperationException(result.Error ?? LocalizationService.T("ScanPrintNoPages"));
        return result.Paths[0];
    }

    public Task<WindowsScanBatchResult> ScanBatchAsync(WindowsScanRequest request,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Dpi, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Dpi, 2400);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumPages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MaximumPages, 500);
        if (request.Duplex && request.Source != WindowsScanSource.Feeder)
            throw new ArgumentException(LocalizationService.T("ScanPrintDuplexRequiresFeeder"), nameof(request));
        if (request.Duplex && request.MaximumPages % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The duplex page limit must be even.");
        return Task.Run(() => ScanBatch(request, progress, cancellationToken), cancellationToken);
    }

    // Retain the old cancellation-token overload for callers using positional arguments.
    public Task<WindowsScanBatchResult> ScanBatchAsync(WindowsScanRequest request, CancellationToken cancellationToken) =>
        ScanBatchAsync(request, null, cancellationToken);

    private WindowsScanBatchResult ScanBatch(WindowsScanRequest request, IProgress<int>? progress, CancellationToken token)
    {
        var paths = new List<string>();
        var warnings = new List<string>();
        try
        {
            token.ThrowIfCancellationRequested();
            using var session = _connect(request.DeviceId, request.Source);
            if (request.Source == WindowsScanSource.Feeder && !session.Capabilities.HasFeeder)
                throw new NotSupportedException(LocalizationService.T("ScanPrintNoFeeder"));
            if (request.Duplex && !session.Capabilities.HasDuplex)
                throw new NotSupportedException(LocalizationService.T("ScanPrintNoDuplex"));
            warnings.AddRange(session.Configure(request));
            while (paths.Count < request.MaximumPages)
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<string> pages;
                try { pages = session.Acquire(request, paths.Count + 1); }
                catch (COMException ex) when (ex.HResult == PaperEmpty)
                {
                    return new(paths, warnings, paths.Count == 0 ? LocalizationService.T("ScanPrintPaperEmpty") : null);
                }
                catch (ScanTransferException ex)
                {
                    paths.AddRange(ex.Paths);
                    progress?.Report(paths.Count);
                    return new(paths, warnings, DescribeError(ex.InnerException ?? ex));
                }
                if (pages.Count == 0) throw new InvalidDataException(LocalizationService.T("ScanPrintNoPages"));
                paths.AddRange(pages);
                progress?.Report(paths.Count);
                if (request.Source == WindowsScanSource.Flatbed) break;
            }
            if (request.Source == WindowsScanSource.Feeder && paths.Count >= request.MaximumPages)
                warnings.Add(LocalizationService.T("ScanPrintPageLimit", paths.Count));
            return new(paths, warnings);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(paths, warnings, Canceled: true); }
        catch (Exception ex) when (paths.Count > 0) { return new(paths, warnings, DescribeError(ex)); }
    }

    internal static string DescribeError(Exception exception) => exception.HResult switch
    {
        PaperEmpty => LocalizationService.T("ScanPrintPaperEmpty"),
        unchecked((int)0x80210002) => LocalizationService.T("ScanPrintPaperJam"),
        unchecked((int)0x80210006) => LocalizationService.T("ScanPrintDeviceBusy"),
        _ => exception.Message
    };

    private static IReadOnlyList<WindowsScannerDeviceInfo> Discover(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var scope = new ComScope();
        try
        {
            dynamic manager = scope.Create("WIA.DeviceManager");
            dynamic infos = scope.Own((object)manager.DeviceInfos);
            var result = new List<WindowsScannerDeviceInfo>();
            for (var index = 1; index <= (int)infos.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                using var entry = new ComScope();
                dynamic info = entry.Own((object)infos[index]);
                if ((int)info.Type != 1) continue;
                var id = Convert.ToString(info.DeviceID) ?? string.Empty;
                if (id.Length > 0) result.Add(new(id, ReadString(info, "Name") ?? id,
                    ReadString(info, "Description"), ReadString(info, "Port")));
            }
            return result;
        }
        catch (COMException) { return []; }
    }

    private sealed class WiaScanSession : IWindowsScanSession
    {
        private readonly ComScope _scope = new();
        private readonly object _device;
        private readonly object _item;
        private readonly bool _categorizedItem;
        public WindowsScannerCapabilities Capabilities { get; }

        public WiaScanSession(string deviceId, WindowsScanSource source)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(LocalizationService.T("ConversionUiWiaScanningWindowsOnly"));
            try
            {
                dynamic manager = _scope.Create("WIA.DeviceManager");
                dynamic infos = _scope.Own((object)manager.DeviceInfos);
                object? device = null;
                for (var index = 1; index <= (int)infos.Count; index++)
                {
                    using var entry = new ComScope();
                    dynamic info = entry.Own((object)infos[index]);
                    if ((int)info.Type == 1 && string.Equals(Convert.ToString(info.DeviceID), deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        device = _scope.Own((object)info.Connect());
                        break;
                    }
                }
                _device = device ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiScannerNotFound"));
                dynamic items = _scope.Own((object)((dynamic)_device).Items);
                object? first = null, flatbed = null, feeder = null;
                for (var index = 1; index <= (int)items.Count; index++)
                {
                    var item = _scope.Own((object)items[index]);
                    first ??= item;
                    var category = ReadString(item, 4125);
                    if (string.Equals(category, FlatbedCategory, StringComparison.OrdinalIgnoreCase)) flatbed = item;
                    if (string.Equals(category, FeederCategory, StringComparison.OrdinalIgnoreCase)) feeder = item;
                }
                var flags = ReadInt(_device, 3086) ?? 0;
                _item = (source == WindowsScanSource.Feeder ? feeder : flatbed) ?? first
                    ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiScannerNoValidImage"));
                _categorizedItem = ReadString(_item, 4125) is { Length: > 0 };
                var hasFeeder = feeder is not null || (flags & 1) != 0;
                var duplexFlags = ReadValidFlags(feeder ?? _item, 3088);
                Capabilities = new(flatbed is not null || (flags & 2) != 0 || !hasFeeder,
                    hasFeeder, (flags & 4) != 0 || (duplexFlags & 4) != 0, ReadResolutions(_item));
            }
            catch { _scope.Dispose(); throw; }
        }

        public IReadOnlyList<string> Configure(WindowsScanRequest request)
        {
            var warnings = new List<string>();
            var sides = request.Duplex ? 4 | 8 : 32; // DUPLEX | FRONT_FIRST, or FRONT_ONLY.
            if (request.Source == WindowsScanSource.Feeder)
            {
                var selected = _categorizedItem
                    ? WriteProperty(_item, 3088, sides)
                    : WriteProperty(_device, 3088, 1 | sides);
                if (!selected) throw new NotSupportedException(LocalizationService.T(request.Duplex ? "ScanPrintNoDuplex" : "ScanPrintNoFeeder"));
                // WIA counts images, so one duplex sheet requires two images. Requesting
                // a finite sheet lets cancellation and page limits apply between transfers.
                if (!WriteProperty(_item, 3096, request.Duplex ? 2 : 1) && !WriteProperty(_device, 3096, request.Duplex ? 2 : 1))
                    throw new NotSupportedException(LocalizationService.T("ScanPrintBatchUnsupported"));
            }
            else if (!_categorizedItem) WriteProperty(_device, 3088, 2);
            var xSet = WriteProperty(_item, 6147, request.Dpi);
            var ySet = WriteProperty(_item, 6148, request.Dpi);
            if (!xSet || !ySet || ReadInt(_item, 6147) != request.Dpi || ReadInt(_item, 6148) != request.Dpi) warnings.Add(LocalizationService.T("ScanPrintDpiFallback", ReadInt(_item, 6147) ?? 0, ReadInt(_item, 6148) ?? 0));
            if (!WriteProperty(_item, 6146, (int)request.Color))
                warnings.Add(LocalizationService.T("ScanPrintColorFallback"));
            return warnings;
        }

        public IReadOnlyList<string> Acquire(WindowsScanRequest request, int firstPageNumber)
        {
            using var transfer = new ComScope();
            dynamic image = transfer.Own((object)((dynamic)_item).Transfer(ResolveFormatId(request.Duplex ? "tiff" : request.Format)));
            var count = Math.Max(1, Convert.ToInt32(image.FrameCount));
            var paths = new List<string>();
            try
            {
                for (var frame = 1; frame <= count; frame++)
                {
                    using var page = new ComScope();
                    object pageImage = image;
                    if (count > 1)
                    {
                        image.ActiveFrame = frame;
                        dynamic pixels = page.Own((object)image.ARGBData);
                        pageImage = page.Own((object)pixels.ImageFile((int)image.Width, (int)image.Height));
                    }
                    // Transfer may return the device's preferred format. Explicit conversion
                    // guarantees that the on-disk encoding matches each page's extension.
                    dynamic processor = page.Create("WIA.ImageProcess");
                    dynamic filterInfos = page.Own((object)processor.FilterInfos);
                    dynamic convertInfo = page.Own((object)filterInfos["Convert"]);
                    dynamic filters = page.Own((object)processor.Filters);
                    filters.Add((string)convertInfo.FilterID);
                    dynamic filter = page.Own((object)filters[1]);
                    if (!WriteProperty(filter, "FormatID", ResolveFormatId(request.Format)))
                        throw new InvalidDataException(LocalizationService.T("ConversionUiScannerNoValidImage"));
                    dynamic converted = page.Own((object)processor.Apply(pageImage));
                    var path = PagePath(request.OutputPath, firstPageNumber + frame - 1, request.Source == WindowsScanSource.Feeder || count > 1);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        converted.SaveFile(staging);
                        if (!File.Exists(staging) || new FileInfo(staging).Length == 0)
                            throw new InvalidDataException(LocalizationService.T("ConversionUiScannerNoValidImage"));
                        File.Move(staging, path, overwrite: false);
                        paths.Add(path);
                    }
                    finally { if (File.Exists(staging)) File.Delete(staging); }
                }
                return paths;
            }
            catch (Exception ex) when (paths.Count > 0) { throw new ScanTransferException(paths, ex); }
        }
        public void Dispose() => _scope.Dispose();
    }

    internal static string PagePath(string outputPath, int page, bool numberPages) => numberPages
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $"{Path.GetFileNameWithoutExtension(outputPath)}-{page:0000}{Path.GetExtension(outputPath)}")
        : Path.GetFullPath(outputPath);

    internal static string? ReadString(object owner, object key)
    {
        using var scope = new ComScope();
        try
        {
            dynamic property = FindProperty(scope, owner, key);
            return Convert.ToString(property.Value);
        }
        catch (COMException) { return null; }
    }
    private static int? ReadInt(object owner, object key) => int.TryParse(ReadString(owner, key), out var value) ? value : null;
    private static int ReadValidFlags(object owner, int key)
    {
        using var scope = new ComScope();
        try
        {
            dynamic property = FindProperty(scope, owner, key);
            return Convert.ToInt32(property.SubTypeValues);
        }
        catch (COMException) { return 0; }
        catch (InvalidCastException) { return 0; }
    }
    private static IReadOnlyList<int> ReadResolutions(object item)
    {
        using var scope = new ComScope();
        try
        {
            dynamic property = FindProperty(scope, item, 6147);
            var subtype = Convert.ToInt32(property.SubType);
            if (subtype == 2)
            {
                dynamic values = scope.Own((object)property.SubTypeValues);
                var list = new List<int>();
                for (var index = 1; index <= (int)values.Count; index++) list.Add(Convert.ToInt32(values[index]));
                return list.Where(value => value is >= 50 and <= 2400).Distinct().Order().ToArray();
            }
            if (subtype == 1)
            {
                int min = Convert.ToInt32(property.SubTypeMin), max = Convert.ToInt32(property.SubTypeMax), step = Math.Max(1, Convert.ToInt32(property.SubTypeStep));
                return new[] { min, 75, 100, 150, 200, 300, 600, 1200, max }.Where(value => value >= min && value <= max && value is >= 50 and <= 2400 && (value - min) % step == 0).Distinct().Order().ToArray();
            }
        }
        catch (COMException) { }
        return [150, 200, 300, 600];
    }
    internal static bool WriteProperty(object owner, object key, object value)
    {
        using var scope = new ComScope();
        try
        {
            dynamic property = FindProperty(scope, owner, key);
            property.Value = value;
            return true;
        }
        catch (COMException) { return false; }
    }

    // WIA Automation numeric collection keys are ordinal positions, not WIA property IDs.
    // Resolve PropertyID explicitly so sparse/reordered driver property lists work.
    private static object FindProperty(ComScope scope, object owner, object key)
    {
        dynamic properties = scope.Own((object)((dynamic)owner).Properties);
        if (key is not int propertyId) return scope.Own((object)properties[key]);
        for (var index = 1; index <= (int)properties.Count; index++)
        {
            dynamic property = scope.Own((object)properties[index]);
            if (Convert.ToInt32(property.PropertyID) == propertyId) return property;
        }
        throw new COMException("The scanner property is unavailable.", unchecked((int)0x80070057));
    }
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        public object Own(object value) { _objects.Add(value); return value; }
        public object Create(string progId)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return Own(Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)!)
                ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiCannotCreateWiaDeviceManager")));
        }
        public void Dispose()
        {
            for (var index = _objects.Count - 1; index >= 0; index--)
                if (OperatingSystem.IsWindows() && Marshal.IsComObject(_objects[index])) Marshal.ReleaseComObject(_objects[index]);
            _objects.Clear();
        }
    }
    internal static string ResolveFormatId(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "bmp" => "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}",
        "jpg" or "jpeg" => "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}",
        "tif" or "tiff" => "{B96B3CB1-0728-11D3-9D7B-0000F81EF32E}",
        _ => "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}"
    };
    private const int PaperEmpty = unchecked((int)0x80210003);
    private const string FlatbedCategory = "{FB607B1F-43F3-488B-855B-FB703EC342A6}";
    private const string FeederCategory = "{FE131934-F84C-42AD-8DA4-6129CDDD7288}";
}




