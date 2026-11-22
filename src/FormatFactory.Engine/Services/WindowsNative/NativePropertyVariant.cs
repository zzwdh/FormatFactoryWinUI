using System.Globalization;
using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct NativePropertyVariant
{
    [FieldOffset(0)]
    public ushort VariantType;

    [FieldOffset(8)]
    public byte ByteValue;

    [FieldOffset(8)]
    public ushort UShortValue;

    [FieldOffset(8)]
    public uint UIntValue;

    [FieldOffset(8)]
    public ulong ULongValue;

    [FieldOffset(8)]
    public short ShortValue;

    [FieldOffset(8)]
    public int IntValue;

    [FieldOffset(8)]
    public long LongValue;

    [FieldOffset(8)]
    public double DoubleValue;

    [FieldOffset(8)]
    public IntPtr PointerValue;
}

internal static partial class NativePropertyValue
{
    private const ushort VtEmpty = 0;
    private const ushort VtUi1 = 17;
    private const ushort VtUi2 = 18;
    private const ushort VtUi4 = 19;
    private const ushort VtUi8 = 21;
    private const ushort VtI2 = 2;
    private const ushort VtI4 = 3;
    private const ushort VtI8 = 20;
    private const ushort VtR8 = 5;
    private const ushort VtBool = 11;
    private const ushort VtLpWStr = 31;
    private const ushort VtBStr = 8;

    public static ulong ToUInt64(in NativePropertyVariant value) => value.VariantType switch
    {
        VtUi1 => value.ByteValue,
        VtUi2 => value.UShortValue,
        VtUi4 => value.UIntValue,
        VtUi8 => value.ULongValue,
        VtI2 when value.ShortValue >= 0 => (ulong)value.ShortValue,
        VtI4 when value.IntValue >= 0 => (ulong)value.IntValue,
        VtI8 when value.LongValue >= 0 => (ulong)value.LongValue,
        _ => 0
    };

    public static ushort ToUInt16(in NativePropertyVariant value)
    {
        var converted = ToUInt64(value);
        return converted <= ushort.MaxValue ? (ushort)converted : (ushort)0;
    }

    public static string ToDisplayString(ref NativePropertyVariant value)
    {
        if (value.VariantType == VtEmpty)
        {
            return string.Empty;
        }

        if (OperatingSystem.IsWindows())
        {
            IntPtr allocated = IntPtr.Zero;
            try
            {
                if (PropVariantToStringAlloc(ref value, out allocated) >= 0 && allocated != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(allocated) ?? string.Empty;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            finally
            {
                if (allocated != IntPtr.Zero)
                {
                    CoTaskMemFree(allocated);
                }
            }
        }

        return value.VariantType switch
        {
            VtUi1 => value.ByteValue.ToString(CultureInfo.InvariantCulture),
            VtUi2 => value.UShortValue.ToString(CultureInfo.InvariantCulture),
            VtUi4 => value.UIntValue.ToString(CultureInfo.InvariantCulture),
            VtUi8 => value.ULongValue.ToString(CultureInfo.InvariantCulture),
            VtI2 => value.ShortValue.ToString(CultureInfo.InvariantCulture),
            VtI4 => value.IntValue.ToString(CultureInfo.InvariantCulture),
            VtI8 => value.LongValue.ToString(CultureInfo.InvariantCulture),
            VtR8 => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
            VtBool => value.ShortValue == 0 ? "False" : "True",
            VtLpWStr when value.PointerValue != IntPtr.Zero => Marshal.PtrToStringUni(value.PointerValue) ?? string.Empty,
            VtBStr when value.PointerValue != IntPtr.Zero => Marshal.PtrToStringBSTR(value.PointerValue),
            _ => string.Empty
        };
    }

    public static void Clear(ref NativePropertyVariant value)
    {
        if (!OperatingSystem.IsWindows() || value.VariantType == VtEmpty)
        {
            value = default;
            return;
        }

        try
        {
            PropVariantClear(ref value);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        finally
        {
            value = default;
        }
    }

    [LibraryImport("propsys.dll")]
    private static partial int PropVariantToStringAlloc(ref NativePropertyVariant propVariant, out IntPtr value);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref NativePropertyVariant propVariant);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(IntPtr memory);
}
