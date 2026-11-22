using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class FfmpegLocatorTests
{
    [Fact]
    public void WingetDiscovery_SkipsInaccessiblePackageAndKeepsEarlierAndLaterCandidates()
    {
        static IEnumerable<string> InaccessibleSubdirectory()
        {
            yield return "earlier/ffmpeg.exe";
            throw new UnauthorizedAccessException("Synthetic inaccessible WinGet child directory.");
        }
        var candidates = FfmpegLocator.EnumerateWingetCandidates("synthetic-root",
            _ => ["partially-readable", "denied-package", "healthy-package"],
            package => package switch
            {
                "partially-readable" => InaccessibleSubdirectory(),
                "denied-package" => throw new UnauthorizedAccessException("Synthetic denied WinGet package."),
                _ => ["later/ffmpeg.exe"]
            }).ToArray();
        Assert.Equal(["earlier/ffmpeg.exe", "later/ffmpeg.exe"], candidates);
    }

    [Fact]
    public void WingetDiscovery_UnavailablePackageRootIsOptional()
    {
        var candidates = FfmpegLocator.EnumerateWingetCandidates("synthetic-root",
            _ => throw new UnauthorizedAccessException("Synthetic unreadable WinGet package root."));
        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Locate_ProbesEquivalentPathsOnceAndContinuesToWorkingPair(bool explicitlyConfigured)
    {
        using var directory = new TemporaryDirectory();
        var rejected = WriteExecutable(directory.Path, "ffmpeg.exe", 0x8664);
        WriteExecutable(directory.Path, "ffprobe.exe", 0x8664);
        var fallbackDirectory = Path.Combine(directory.Path, "fallback");
        Directory.CreateDirectory(fallbackDirectory);
        var fallback = WriteExecutable(fallbackDirectory, "ffmpeg.exe", 0x8664);
        var fallbackProbe = WriteExecutable(fallbackDirectory, "ffprobe.exe", 0x8664);
        var probes = new List<string>();
        string[] candidates =
        [
            rejected,
            Path.Combine(directory.Path, ".", "ffmpeg.exe"),
            Path.GetRelativePath(Environment.CurrentDirectory, rejected),
            fallback
        ];

        var result = await FfmpegLocator.LocateAsync(explicitlyConfigured ? rejected : null,
            candidates, Architecture.X64, path =>
            {
                probes.Add(path);
                return Task.FromResult(path != rejected);
            });

        Assert.NotNull(result);
        Assert.Equal((fallback, fallbackProbe), result.Value);
        Assert.Equal([rejected, fallback, fallbackProbe], probes);
    }

    [Fact]
    public async Task Locate_PrefersCompatibleConfiguredPairBeforeAutomaticCandidates()
    {
        using var directory = new TemporaryDirectory();
        var ffmpeg = WriteExecutable(directory.Path, "ffmpeg.exe", 0x8664);
        var ffprobe = WriteExecutable(directory.Path, "ffprobe.exe", 0x8664);
        static IEnumerable<string> UnexpectedAutomaticCandidates() =>
            throw new InvalidOperationException("A configured pair must take precedence.");

        // Enumeration must remain lazy: a valid explicit x64 pair is supported on ARM64.
        static IEnumerable<string> AutomaticCandidates()
        {
            foreach (var candidate in UnexpectedAutomaticCandidates()) yield return candidate;
        }
        var result = await FfmpegLocator.LocateAsync(ffmpeg, AutomaticCandidates(), Architecture.Arm64,
            _ => Task.FromResult(true));

        Assert.Equal((ffmpeg, ffprobe), result);
    }

    [Theory]
    [InlineData(0x8664)]
    [InlineData(0xA641)]
    public async Task Locate_SkipsConfiguredEmulatedPairWhenWindowsArm64CannotRunIt(int machine)
    {
        using var directory = new TemporaryDirectory();
        var configuredFfmpeg = WriteExecutable(directory.Path, "ffmpeg.exe", machine);
        WriteExecutable(directory.Path, "ffprobe.exe", machine);
        var nativeDirectory = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(nativeDirectory);
        var nativeFfmpeg = WriteExecutable(nativeDirectory, "ffmpeg.exe", 0xAA64);
        var nativeFfprobe = WriteExecutable(nativeDirectory, "ffprobe.exe", 0xAA64);
        var probes = new List<string>();

        var result = await FfmpegLocator.LocateAsync(configuredFfmpeg, [nativeFfmpeg], Architecture.Arm64,
            path =>
            {
                probes.Add(path);
                return Task.FromResult(true);
            }, x64EmulationAvailable: false);

        Assert.Equal((nativeFfmpeg, nativeFfprobe), result);
        Assert.Equal([nativeFfmpeg, nativeFfprobe], probes);
    }

    [Theory]
    [InlineData(0x8664, Architecture.X64, true, true)]
    [InlineData(0xAA64, Architecture.Arm64, true, true)]
    [InlineData(0x014C, Architecture.X86, true, true)]
    [InlineData(0x8664, Architecture.Arm64, true, false)]
    [InlineData(0x8664, Architecture.Arm64, false, true)]
    [InlineData(0xA641, Architecture.Arm64, true, false)]
    [InlineData(0xA641, Architecture.Arm64, false, true)]
    [InlineData(0x014C, Architecture.X64, true, false)]
    [InlineData(0x014C, Architecture.X64, false, true)]
    [InlineData(0xAA64, Architecture.X64, false, false)]
    public async Task ValidatePair_PreservesNativeDiscoveryAndExplicitCompatibility(
        int machine, Architecture processArchitecture, bool requireNative, bool expected)
    {
        using var directory = new TemporaryDirectory();
        var ffmpeg = WriteExecutable(directory.Path, "ffmpeg.exe", machine);
        var ffprobe = WriteExecutable(directory.Path, "ffprobe.exe", machine);
        var probes = new List<string>();

        var actual = await FfmpegLocator.ValidatePairAsync(ffmpeg, processArchitecture, requireNative, path =>
        {
            probes.Add(path);
            return Task.FromResult(true);
        });

        Assert.Equal(expected, actual);
        Assert.Equal(expected ? new[] { ffmpeg, ffprobe } : [], probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatePair_RejectsMissingOrMismatchedCompanionBeforeExecution(bool mismatched)
    {
        using var directory = new TemporaryDirectory();
        var ffmpeg = WriteExecutable(directory.Path, "ffmpeg.exe", 0x8664);
        if (mismatched) WriteExecutable(directory.Path, "ffprobe.exe", 0xAA64);

        var actual = await FfmpegLocator.ValidatePairAsync(ffmpeg, Architecture.X64, true,
            _ => throw new InvalidOperationException("An incomplete pair must not be executed."));

        Assert.False(actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatePair_RequiresBothExecutablesToRun(bool ffmpegRuns)
    {
        using var directory = new TemporaryDirectory();
        var ffmpeg = WriteExecutable(directory.Path, "ffmpeg.exe", 0x8664);
        var ffprobe = WriteExecutable(directory.Path, "ffprobe.exe", 0x8664);
        var probes = new List<string>();

        var actual = await FfmpegLocator.ValidatePairAsync(ffmpeg, Architecture.X64, true, path =>
        {
            probes.Add(path);
            return Task.FromResult(path == ffmpeg && ffmpegRuns);
        });

        Assert.False(actual);
        Assert.Equal(ffmpegRuns ? new[] { ffmpeg, ffprobe } : [ffmpeg], probes);
    }

    private static string WriteExecutable(string directory, string name, int machine)
    {
        var path = Path.Combine(directory, name);
        var bytes = new byte[0x48];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);
        bytes[0x40] = 0x50;
        bytes[0x41] = 0x45;
        BitConverter.GetBytes(checked((ushort)machine)).CopyTo(bytes, 0x44);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
