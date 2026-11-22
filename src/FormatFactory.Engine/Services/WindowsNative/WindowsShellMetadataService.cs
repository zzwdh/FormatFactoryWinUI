using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public sealed partial class WindowsShellMetadataService : IWindowsShellMetadataService
{
    private static readonly Guid PropertyStoreInterface = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    public NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>> ReadProperties(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.NotSupported(
                "Windows Property System is available only on Windows.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Failed("A path is required.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Failed(
                "The file system item does not exist.");
        }

        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null)
        {
            return new NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>(
                comResult.Status,
                null,
                comResult.Message);
        }

        using var com = comResult.Value;
        IntPtr propertyStore = IntPtr.Zero;
        try
        {
            var interfaceId = PropertyStoreInterface;
            var openResult = SHGetPropertyStoreFromParsingName(
                fullPath,
                IntPtr.Zero,
                0,
                ref interfaceId,
                out propertyStore);
            if (openResult < 0 || propertyStore == IntPtr.Zero)
            {
                return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Unavailable(
                    $"Windows Property System could not open this item (0x{openResult:X8}).");
            }

            var getCount = GetDelegate<GetCountDelegate>(propertyStore, 3);
            if (getCount(propertyStore, out var count) < 0)
            {
                return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Unavailable(
                    "Windows Property System could not enumerate item properties.");
            }

            var getAt = GetDelegate<GetAtDelegate>(propertyStore, 4);
            var getValue = GetDelegate<GetValueDelegate>(propertyStore, 5);
            var properties = new List<ShellPropertyMetadata>(checked((int)count));
            for (var index = 0u; index < count; index++)
            {
                if (getAt(propertyStore, index, out var key) < 0)
                {
                    continue;
                }

                var value = default(NativePropertyVariant);
                try
                {
                    if (getValue(propertyStore, ref key, out value) < 0)
                    {
                        continue;
                    }

                    var displayValue = NativePropertyValue.ToDisplayString(ref value);
                    if (string.IsNullOrWhiteSpace(displayValue))
                    {
                        continue;
                    }

                    properties.Add(new ShellPropertyMetadata(
                        ReadCanonicalName(key),
                        displayValue));
                }
                finally
                {
                    NativePropertyValue.Clear(ref value);
                }
            }

            var ordered = properties
                .OrderBy(property => property.CanonicalName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Available(
                ordered,
                $"Read {ordered.Length} Windows shell properties.");
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Unavailable(
                "Windows Property System libraries were not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Unavailable(
                "Windows Property System is unavailable on this Windows version.");
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>.Failed(exception.Message);
        }
        finally
        {
            if (propertyStore != IntPtr.Zero)
            {
                Marshal.Release(propertyStore);
            }
        }
    }

    private static string ReadCanonicalName(PropertyKey key)
    {
        IntPtr name = IntPtr.Zero;
        try
        {
            return PSGetNameFromPropertyKey(ref key, out name) >= 0 && name != IntPtr.Zero
                ? Marshal.PtrToStringUni(name) ?? $"{key.FormatId:D}/{key.PropertyId}"
                : $"{key.FormatId:D}/{key.PropertyId}";
        }
        finally
        {
            if (name != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
    }

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountDelegate(IntPtr self, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAtDelegate(IntPtr self, uint index, out PropertyKey key);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueDelegate(
        IntPtr self,
        ref PropertyKey key,
        out NativePropertyVariant value);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindContext,
        uint flags,
        ref Guid interfaceId,
        out IntPtr propertyStore);

    [LibraryImport("propsys.dll")]
    private static partial int PSGetNameFromPropertyKey(ref PropertyKey key, out IntPtr canonicalName);
}
