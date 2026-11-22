using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

internal enum PortableExecutableArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64,
    Arm64Ec
}

internal static class PortableExecutableInspector
{
    internal const ushort ImageFileMachineI386 = 0x014C;
    internal const ushort ImageFileMachineAmd64 = 0x8664;
    internal const ushort ImageFileMachineArm64 = 0xAA64;
    internal const ushort ImageFileMachineArm64Ec = 0xA641;

    // Windows 10 on Arm supports x86 emulation. x64 and ARM64EC require Windows 11.
    internal static bool IsX64EmulationUnavailable =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 &&
        OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    internal static bool RequiresUnavailableX64Support(string path) =>
        IsX64EmulationUnavailable && TryReadArchitecture(path, out var architecture) &&
        architecture is PortableExecutableArchitecture.X64 or PortableExecutableArchitecture.Arm64Ec;

    internal static bool TryReadArchitecture(string path, out PortableExecutableArchitecture architecture)
    {
        architecture = PortableExecutableArchitecture.Unknown;
        if (!TryReadMachine(path, out var machine))
        {
            return false;
        }

        architecture = machine switch
        {
            ImageFileMachineI386 => PortableExecutableArchitecture.X86,
            ImageFileMachineAmd64 => PortableExecutableArchitecture.X64,
            ImageFileMachineArm64 => PortableExecutableArchitecture.Arm64,
            ImageFileMachineArm64Ec => PortableExecutableArchitecture.Arm64Ec,
            _ => PortableExecutableArchitecture.Unknown
        };
        return architecture != PortableExecutableArchitecture.Unknown;
    }

    internal static bool TryReadMachine(string path, out ushort machine)
    {
        machine = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
            {
                return false;
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6)
            {
                return false;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return false;
            }

            machine = reader.ReadUInt16();
            return machine is ImageFileMachineI386 or
                ImageFileMachineAmd64 or
                ImageFileMachineArm64 or
                ImageFileMachineArm64Ec;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsCompatible(
        PortableExecutableArchitecture executableArchitecture,
        Architecture processArchitecture,
        bool x64EmulationAvailable = true) => processArchitecture switch
        {
            Architecture.X64 => executableArchitecture is PortableExecutableArchitecture.X64 or PortableExecutableArchitecture.X86,
            Architecture.Arm64 => executableArchitecture == PortableExecutableArchitecture.Arm64 ||
                (x64EmulationAvailable && executableArchitecture is PortableExecutableArchitecture.Arm64Ec or PortableExecutableArchitecture.X64),
            Architecture.X86 => executableArchitecture == PortableExecutableArchitecture.X86,
            _ => false
        };
}
