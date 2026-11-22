using System.Diagnostics;
using System.Security.Cryptography;

namespace FormatFactoryWinUI.Services;

internal static class BundledEngineInstaller
{
    // Keep the original vendor installer intact; the vendor presents its own license UI.
    private const string MakeMkvSha256 = "F4E609BD34042A95B82A37D036D099A96E4480FF43D2BBB0BEE009757BD74B72";
    internal static string MakeMkvPath => Path.Combine(AppContext.BaseDirectory,
        "Engines", "makemkv", "Setup_MakeMKV_v2.0.0.exe");

    internal static bool HasMakeMkvInstaller =>
        !PortableExecutableInspector.IsX64EmulationUnavailable && File.Exists(MakeMkvPath);

    internal static async Task InstallMakeMkvAsync()
    {
        // The x86 NSIS bootstrapper installs an x64 MakeMKV command-line backend.
        if (PortableExecutableInspector.IsX64EmulationUnavailable)
            throw new PlatformNotSupportedException(BundledEngineLocator.FormatX64UnavailableStatus("MakeMKV"));
        var path = MakeMkvPath;
        await using (var input = File.OpenRead(path))
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(input));
            if (!digest.Equals(MakeMkvSha256, StringComparison.Ordinal))
                throw new InvalidDataException(LocalizationService.T("ConversionUiBundledInstallerInvalid"));
        }
        using var installer = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        }) ?? throw new IOException(LocalizationService.T("ConversionUiBundledInstallerFailed"));
        await installer.WaitForExitAsync();
        // NSIS returns a nonzero code when cancelled; simply rediscover, without claiming success.
    }
}
