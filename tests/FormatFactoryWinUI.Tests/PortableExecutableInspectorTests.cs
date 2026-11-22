using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class PortableExecutableInspectorTests
{
    [Theory]
    [InlineData(0x014C, 1)]
    [InlineData(0x8664, 2)]
    [InlineData(0xAA64, 3)]
    [InlineData(0xA641, 4)]
    public void TryReadArchitecture_RecognizesSupportedMachineTypes(int machine, int expected)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sample.exe");
        WriteMinimalPe(path, checked((ushort)machine));

        var success = PortableExecutableInspector.TryReadArchitecture(path, out var actual);

        Assert.True(success);
        Assert.Equal((PortableExecutableArchitecture)expected, actual);
    }

    [Fact]
    public void TryReadArchitecture_RejectsDamagedAndMissingFiles()
    {
        using var directory = new TemporaryDirectory();
        var damagedPath = Path.Combine(directory.Path, "damaged.exe");
        File.WriteAllBytes(damagedPath, [0x4D, 0x5A, 0x00]);

        Assert.False(PortableExecutableInspector.TryReadArchitecture(damagedPath, out var damagedArchitecture));
        Assert.Equal(PortableExecutableArchitecture.Unknown, damagedArchitecture);
        Assert.False(PortableExecutableInspector.TryReadArchitecture(Path.Combine(directory.Path, "missing.exe"), out var missingArchitecture));
        Assert.Equal(PortableExecutableArchitecture.Unknown, missingArchitecture);
    }

    [Theory]
    [InlineData(2, Architecture.X64, true)]
    [InlineData(1, Architecture.X64, true)]
    [InlineData(3, Architecture.X64, false)]
    [InlineData(3, Architecture.Arm64, true)]
    [InlineData(4, Architecture.Arm64, true)]
    [InlineData(2, Architecture.Arm64, true)]
    [InlineData(1, Architecture.Arm64, false)]
    [InlineData(1, Architecture.X86, true)]
    [InlineData(2, Architecture.X86, false)]
    public void IsCompatible_UsesExpectedWindowsCompatibilityMatrix(
        int executable,
        Architecture process,
        bool expected)
    {
        Assert.Equal(expected, PortableExecutableInspector.IsCompatible((PortableExecutableArchitecture)executable, process));
    }

    private static void WriteMinimalPe(string path, ushort machine)
    {
        var bytes = new byte[0x48];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);
        bytes[0x40] = 0x50;
        bytes[0x41] = 0x45;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x44);
        File.WriteAllBytes(path, bytes);
    }
}
