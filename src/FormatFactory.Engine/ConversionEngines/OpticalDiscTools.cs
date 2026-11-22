using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

public enum OpticalDiscToolKind
{
    MakeMkv,
    AudioCdRipper,
    Readom
}

public enum OpticalDiscOperation
{
    InspectMakeMkvDrives,
    BluRayToMkv,
    AudioCdToWavTracks,
    DiscToIso
}

public sealed record OpticalDiscToolConfiguration(
    string? MakeMkvPath = null,
    string? AudioCdRipperPath = null,
    string? ReadomPath = null);

public sealed record OpticalDiscToolInfo(
    OpticalDiscToolKind Kind,
    string DisplayName,
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string StatusMessage);

public interface IOpticalDiscToolDiscovery
{
    Task<IReadOnlyList<OpticalDiscToolInfo>> DiscoverAsync(
        CancellationToken cancellationToken = default);

    Task<OpticalDiscToolInfo> DiscoverAsync(
        OpticalDiscToolKind kind,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds bundled and user-installed optical-disc tools. Bundled tools are probed;
/// user-installed tools retain their existing discovery behavior. No downloads occur.
/// </summary>
public sealed class OpticalDiscToolDiscovery : IOpticalDiscToolDiscovery
{
    private static readonly string[] MakeMkvNames = ["makemkvcon64.exe", "makemkvcon.exe"];
    private static readonly string[] AudioCdRipperNames = ["cdda2wav.exe", "icedax.exe"];
    private static readonly string[] ReadomNames = ["readom.exe"];
    private readonly OpticalDiscToolConfiguration _configuration;
    private readonly string _baseDirectory;
    private readonly Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>>? _runProcess;

    public OpticalDiscToolDiscovery(OpticalDiscToolConfiguration? configuration = null)
        : this(configuration, AppContext.BaseDirectory)
    {
    }

    internal OpticalDiscToolDiscovery(
        OpticalDiscToolConfiguration? configuration,
        string baseDirectory,
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>>? runProcess = null)
    {
        _configuration = configuration ?? new OpticalDiscToolConfiguration();
        _baseDirectory = baseDirectory;
        _runProcess = runProcess;
    }
    public async Task<IReadOnlyList<OpticalDiscToolInfo>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OpticalDiscToolInfo> tools =
        [
            await DiscoverToolAsync(
                OpticalDiscToolKind.MakeMkv,
                "MakeMKV CLI",
                _configuration.MakeMkvPath,
                MakeMkvNames,
                KnownDirectories(OpticalDiscToolKind.MakeMkv), cancellationToken),
            await DiscoverToolAsync(
                OpticalDiscToolKind.AudioCdRipper,
                "cdda2wav / icedax",
                _configuration.AudioCdRipperPath,
                AudioCdRipperNames,
                KnownDirectories(OpticalDiscToolKind.AudioCdRipper), cancellationToken),
            await DiscoverToolAsync(
                OpticalDiscToolKind.Readom,
                "readom",
                _configuration.ReadomPath,
                ReadomNames,
                KnownDirectories(OpticalDiscToolKind.Readom), cancellationToken)
        ];
        return tools;
    }

    public async Task<OpticalDiscToolInfo> DiscoverAsync(
        OpticalDiscToolKind kind,
        CancellationToken cancellationToken = default)
    {
        var tools = await DiscoverAsync(cancellationToken);
        return tools.Single(tool => tool.Kind == kind);
    }

    /// <summary>
    /// Deterministic executable lookup used by discovery and tests. A configured
    /// file or directory takes precedence over the supplied search directories.
    /// </summary>
    public static string? FindExecutable(
        string? configuredPath,
        IReadOnlyList<string> executableNames,
        IEnumerable<string>? searchDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(executableNames);
        if (executableNames.Count == 0 || executableNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiExecutableNameRequired"), nameof(executableNames));
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (File.Exists(configured) && !PortableExecutableInspector.RequiresUnavailableX64Support(configured))
            {
                return Path.GetFullPath(configured);
            }

            if (Directory.Exists(configured))
            {
                var found = FindInDirectory(configured, executableNames);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        foreach (var directory in searchDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
            var found = FindInDirectory(expanded, executableNames);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private async Task<OpticalDiscToolInfo> DiscoverToolAsync(
        OpticalDiscToolKind kind,
        string displayName,
        string? configuredPath,
        IReadOnlyList<string> executableNames,
        IEnumerable<string> knownDirectories,
        CancellationToken cancellationToken)
    {
        var searchDirectories = knownDirectories
            .Concat(PathDirectories())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var engineId = kind switch
        {
            OpticalDiscToolKind.MakeMkv => "makemkv",
            OpticalDiscToolKind.AudioCdRipper => "cdda2wav",
            OpticalDiscToolKind.Readom => "readom",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var path = FindExecutable(configuredPath, executableNames);
        if (path is not null && BundledEngineLocator.IsBundledExecutable(path, _baseDirectory) &&
            !await BundledEngineLocator.ValidateExecutableAsync(engineId, path, cancellationToken, _runProcess))
        {
            path = null;
        }
        path ??= await BundledEngineLocator.FindExecutableAsync(engineId, cancellationToken, _baseDirectory, _runProcess);
        path ??= FindExecutable(null, executableNames, searchDirectories.Where(directory =>
            !BundledEngineLocator.IsBundledExecutable(
                Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), executableNames[0]), _baseDirectory)));
        var isNativeReadFallback = kind is
            OpticalDiscToolKind.AudioCdRipper or OpticalDiscToolKind.Readom;
        return path is null
            ? new OpticalDiscToolInfo(
                kind,
                displayName,
                false,
                null,
                null,
                BundledEngineLocator.GetArchitectureLimitation(engineId, displayName, _baseDirectory) ?? (isNativeReadFallback
                    ? LocalizationService.T("ConversionUiFallbackToolNotDetected", displayName)
                    : LocalizationService.T("ConversionUiToolNotDetectedSetLocation", displayName)))
            : new OpticalDiscToolInfo(
                kind,
                displayName,
                true,
                path,
                ReadFileVersion(path),
                isNativeReadFallback
                    ? LocalizationService.T("ConversionUiFallbackBackendAvailable", Path.GetFileName(path))
                    : LocalizationService.T("ConversionUiAvailablePath", Path.GetFileName(path)));
    }

    private static string? FindInDirectory(string directory, IReadOnlyList<string> executableNames)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var executableName in executableNames)
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate) && !PortableExecutableInspector.RequiresUnavailableX64Support(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> KnownDirectories(OpticalDiscToolKind kind)
    {
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            if (kind == OpticalDiscToolKind.MakeMkv)
            {
                yield return Path.Combine(programFiles, "MakeMKV");
                continue;
            }

            yield return Path.Combine(programFiles, "cdrtools");
            yield return Path.Combine(programFiles, "cdrtools", "bin");
            yield return Path.Combine(programFiles, "cdrkit", "bin");
            yield return Path.Combine(programFiles, "schilytools", "bin");
        }
    }

    private static IEnumerable<string> PathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        return string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? ReadFileVersion(string executablePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            return FirstNonEmpty(version.ProductVersion, version.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed record OpticalDiscToolCommand
{
    public OpticalDiscToolCommand(
        OpticalDiscOperation operation,
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        var materializedArguments = arguments.ToArray();
        if (materializedArguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiExternalArgumentsNoNull"), nameof(arguments));
        }

        Operation = operation;
        ExecutablePath = executablePath.Trim();
        Arguments = Array.AsReadOnly(materializedArguments);
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFullPath(workingDirectory);
    }

    public OpticalDiscOperation Operation { get; }

    public string ExecutablePath { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }
}

/// <summary>
/// Builds argument-list commands only. Paths remain individual arguments and
/// are never concatenated into a shell command line.
/// </summary>
public static class OpticalDiscCommandFactory
{
    public static OpticalDiscToolCommand BuildMakeMkvDriveScan(string executablePath) => new(
        OpticalDiscOperation.InspectMakeMkvDrives,
        executablePath,
        ["--robot", "--messages=-stdout", "info", "disc:9999"]);

    public static OpticalDiscToolCommand BuildBluRayToMkv(
        string executablePath,
        int driveIndex,
        string outputDirectory)
    {
        if (driveIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(driveIndex));
        }

        var output = RequireFullPath(outputDirectory, nameof(outputDirectory));
        return new OpticalDiscToolCommand(
            OpticalDiscOperation.BluRayToMkv,
            executablePath,
            [
                "--robot",
                "--messages=-stdout",
                "--progress=-stdout",
                "mkv",
                $"disc:{driveIndex.ToString(CultureInfo.InvariantCulture)}",
                "all",
                output
            ]);
    }

    public static OpticalDiscToolCommand BuildAudioCdToWavTracks(
        string executablePath,
        string rootPath,
        string outputDirectory)
    {
        var drive = OpticalDiscPath.ToDriveSpecifier(rootPath);
        var output = RequireFullPath(outputDirectory, nameof(outputDirectory));
        return new OpticalDiscToolCommand(
            OpticalDiscOperation.AudioCdToWavTracks,
            executablePath,
            ["-D", drive, "-B", "-O", "wav", "track"],
            output);
    }

    public static OpticalDiscToolCommand BuildDiscToIso(
        string executablePath,
        string rootPath,
        string outputFile)
    {
        var drive = OpticalDiscPath.ToDriveSpecifier(rootPath);
        var output = RequireFullPath(outputFile, nameof(outputFile));
        return new OpticalDiscToolCommand(
            OpticalDiscOperation.DiscToIso,
            executablePath,
            [$"dev={drive}", $"f={output}"]);
    }

    private static string RequireFullPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Contains('\0'))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiPathNoNul"), parameterName);
        }

        return Path.GetFullPath(path);
    }
}

public sealed record MakeMkvDriveEntry(
    int Index,
    string DriveName,
    string DiscName,
    string DeviceName);

/// <summary>
/// Parses MakeMKV robot-mode DRV records and maps its private disc index to a
/// Windows drive root. DRV is CSV-like and may contain commas or escaped quotes.
/// </summary>
public static class MakeMkvDriveListParser
{
    public static IReadOnlyList<MakeMkvDriveEntry> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var result = new List<MakeMkvDriveEntry>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("DRV:", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = ParseCsv(line[4..]);
            if (fields is null || fields.Count < 7 ||
                !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                index < 0)
            {
                continue;
            }

            result.Add(new MakeMkvDriveEntry(
                index,
                fields[4],
                fields[5],
                fields[^1]));
        }

        return result.AsReadOnly();
    }

    public static int? ResolveDriveIndex(string output, string rootPath)
    {
        var target = OpticalDiscPath.ToDriveSpecifier(rootPath);
        var matches = Parse(output)
            .Where(entry => string.Equals(
                GetWindowsDriveSpecifier(entry.DeviceName),
                target,
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Index)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<string>? ParseCsv(string value)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                if (quoted && index + 1 < value.Length && value[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (character == ',' && !quoted)
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
                continue;
            }

            field.Append(character);
        }

        if (quoted)
        {
            return null;
        }

        fields.Add(field.ToString().Trim());
        return fields;
    }

    private static string? GetWindowsDriveSpecifier(string deviceName)
    {
        var value = deviceName.Trim();
        for (var index = value.Length - 2; index >= 0; index--)
        {
            if (char.IsAsciiLetter(value[index]) && value[index + 1] == ':')
            {
                return $"{char.ToUpperInvariant(value[index])}:";
            }
        }

        return null;
    }
}

public sealed record OpticalDiscProgress(
    OpticalDiscOperation Operation,
    double? Percentage,
    string Message);

public static partial class OpticalDiscProgressParser
{
    private static readonly Regex PercentagePattern = new(
        @"(?<![\d.])(?<value>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static double? ParsePercentage(OpticalDiscOperation operation, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (operation == OpticalDiscOperation.BluRayToMkv &&
            line.StartsWith("PRGV:", StringComparison.Ordinal))
        {
            var values = line[5..].Split(',', StringSplitOptions.TrimEntries);
            if (values.Length >= 3 &&
                double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var total) &&
                double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum) &&
                maximum > 0)
            {
                return Math.Clamp(total * 100d / maximum, 0d, 100d);
            }
        }

        var match = PercentagePattern.Match(line);
        if (!match.Success ||
            !double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percentage))
        {
            return null;
        }

        return Math.Clamp(percentage, 0d, 100d);
    }
}
