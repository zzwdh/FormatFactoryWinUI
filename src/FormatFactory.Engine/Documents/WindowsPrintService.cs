using System.ComponentModel;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

public sealed record WindowsPrinterInfo(string Name, string? ServerName, uint Attributes);

/// <summary>Enumerates installed printers and submits supported documents through the Windows shell print verb.</summary>
public sealed class WindowsPrintService
{
    public IReadOnlyList<WindowsPrinterInfo> DiscoverPrinters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        const uint flags = PrinterEnumLocal | PrinterEnumConnections;
        var firstResult = EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var required, out _);
        if (required == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (!firstResult && error != 0 && error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error);
            }

            return [];
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, required, out _, out var returned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var size = Marshal.SizeOf<PrinterInfo4>();
            var result = new List<WindowsPrinterInfo>(checked((int)returned));
            for (var index = 0; index < returned; index++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, checked((int)index * size)));
                var name = Marshal.PtrToStringUni(info.PrinterName);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new WindowsPrinterInfo(name, Marshal.PtrToStringUni(info.ServerName), info.Attributes));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public Task SubmitAsync(
        string documentPath,
        string printerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        var fullPath = Path.GetFullPath(documentPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiPrintDocumentNotFound"), fullPath);
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(LocalizationService.T("ConversionUiWindowsPrintingWindowsOnly"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskNoCloseProcess,
            Verb = "printto",
            File = fullPath,
            Parameters = $"\"{printerName}\"",
            Show = ShowNormal
        };
        if (!ShellExecuteEx(ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (info.Process != IntPtr.Zero)
        {
            CloseHandle(info.Process);
        }
        return Task.CompletedTask;
    }

    public static bool SupportsNativePreview(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".pdf" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".webp";

    public static IReadOnlyList<int> SelectPages(int pageCount, IEnumerable<(int First, int Last)> ranges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        var selected = ranges.ToArray();
        if (selected.Length == 0) return Enumerable.Range(1, pageCount).ToArray();
        var pages = new SortedSet<int>();
        foreach (var (first, last) in selected)
        {
            if (first < 1 || last < first || last > pageCount)
                throw new ArgumentOutOfRangeException(nameof(ranges), LocalizationService.T("ScanPrintInvalidPageRange", pageCount));
            for (var page = first; page <= last; page++) pages.Add(page);
        }
        return pages.ToArray();
    }
    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterEnumConnections = 0x00000004;
    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const int ShowNormal = 1;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(
        uint flags,
        string? name,
        uint level,
        IntPtr printerEnum,
        uint bufferSize,
        out uint required,
        out uint returned);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

