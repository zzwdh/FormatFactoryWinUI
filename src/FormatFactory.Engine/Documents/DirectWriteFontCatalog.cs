using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FormatFactoryWinUI.Documents;

public sealed record DirectWriteFontFamilyInfo(string FamilyName, uint FontCount);

/// <summary>Enumerates installed font families through DirectWrite.</summary>
public sealed class DirectWriteFontCatalog
{
    public IReadOnlyList<DirectWriteFontFamilyInfo> Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        IDWriteFactory? factory = null;
        IDWriteFontCollection? collection = null;
        try
        {
            var interfaceId = typeof(IDWriteFactory).GUID;
            Marshal.ThrowExceptionForHR(DWriteCreateFactory(DWriteFactoryType.Shared, ref interfaceId, out factory));
            Marshal.ThrowExceptionForHR(factory.GetSystemFontCollection(out collection, false));
            var result = new List<DirectWriteFontFamilyInfo>(checked((int)collection.GetFontFamilyCount()));
            for (uint index = 0; index < collection.GetFontFamilyCount(); index++)
            {
                IDWriteFontFamily? family = null;
                IDWriteLocalizedStrings? names = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.GetFontFamily(index, out family));
                    Marshal.ThrowExceptionForHR(family.GetFamilyNames(out names));
                    var name = ReadBestName(names);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.Add(new DirectWriteFontFamilyInfo(name, family.GetFontCount()));
                    }
                }
                finally
                {
                    Release(names);
                    Release(family);
                }
            }

            return result.OrderBy(item => item.FamilyName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (COMException)
        {
            return [];
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        finally
        {
            Release(collection);
            Release(factory);
        }
    }

    private static string ReadBestName(IDWriteLocalizedStrings names)
    {
        var locale = CultureInfo.CurrentUICulture.Name;
        var index = FindLocale(names, locale) ?? FindLocale(names, "en-us") ?? 0;
        Marshal.ThrowExceptionForHR(names.GetStringLength(index, out var length));
        var value = new StringBuilder(checked((int)length + 1));
        Marshal.ThrowExceptionForHR(names.GetString(index, value, length + 1));
        return value.ToString();
    }

    private static uint? FindLocale(IDWriteLocalizedStrings names, string locale)
    {
        Marshal.ThrowExceptionForHR(names.FindLocaleName(locale, out var index, out var exists));
        return exists ? index : null;
    }

    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private enum DWriteFactoryType : uint
    {
        Shared,
        Isolated
    }

    [DllImport("dwrite.dll", PreserveSig = true)]
    private static extern int DWriteCreateFactory(
        DWriteFactoryType factoryType,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IDWriteFactory factory);

    [ComImport]
    [Guid("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig]
        int GetSystemFontCollection(
            [MarshalAs(UnmanagedType.Interface)] out IDWriteFontCollection collection,
            [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
    }

    [ComImport]
    [Guid("A84CEE02-3EEA-4EEE-A827-87C1A02A0FCC")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig] uint GetFontFamilyCount();
        [PreserveSig] int GetFontFamily(uint index, [MarshalAs(UnmanagedType.Interface)] out IDWriteFontFamily family);
    }

    [ComImport]
    [Guid("DA20D8EF-812A-4C43-9802-62EC4ABD7ADD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        [PreserveSig] int GetFontCollection([MarshalAs(UnmanagedType.Interface)] out IDWriteFontCollection collection);
        [PreserveSig] uint GetFontCount();
        [PreserveSig] int GetFont(uint index, out IntPtr font);
        [PreserveSig] int GetFamilyNames([MarshalAs(UnmanagedType.Interface)] out IDWriteLocalizedStrings names);
    }

    [ComImport]
    [Guid("08256209-099A-4B34-B86D-C22B110E7771")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteLocalizedStrings
    {
        [PreserveSig] uint GetCount();
        [PreserveSig] int FindLocaleName(
            [MarshalAs(UnmanagedType.LPWStr)] string localeName,
            out uint index,
            [MarshalAs(UnmanagedType.Bool)] out bool exists);
        [PreserveSig] int GetLocaleNameLength(uint index, out uint length);
        [PreserveSig] int GetLocaleName(uint index, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder localeName, uint size);
        [PreserveSig] int GetStringLength(uint index, out uint length);
        [PreserveSig] int GetString(uint index, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size);
    }
}
