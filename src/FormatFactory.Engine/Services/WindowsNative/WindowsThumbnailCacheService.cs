using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public sealed partial class WindowsThumbnailCacheService : IWindowsThumbnailCacheService
{
    private const uint ClsctxInprocServer = 0x1;
    private const uint WtsScaleToRequestedSize = 0x40;
    private const uint WtsScaleUp = 0x10000;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private static readonly Guid LocalThumbnailCacheClass = new("50ef4544-ac9f-4a8e-b21b-8a26180db13f");
    private static readonly Guid ThumbnailCacheInterface = new("f676c15d-596a-4ce2-8234-33996f445db1");
    private static readonly Guid ShellItemInterface = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public Task<NativeCapabilityResult<ThumbnailPixels>> GetThumbnailAsync(
        string path,
        uint requestedSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(NativeCapabilityResult<ThumbnailPixels>.Failed("A path is required."));
        }

        var normalizedSize = Math.Clamp(requestedSize, 16u, 4096u);
        return Task.Run(() => GetThumbnail(path, normalizedSize, cancellationToken), cancellationToken);
    }

    private static NativeCapabilityResult<ThumbnailPixels> GetThumbnail(
        string path,
        uint requestedSize,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<ThumbnailPixels>.NotSupported(
                "IThumbnailCache is available only on Windows.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return NativeCapabilityResult<ThumbnailPixels>.Failed("The file system item does not exist.");
        }

        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null)
        {
            return new NativeCapabilityResult<ThumbnailPixels>(comResult.Status, null, comResult.Message);
        }

        using var com = comResult.Value;
        IntPtr shellItem = IntPtr.Zero;
        IntPtr cache = IntPtr.Zero;
        IntPtr sharedBitmap = IntPtr.Zero;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellItemId = ShellItemInterface;
            var itemResult = SHCreateItemFromParsingName(
                fullPath,
                IntPtr.Zero,
                ref shellItemId,
                out shellItem);
            if (itemResult < 0 || shellItem == IntPtr.Zero)
            {
                return NativeCapabilityResult<ThumbnailPixels>.Unavailable(
                    $"Windows Shell could not open this item (0x{itemResult:X8}).");
            }

            var cacheClass = LocalThumbnailCacheClass;
            var cacheInterface = ThumbnailCacheInterface;
            var cacheResult = CoCreateInstance(
                ref cacheClass,
                IntPtr.Zero,
                ClsctxInprocServer,
                ref cacheInterface,
                out cache);
            if (cacheResult < 0 || cache == IntPtr.Zero)
            {
                return NativeCapabilityResult<ThumbnailPixels>.Unavailable(
                    $"IThumbnailCache activation failed (0x{cacheResult:X8}).");
            }

            var getThumbnail = GetDelegate<GetThumbnailDelegate>(cache, 3);
            var thumbnailResult = getThumbnail(
                cache,
                shellItem,
                requestedSize,
                WtsScaleToRequestedSize | WtsScaleUp,
                out sharedBitmap,
                out _,
                out _);
            if (thumbnailResult < 0 || sharedBitmap == IntPtr.Zero)
            {
                return NativeCapabilityResult<ThumbnailPixels>.Unavailable(
                    $"Windows thumbnail extraction failed (0x{thumbnailResult:X8}).");
            }

            var getBitmap = GetDelegate<GetSharedBitmapDelegate>(sharedBitmap, 3);
            var getSize = GetDelegate<GetBitmapSizeDelegate>(sharedBitmap, 4);
            var getFormat = GetDelegate<GetBitmapFormatDelegate>(sharedBitmap, 5);
            if (getBitmap(sharedBitmap, out var bitmap) < 0 || bitmap == IntPtr.Zero
                || getSize(sharedBitmap, out var size) < 0
                || size.Width <= 0
                || size.Height <= 0)
            {
                return NativeCapabilityResult<ThumbnailPixels>.Unavailable(
                    "Windows returned an empty cached thumbnail.");
            }

            _ = getFormat(sharedBitmap, out var alphaType);
            var pixels = CopyBitmapPixels(bitmap, size.Width, size.Height, alphaType == 2);
            cancellationToken.ThrowIfCancellationRequested();
            return pixels is null
                ? NativeCapabilityResult<ThumbnailPixels>.Unavailable(
                    "The cached thumbnail could not be copied to BGRA pixels.")
                : NativeCapabilityResult<ThumbnailPixels>.Available(
                    pixels,
                    $"Loaded a {pixels.Width}×{pixels.Height} thumbnail from IThumbnailCache.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<ThumbnailPixels>.Unavailable(
                "Windows Shell thumbnail libraries were not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<ThumbnailPixels>.Unavailable(
                "Windows Shell thumbnail APIs are unavailable.");
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<ThumbnailPixels>.Failed(exception.Message);
        }
        finally
        {
            if (sharedBitmap != IntPtr.Zero) Marshal.Release(sharedBitmap);
            if (cache != IntPtr.Zero) Marshal.Release(cache);
            if (shellItem != IntPtr.Zero) Marshal.Release(shellItem);
        }
    }

    private static ThumbnailPixels? CopyBitmapPixels(
        IntPtr bitmap,
        int width,
        int height,
        bool hasAlpha)
    {
        var stride = checked(width * 4);
        var buffer = new byte[checked(stride * height)];
        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = checked((uint)buffer.Length)
            }
        };

        var deviceContext = CreateCompatibleDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var copiedLines = GetDIBits(
                deviceContext,
                bitmap,
                0,
                checked((uint)height),
                buffer,
                ref bitmapInfo,
                DibRgbColors);
            return copiedLines == height
                ? new ThumbnailPixels(width, height, stride, buffer, hasAlpha)
                : null;
        }
        finally
        {
            DeleteDC(deviceContext);
        }
    }

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetThumbnailDelegate(
        IntPtr self,
        IntPtr shellItem,
        uint requestedSize,
        uint flags,
        out IntPtr sharedBitmap,
        out uint cacheFlags,
        out Guid thumbnailId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSharedBitmapDelegate(IntPtr self, out IntPtr bitmap);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBitmapSizeDelegate(IntPtr self, out NativeSize size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBitmapFormatDelegate(IntPtr self, out uint alphaType);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr shellItem);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfo bitmapInfo,
        uint usage);
}
