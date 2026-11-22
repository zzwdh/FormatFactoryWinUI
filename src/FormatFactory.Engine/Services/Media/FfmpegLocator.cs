using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

/// <summary>Finds a bundled or explicitly configured FFmpeg/FFprobe pair.</summary>
public sealed class FfmpegLocator
{
    public Task<(string Ffmpeg, string Ffprobe)?> LocateAsync(string? configuredPath = null) =>
        LocateAsync(configuredPath, EnumerateAutomaticCandidates(), RuntimeInformation.ProcessArchitecture,
            x64EmulationAvailable: !PortableExecutableInspector.IsX64EmulationUnavailable);

    internal static async Task<(string Ffmpeg, string Ffprobe)?> LocateAsync(
        string? configuredPath,
        IEnumerable<string> automaticCandidates,
        Architecture processArchitecture,
        Func<string, Task<bool>>? canRun = null,
        bool x64EmulationAvailable = true)
    {
        var examined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var explicitlyConfigured = ResolveSymbolicLink(configuredPath);
            examined.Add(explicitlyConfigured);
            if (await ValidatePairAsync(explicitlyConfigured, processArchitecture, requireNativeArchitecture: false, canRun, x64EmulationAvailable))
            {
                return CreatePair(explicitlyConfigured);
            }
        }

        foreach (var candidate in automaticCandidates)
        {
            var resolved = ResolveSymbolicLink(candidate);
            // PATH and WinGet links can point to the same pair. Probe each real path once.
            if (examined.Add(resolved) &&
                await ValidatePairAsync(resolved, processArchitecture, requireNativeArchitecture: true, canRun, x64EmulationAvailable))
            {
                return CreatePair(resolved);
            }
        }

        return null;
    }

    public Task<bool> ValidatePairAsync(string ffmpegPath) =>
        ValidatePairAsync(ffmpegPath, RuntimeInformation.ProcessArchitecture, requireNativeArchitecture: false,
            x64EmulationAvailable: !PortableExecutableInspector.IsX64EmulationUnavailable);

    public static string GetFfprobePath(string ffmpegPath) =>
        Path.Combine(Path.GetDirectoryName(ResolveSymbolicLink(ffmpegPath)) ?? string.Empty, "ffprobe.exe");

    public static string ResolveSymbolicLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
        }
        catch
        {
            return path;
        }
    }

    internal static async Task<bool> ValidatePairAsync(
        string ffmpegPath,
        Architecture processArchitecture,
        bool requireNativeArchitecture,
        Func<string, Task<bool>>? canRun = null,
        bool x64EmulationAvailable = true)
    {
        var resolved = ResolveSymbolicLink(ffmpegPath);
        var ffprobe = ResolveSymbolicLink(GetFfprobePath(resolved));
        if (!PortableExecutableInspector.TryReadArchitecture(resolved, out var ffmpegArchitecture) ||
            !PortableExecutableInspector.TryReadArchitecture(ffprobe, out var ffprobeArchitecture) ||
            ffmpegArchitecture != ffprobeArchitecture)
        {
            return false;
        }

        // The architecture is already decoded from the PE header; do not reopen both files
        // just to read the same machine field again for automatic discovery.
        if (requireNativeArchitecture && ffmpegArchitecture != GetNativeArchitecture(processArchitecture))
        {
            return false;
        }

        if (!PortableExecutableInspector.IsCompatible(ffmpegArchitecture, processArchitecture, x64EmulationAvailable))
        {
            LogService.WriteApp(LocalizationService.T(
                "OperationalLogIncompatibleFfmpegIgnored",
                resolved,
                ffmpegArchitecture,
                processArchitecture));
            return false;
        }

        if (!requireNativeArchitecture &&
            processArchitecture == Architecture.Arm64 &&
            ffmpegArchitecture == PortableExecutableArchitecture.X64)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogExplicitX64FfmpegOnArm64", resolved));
        }

        canRun ??= CanRunAsync;
        return await canRun(resolved) && await canRun(ffprobe);
    }

    private static (string Ffmpeg, string Ffprobe) CreatePair(string resolvedFfmpeg) =>
        (resolvedFfmpeg, ResolveSymbolicLink(GetFfprobePath(resolvedFfmpeg)));

    internal static IEnumerable<string> EnumerateAutomaticCandidates(string? baseDirectory = null)
    {
        foreach (var bundled in BundledEngineLocator.GetCandidates("ffmpeg", baseDirectory))
        {
            yield return bundled;
        }
        // Keep finding engines installed by older versions when a bundle is unavailable.
        yield return Path.Combine(LogService.AppDataRoot, "FFmpeg", "Current", "ffmpeg.exe");
        yield return Path.Combine(LogService.AppDataRoot, "FFmpeg", "ffmpeg.exe");
        yield return Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "ffmpeg.exe");

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory.Trim('"'), "ffmpeg.exe");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");

        var packagesRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        foreach (var executable in EnumerateWingetCandidates(packagesRoot)) yield return executable;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "FFmpeg",
            "bin",
            "ffmpeg.exe");
    }

    internal static IEnumerable<string> EnumerateWingetCandidates(string packagesRoot,
        Func<string, IEnumerable<string>>? packageDirectories = null,
        Func<string, IEnumerable<string>>? executables = null)
    {
        packageDirectories ??= root => Directory.EnumerateDirectories(root, "Gyan.FFmpeg*",
            new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 });
        executables ??= package => Directory.EnumerateFiles(package, "ffmpeg.exe",
            new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true,
                // Automatic discovery must not follow directory links into cycles or unrelated trees.
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        foreach (var package in EnumerateDiscoveryEntries(() => packageDirectories(packagesRoot)))
            foreach (var executable in EnumerateDiscoveryEntries(() => executables(package)))
                yield return executable;
    }

    private static IEnumerable<string> EnumerateDiscoveryEntries(Func<IEnumerable<string>> enumerate)
    {
        IEnumerator<string>? entries = null;
        try { entries = enumerate().GetEnumerator(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { }
        if (entries is null) yield break;
        using (entries)
        {
            while (true)
            {
                string? entry = null;
                var hasNext = false;
                try
                {
                    hasNext = entries.MoveNext();
                    if (hasNext) entry = entries.Current;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { }
                if (!hasNext || entry is null) yield break;
                yield return entry;
            }
        }
    }

    private static PortableExecutableArchitecture GetNativeArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => PortableExecutableArchitecture.X64,
        Architecture.Arm64 => PortableExecutableArchitecture.Arm64,
        Architecture.X86 => PortableExecutableArchitecture.X86,
        _ => throw new PlatformNotSupportedException(
            LocalizationService.T("GeneralServiceUnsupportedAutoDiscoveryArchitecture", architecture))
    };

    private static async Task<bool> CanRunAsync(string path)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(path, ["-version"], TimeSpan.FromSeconds(5));
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
