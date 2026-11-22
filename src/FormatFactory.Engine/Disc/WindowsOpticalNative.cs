using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Disc;

internal static class OpticalDevicePath
{
    public static string Normalize(string rootOrDevicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootOrDevicePath);
        var value = rootOrDevicePath.Trim().Trim('"').Replace('/', '\\');
        if (value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        if (value.Length < 2 || !char.IsAsciiLetter(value[0]) || value[1] != ':' ||
            (value.Length > 2 && value[2..].Any(character => character != '\\')))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiOpticalPathMustBeDriveRootOrDevice"), nameof(rootOrDevicePath));
        }

        return $@"\\.\{char.ToUpperInvariant(value[0])}:";
    }
}

internal static class CdTableOfContentsParser
{
    private const int HeaderSize = 4;
    private const int TrackEntrySize = 8;
    private const byte LeadOutTrackNumber = 0xAA;

    public static CdTableOfContents Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize + TrackEntrySize)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiCdTocIncomplete"));
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);
        var declaredLength = Math.Min(buffer.Length, payloadLength + 2);
        var firstTrack = buffer[2];
        var lastTrack = buffer[3];
        if (firstTrack == 0 || lastTrack < firstTrack)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiCdTocTrackRangeInvalid"));
        }

        var entries = new List<(byte Number, int StartLba, bool IsData)>();
        int? leadOut = null;
        for (var offset = HeaderSize; offset + TrackEntrySize <= declaredLength; offset += TrackEntrySize)
        {
            var number = buffer[offset + 2];
            var lba = MsfToLba(buffer[offset + 5], buffer[offset + 6], buffer[offset + 7]);
            if (number == LeadOutTrackNumber)
            {
                leadOut = lba;
                break;
            }

            if (number < firstTrack || number > lastTrack)
            {
                continue;
            }

            entries.Add((number, lba, (buffer[offset + 1] & 0x04) != 0));
        }

        entries = entries
            .GroupBy(entry => entry.Number)
            .Select(group => group.First())
            .OrderBy(entry => entry.Number)
            .ToList();
        if (entries.Count == 0 || leadOut is null || leadOut < entries[^1].StartLba)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiCdTocMissingTracks"));
        }

        var tracks = new List<CdTrackInfo>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var endLba = index + 1 < entries.Count ? entries[index + 1].StartLba : leadOut.Value;
            if (endLba < entry.StartLba)
            {
                throw new InvalidDataException(LocalizationService.T("ConversionUiCdTocTrackOrderInvalid"));
            }

            tracks.Add(new CdTrackInfo(entry.Number, entry.StartLba, endLba, entry.IsData));
        }

        return new CdTableOfContents(firstTrack, lastTrack, leadOut.Value, tracks.AsReadOnly());
    }

    internal static int MsfToLba(byte minute, byte second, byte frame) =>
        Math.Max(0, ((minute * 60) + second) * 75 + frame - 150);
}

internal static class CdAudioWave
{
    public const int SectorBytes = 2352;
    public const int SampleRate = 44100;
    public const short Channels = 2;
    public const short BitsPerSample = 16;
    public const int HeaderLength = 44;

    public static byte[] CreateHeader(long pcmLength)
    {
        if (pcmLength < 0 || pcmLength > uint.MaxValue - 36)
        {
            throw new ArgumentOutOfRangeException(nameof(pcmLength));
        }

        var header = new byte[HeaderLength];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)(pcmLength + 36)));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SampleRate);
        var blockAlign = checked((ushort)(Channels * BitsPerSample / 8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)(SampleRate * blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), (ushort)BitsPerSample);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), checked((uint)pcmLength));
        return header;
    }

    public static void ConvertBigEndianPcmToWaveInPlace(Span<byte> bytes)
    {
        if ((bytes.Length & 1) != 0)
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiCddaPcmRequiresCompleteSamples"), nameof(bytes));
        }

        for (var index = 0; index < bytes.Length; index += 2)
        {
            (bytes[index], bytes[index + 1]) = (bytes[index + 1], bytes[index]);
        }
    }
}

internal static partial class WindowsOpticalNative
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;

    internal const uint IoctlCdromReadToc = 0x00024000;
    internal const uint IoctlCdromRawRead = 0x0002403E;
    internal const uint IoctlDiskGetLengthInfo = 0x0007405C;
    internal const uint IoctlStorageMediaRemoval = 0x002D4804;
    internal const uint IoctlStorageEjectMedia = 0x002D4808;
    internal const uint FsctlLockVolume = 0x00090018;
    internal const uint FsctlUnlockVolume = 0x0009001C;
    internal const uint FsctlDismountVolume = 0x00090020;
    internal const uint FsctlAllowExtendedDasdIo = 0x00090083;

    internal const int ErrorHandleEof = 38;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        [In]
        byte[]? inputBuffer,
        uint inputBufferSize,
        [Out]
        byte[]? outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(
        SafeFileHandle file,
        [Out]
        byte[] buffer,
        uint bytesToRead,
        out uint bytesRead,
        IntPtr overlapped);

    internal static SafeFileHandle OpenDevice(string path, uint access)
    {
        var devicePath = OpticalDevicePath.Normalize(path);
        var handle = CreateFile(
            devicePath,
            access,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, LocalizationService.T("ConversionUiCannotOpenOpticalDevice", devicePath));
        }

        return handle;
    }

    internal static void Control(SafeFileHandle handle, uint controlCode, string operation)
    {
        if (!DeviceIoControl(handle, controlCode, null, 0, null, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }
    }
}
