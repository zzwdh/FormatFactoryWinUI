namespace FormatFactoryWinUI.Services;

/// <summary>Locates the application's private, read-only conversion runtimes.</summary>
public static class BundledEngineLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] CajCompanionFiles =
        ["launcher.py", "python312.dll", "python312.zip", "python312._pth", "mutool.exe",
         "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    public static bool IsBundledExecutable(string? path) =>
        IsBundledExecutable(path, AppContext.BaseDirectory);

    internal static bool IsBundledExecutable(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetFullPath(Path.Combine(baseDirectory, "Engines")) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Resolve real paths first, then discard versioned package locations from saved settings.
    internal static FormatFactoryWinUI.Models.SettingsDraft NormalizeSettingsPaths(FormatFactoryWinUI.Models.SettingsDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalized = draft.NormalizeToolPaths(FfmpegLocator.ResolveSymbolicLink);
        static string Persist(string path) => IsBundledExecutable(path) ? string.Empty : path;
        return normalized with
        {
            FfmpegPath = Persist(normalized.FfmpegPath),
            CompatibilityFfmpegPath = Persist(normalized.CompatibilityFfmpegPath),
            LibreOfficePath = Persist(normalized.LibreOfficePath),
            CalibreEbookConvertPath = Persist(normalized.CalibreEbookConvertPath),
            Caj2PdfPath = Persist(normalized.Caj2PdfPath),
            MakeMkvPath = Persist(normalized.MakeMkvPath),
            AudioCdRipperPath = Persist(normalized.AudioCdRipperPath),
            ReadomPath = Persist(normalized.ReadomPath)
        };
    }
    internal static IEnumerable<string> GetCandidates(string engineId, string? baseDirectory = null)
    {
        var root = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Engines", engineId);
        string[] relativePaths = engineId switch
        {
            "ffmpeg" => ["bin/ffmpeg.exe", "ffmpeg.exe"],
            "libreoffice" => ["program/soffice.com", "program/soffice.exe"],
            "calibre" => ["ebook-convert.exe", "Calibre/ebook-convert.exe"],
            "caj2pdf" => ["python.exe"],
            "cdda2wav" => ["cdda2wav.exe", "bin/cdda2wav.exe", "icedax.exe", "bin/icedax.exe"],
            "readom" => ["readom.exe", "bin/readom.exe"],
            "makemkv" => ["makemkvcon64.exe", "makemkvcon.exe"],
            _ => []
        };
        return relativePaths.Select(relative => Path.GetFullPath(Path.Combine(root, relative)));
    }

    internal static async Task<string?> FindExecutableAsync(
        string engineId,
        CancellationToken cancellationToken = default,
        string? baseDirectory = null,
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>>? runProcess = null)
    {
        foreach (var candidate in GetCandidates(engineId, baseDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ValidateExecutableAsync(engineId, candidate, cancellationToken, runProcess)) return candidate;
        }
        return null;
    }

    internal static string? GetArchitectureLimitation(string engineId, string displayName, string baseDirectory) =>
        PortableExecutableInspector.IsX64EmulationUnavailable &&
        GetCandidates(engineId, baseDirectory).Any(PortableExecutableInspector.RequiresUnavailableX64Support)
            ? FormatX64UnavailableStatus(displayName) : null;

    internal static string FormatX64UnavailableStatus(string displayName) =>
        $"{displayName} (x64) · {LocalizationService.T("NativeCapabilityUnavailable")}";

    internal static async Task<bool> ValidateExecutableAsync(
        string engineId,
        string executable,
        CancellationToken cancellationToken = default,
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>>? runProcess = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) return false;
        runProcess ??= static (path, arguments, timeout, token) => ProcessRunner.RunAsync(path, arguments, timeout, token);
        var directory = Path.GetDirectoryName(executable)!;
        var probes = new List<(string Executable, string[] Arguments)>();
        switch (engineId)
        {
            case "ffmpeg":
                var ffprobe = Path.Combine(directory, "ffprobe.exe");
                if (!File.Exists(ffprobe)) return false;
                probes.Add((executable, ["-version"]));
                probes.Add((ffprobe, ["-version"]));
                break;
            case "libreoffice":
                if (!File.Exists(Path.Combine(directory, "soffice.bin")) ||
                    !File.Exists(Path.Combine(directory, "fundamental.ini"))) return false;
                probes.Add((executable, ["--headless", "--version"]));
                break;
            case "calibre":
                var metadata = Path.Combine(directory, "ebook-meta.exe");
                if (!File.Exists(metadata)) return false;
                probes.Add((executable, ["--version"]));
                probes.Add((metadata, ["--version"]));
                break;
            case "caj2pdf":
                if (!CajCompanionFiles.All(name => File.Exists(Path.Combine(directory, name)))) return false;
                var launcher = Path.Combine(directory, "launcher.py");
                probes.Add((executable, ["-I", "-B", launcher, "--version"]));
                break;
            case "cdda2wav":
            case "readom":
                probes.Add((executable, ["-version"]));
                break;
            case "makemkv":
                probes.Add((executable, ["--version"]));
                break;
            default:
                return false;
        }
        if (probes.Any(probe => PortableExecutableInspector.RequiresUnavailableX64Support(probe.Executable)))
            return false;
        try
        {
            foreach (var probe in probes)
            {
                var result = await runProcess(probe.Executable, probe.Arguments, ProbeTimeout, cancellationToken);
                if (result.ExitCode != 0) return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Incomplete packages must fall back to installed tools, never report availability.
            return false;
        }
    }

    internal static IReadOnlyList<string> GetCajArgumentPrefix(string executable) =>
        ["-I", "-B", Path.Combine(Path.GetDirectoryName(executable)!, "launcher.py")];
}
