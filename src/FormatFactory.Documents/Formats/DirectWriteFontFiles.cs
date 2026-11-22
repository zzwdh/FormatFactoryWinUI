using System.Runtime.InteropServices;
using System.Text;

namespace FormatFactoryWinUI.Documents;

internal sealed record SystemFontFile(string Path, uint FaceIndex);

/// <summary>Resolves installed fonts, including per-user fonts and TTC faces, through DirectWrite.</summary>
internal static class DirectWriteFontFiles
{
    public static IReadOnlyList<SystemFontFile> Discover()
    {
        if (!OperatingSystem.IsWindows()) return [];
        nint factory = 0, collection = 0;
        var files = new HashSet<SystemFontFile>();
        try
        {
            var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            Marshal.ThrowExceptionForHR(DWriteCreateFactory(0, ref iid, out factory));
            Marshal.ThrowExceptionForHR(Call<GetCollection>(factory, 3)(factory, out collection, false));
            var count = Call<GetCount>(collection, 3)(collection);
            for (uint index = 0; index < count; index++)
            {
                nint family = 0, font = 0, face = 0;
                try
                {
                    if (Call<GetAt>(collection, 4)(collection, index, out family) < 0) continue;
                    // Select the regular face; fallback is selected by actual character coverage below.
                    if (Call<MatchFont>(family, 7)(family, 400, 5, 0, out font) < 0) continue;
                    if (Call<GetObject>(font, 13)(font, out face) < 0) continue;
                    var faceIndex = Call<GetCount>(face, 5)(face);
                    uint fileCount = 0;
                    if (Call<GetFiles>(face, 4)(face, ref fileCount, 0) < 0 || fileCount != 1) continue;
                    var array = Marshal.AllocCoTaskMem(IntPtr.Size);
                    Marshal.WriteIntPtr(array, 0);
                    try
                    {
                        if (Call<GetFiles>(face, 4)(face, ref fileCount, array) < 0) continue;
                        var file = Marshal.ReadIntPtr(array);
                        try
                        {
                            var path = ReadPath(file);
                            if (path is not null) files.Add(new SystemFontFile(path, faceIndex));
                        }
                        finally { Release(file); }
                    }
                    finally { Marshal.FreeCoTaskMem(array); }
                }
                finally { Release(face); Release(font); Release(family); }
            }
        }
        catch (COMException) { }
        catch (DllNotFoundException) { }
        finally { Release(collection); Release(factory); }
        return files.OrderBy(file => Preference(file.Path)).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int Preference(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant() switch
    {
        "segoeui" => 0, "arial" => 1, "msyh" => 2, "malgun" => 3, "meiryo" => 4, _ => 5
    };

    private static string? ReadPath(nint file)
    {
        nint loader = 0, local = 0;
        try
        {
            if (Call<GetKey>(file, 3)(file, out var key, out var size) < 0 ||
                Call<GetObject>(file, 4)(file, out loader) < 0) return null;
            var iid = new Guid("b2d9f3ec-c9fe-4a11-a2ec-d86208f7c0a2");
            if (Marshal.QueryInterface(loader, in iid, out local) < 0) return null;
            if (Call<GetPathLength>(local, 4)(local, key, size, out var length) < 0 || length > 32767) return null;
            var path = new StringBuilder(checked((int)length + 1));
            return Call<GetPath>(local, 5)(local, key, size, path, length + 1) >= 0 ? path.ToString() : null;
        }
        finally { Release(local); Release(loader); }
    }

    private static void Release(nint pointer) { if (pointer != 0) Marshal.Release(pointer); }
    private static T Call<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [DllImport("dwrite.dll")] private static extern int DWriteCreateFactory(uint type, ref Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCollection(nint self, out nint collection, [MarshalAs(UnmanagedType.Bool)] bool update);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint GetCount(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAt(nint self, uint index, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MatchFont(nint self, uint weight, uint stretch, uint style, out nint font);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFiles(nint self, ref uint count, nint files);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetKey(nint self, out nint key, out uint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPathLength(nint self, nint key, uint size, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetPath(nint self, nint key, uint size, StringBuilder path, uint length);
}
