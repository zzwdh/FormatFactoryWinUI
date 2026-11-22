using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public sealed partial class WindowsImagingNativeService : IWindowsImagingNativeService
{
    private const uint ClsctxInprocServer = 0x1;
    private const uint GenericRead = 0x80000000;
    private const uint WicDecoder = 0x1;
    private const uint WicEncoder = 0x2;
    private static readonly Guid WicFactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid WicFactoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid WicBitmapCodecInfoInterface = new("e87a44c4-b76e-4c47-8b09-298eb12a2714");
    private static readonly string[] OrientationQueries =
    [
        "/app1/ifd/{ushort=274}",
        "/ifd/{ushort=274}",
        "/xmp/tiff:Orientation"
    ];

    public NativeCapabilityResult<IReadOnlyList<WicCodecInfo>> EnumerateCodecs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<IReadOnlyList<WicCodecInfo>>.NotSupported(
                "Windows Imaging Component is available only on Windows.");
        }

        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null)
        {
            return new NativeCapabilityResult<IReadOnlyList<WicCodecInfo>>(
                comResult.Status,
                null,
                comResult.Message);
        }

        using var com = comResult.Value;
        IntPtr factory = IntPtr.Zero;
        try
        {
            var factoryResult = CreateFactory(out factory);
            if (factoryResult < 0 || factory == IntPtr.Zero)
            {
                return NativeCapabilityResult<IReadOnlyList<WicCodecInfo>>.Unavailable(
                    $"WIC factory creation failed (0x{factoryResult:X8}).");
            }

            var codecs = new List<WicCodecInfo>();
            EnumerateCodecKind(factory, WicDecoder, WicCodecKind.Decoder, codecs);
            EnumerateCodecKind(factory, WicEncoder, WicCodecKind.Encoder, codecs);
            var distinct = codecs
                .DistinctBy(codec => (codec.ClassId, codec.Kind))
                .OrderBy(codec => codec.Kind)
                .ThenBy(codec => codec.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return NativeCapabilityResult<IReadOnlyList<WicCodecInfo>>.Available(
                distinct,
                $"Detected {distinct.Length} Windows Imaging codecs.");
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<WicCodecInfo>>.Unavailable(
                "Windows Imaging Component runtime was not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<WicCodecInfo>>.Unavailable(
                "Windows Imaging Component activation is unavailable.");
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<IReadOnlyList<WicCodecInfo>>.Failed(exception.Message);
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }
        }
    }

    public Task<NativeCapabilityResult<WicImageMetadata>> ReadMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(NativeCapabilityResult<WicImageMetadata>.Failed("An image path is required."));
        }

        return Task.Run(() => ReadMetadata(path, cancellationToken), cancellationToken);
    }

    public static ushort NormalizeExifOrientation(ushort orientation) =>
        orientation is >= 1 and <= 8 ? orientation : (ushort)1;

    private static NativeCapabilityResult<WicImageMetadata> ReadMetadata(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<WicImageMetadata>.NotSupported(
                "Windows Imaging Component is available only on Windows.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return NativeCapabilityResult<WicImageMetadata>.Failed("The image does not exist.");
        }

        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null)
        {
            return new NativeCapabilityResult<WicImageMetadata>(comResult.Status, null, comResult.Message);
        }

        using var com = comResult.Value;
        IntPtr factory = IntPtr.Zero;
        IntPtr decoder = IntPtr.Zero;
        IntPtr frame = IntPtr.Zero;
        try
        {
            var factoryResult = CreateFactory(out factory);
            if (factoryResult < 0 || factory == IntPtr.Zero)
            {
                return NativeCapabilityResult<WicImageMetadata>.Unavailable(
                    $"WIC factory creation failed (0x{factoryResult:X8}).");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var createDecoder = GetDelegate<CreateDecoderFromFilenameDelegate>(factory, 3);
            var decodeResult = createDecoder(
                factory,
                fullPath,
                IntPtr.Zero,
                GenericRead,
                0,
                out decoder);
            if (decodeResult < 0 || decoder == IntPtr.Zero)
            {
                return NativeCapabilityResult<WicImageMetadata>.Unavailable(
                    $"WIC could not decode this image (0x{decodeResult:X8}).");
            }

            var getFrame = GetDelegate<GetFrameDelegate>(decoder, 13);
            var frameResult = getFrame(decoder, 0, out frame);
            if (frameResult < 0 || frame == IntPtr.Zero)
            {
                return NativeCapabilityResult<WicImageMetadata>.Unavailable(
                    $"WIC could not read the first image frame (0x{frameResult:X8}).");
            }

            var getSize = GetDelegate<GetSizeDelegate>(frame, 3);
            _ = getSize(frame, out var width, out var height);
            var orientation = ReadExifOrientation(frame);
            var colorContexts = ReadColorContexts(factory, frame);
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = new WicImageMetadata(
                checked((int)width),
                checked((int)height),
                NormalizeExifOrientation(orientation),
                colorContexts);
            return NativeCapabilityResult<WicImageMetadata>.Available(
                metadata,
                $"WIC read {width}×{height}, EXIF orientation {metadata.ExifOrientation}, and {colorContexts.Count} color contexts.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<WicImageMetadata>.Unavailable(
                "Windows Imaging Component runtime was not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<WicImageMetadata>.Unavailable(
                "Windows Imaging Component activation is unavailable.");
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<WicImageMetadata>.Failed(exception.Message);
        }
        finally
        {
            if (frame != IntPtr.Zero) Marshal.Release(frame);
            if (decoder != IntPtr.Zero) Marshal.Release(decoder);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    private static int CreateFactory(out IntPtr factory)
    {
        var classId = WicFactoryClass;
        var interfaceId = WicFactoryInterface;
        return CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            ClsctxInprocServer,
            ref interfaceId,
            out factory);
    }

    private static void EnumerateCodecKind(
        IntPtr factory,
        uint componentType,
        WicCodecKind kind,
        ICollection<WicCodecInfo> output)
    {
        var createEnumerator = GetDelegate<CreateComponentEnumeratorDelegate>(factory, 23);
        if (createEnumerator(factory, componentType, 0, out var enumerator) < 0 || enumerator == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var next = GetDelegate<EnumNextDelegate>(enumerator, 3);
            while (next(enumerator, 1, out var component, out var fetched) >= 0
                && fetched == 1
                && component != IntPtr.Zero)
            {
                IntPtr codecInfo = IntPtr.Zero;
                try
                {
                    var interfaceId = WicBitmapCodecInfoInterface;
                    if (Marshal.QueryInterface(component, in interfaceId, out codecInfo) < 0
                        || codecInfo == IntPtr.Zero)
                    {
                        continue;
                    }

                    var classId = ReadGuid(codecInfo, 4);
                    output.Add(new WicCodecInfo(
                        classId,
                        kind,
                        ReadString(codecInfo, 10),
                        ReadString(codecInfo, 6),
                        ReadString(codecInfo, 8),
                        ReadString(codecInfo, 16),
                        ReadString(codecInfo, 17),
                        ReadBoolean(codecInfo, 18),
                        ReadBoolean(codecInfo, 20),
                        ReadBoolean(codecInfo, 21)));
                }
                finally
                {
                    if (codecInfo != IntPtr.Zero)
                    {
                        Marshal.Release(codecInfo);
                    }
                    Marshal.Release(component);
                }
            }
        }
        finally
        {
            Marshal.Release(enumerator);
        }
    }

    private static ushort ReadExifOrientation(IntPtr frame)
    {
        var getMetadataReader = GetDelegate<GetMetadataQueryReaderDelegate>(frame, 8);
        if (getMetadataReader(frame, out var reader) < 0 || reader == IntPtr.Zero)
        {
            return 1;
        }

        try
        {
            var getMetadata = GetDelegate<GetMetadataByNameDelegate>(reader, 5);
            foreach (var query in OrientationQueries)
            {
                var value = default(NativePropertyVariant);
                try
                {
                    if (getMetadata(reader, query, out value) >= 0)
                    {
                        var orientation = NativePropertyValue.ToUInt16(value);
                        if (orientation is >= 1 and <= 8)
                        {
                            return orientation;
                        }
                    }
                }
                finally
                {
                    NativePropertyValue.Clear(ref value);
                }
            }
        }
        finally
        {
            Marshal.Release(reader);
        }

        return 1;
    }

    private static IReadOnlyList<WicColorContextInfo> ReadColorContexts(IntPtr factory, IntPtr frame)
    {
        var getColorContexts = GetDelegate<GetColorContextsDelegate>(frame, 9);
        if (getColorContexts(frame, 0, IntPtr.Zero, out var count) < 0 || count == 0)
        {
            return [];
        }

        var array = Marshal.AllocCoTaskMem(checked((int)count * IntPtr.Size));
        var allocatedContexts = new IntPtr[count];
        var contexts = new List<WicColorContextInfo>(checked((int)count));
        try
        {
            for (var index = 0u; index < count; index++)
            {
                var createContext = GetDelegate<CreateColorContextDelegate>(factory, 15);
                if (createContext(factory, out allocatedContexts[index]) < 0) return [];
                Marshal.WriteIntPtr(array, checked((int)index * IntPtr.Size), allocatedContexts[index]);
            }

            if (getColorContexts(frame, count, array, out var actual) < 0)
            {
                return [];
            }

            for (var index = 0u; index < Math.Min(count, actual); index++)
            {
                var context = Marshal.ReadIntPtr(array, checked((int)index * IntPtr.Size));
                if (context == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var getType = GetDelegate<GetColorContextTypeDelegate>(context, 6);
                    var getProfileBytes = GetDelegate<GetProfileBytesDelegate>(context, 7);
                    var getExifColorSpace = GetDelegate<GetExifColorSpaceDelegate>(context, 8);
                    _ = getType(context, out var type);
                    var profileBytes = 0u;
                    var exifColorSpace = 0u;
                    if (type == 1)
                    {
                        _ = getProfileBytes(context, 0, IntPtr.Zero, out profileBytes);
                    }
                    else if (type == 2)
                    {
                        _ = getExifColorSpace(context, out exifColorSpace);
                    }

                    contexts.Add(new WicColorContextInfo(
                        type switch
                        {
                            1 => "Profile",
                            2 => "ExifColorSpace",
                            _ => "Uninitialized"
                        },
                        checked((int)profileBytes),
                        exifColorSpace));
                }
                finally
                {
                    // All contexts are owned by the caller and released together below.
                }
            }
        }
        finally
        {
            foreach (var context in allocatedContexts) if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.FreeCoTaskMem(array);
        }

        return contexts;
    }

    private static Guid ReadGuid(IntPtr component, int slot)
    {
        var getter = GetDelegate<GetGuidDelegate>(component, slot);
        return getter(component, out var value) >= 0 ? value : Guid.Empty;
    }

    private static bool ReadBoolean(IntPtr component, int slot)
    {
        var getter = GetDelegate<GetBooleanDelegate>(component, slot);
        return getter(component, out var value) >= 0 && value != 0;
    }

    private static string ReadString(IntPtr component, int slot)
    {
        var getter = GetDelegate<GetStringDelegate>(component, slot);
        if (getter(component, 0, IntPtr.Zero, out var length) < 0 || length == 0)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocCoTaskMem(checked((int)length * sizeof(char)));
        try
        {
            return getter(component, length, buffer, out _) >= 0
                ? Marshal.PtrToStringUni(buffer) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateComponentEnumeratorDelegate(
        IntPtr self,
        uint componentTypes,
        uint options,
        out IntPtr enumerator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumNextDelegate(IntPtr self, uint count, out IntPtr value, out uint fetched);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuidDelegate(IntPtr self, out Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBooleanDelegate(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStringDelegate(IntPtr self, uint characterCount, IntPtr value, out uint actual);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateDecoderFromFilenameDelegate(
        IntPtr self,
        string fileName,
        IntPtr vendor,
        uint desiredAccess,
        uint metadataOptions,
        out IntPtr decoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrameDelegate(IntPtr self, uint index, out IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSizeDelegate(IntPtr self, out uint width, out uint height);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMetadataQueryReaderDelegate(IntPtr self, out IntPtr reader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetMetadataByNameDelegate(
        IntPtr self,
        string query,
        out NativePropertyVariant value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetColorContextsDelegate(
        IntPtr self,
        uint count,
        IntPtr contexts,
        out uint actual);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetColorContextTypeDelegate(IntPtr self, out uint type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateColorContextDelegate(IntPtr self, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetProfileBytesDelegate(
        IntPtr self,
        uint bufferSize,
        IntPtr buffer,
        out uint actual);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetExifColorSpaceDelegate(IntPtr self, out uint colorSpace);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);
}
