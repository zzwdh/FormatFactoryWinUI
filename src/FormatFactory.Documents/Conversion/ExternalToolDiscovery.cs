using System.Diagnostics;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.Win32;

namespace FormatFactoryWinUI.ConversionEngines;

/// <summary>
/// Finds bundled and user-installed document conversion tools. This service never
/// downloads tools and never treats their absence as an application error.
/// </summary>
public sealed class ExternalToolDiscovery : IEngineDiscovery
{
    private readonly ExternalToolConfiguration _configuration;
    private readonly string _baseDirectory;
    private readonly Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>>? _runProcess;

    public ExternalToolDiscovery(ExternalToolConfiguration? configuration = null)
        : this(configuration, AppContext.BaseDirectory)
    {
    }

    internal ExternalToolDiscovery(
        ExternalToolConfiguration? configuration,
        string baseDirectory,
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>>? runProcess = null)
    {
        _configuration = configuration ?? new ExternalToolConfiguration();
        _baseDirectory = baseDirectory;
        _runProcess = runProcess;
    }

    public async Task<IReadOnlyList<ExternalToolInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.WhenAll(
            DiscoverAsync(ExternalToolKind.MicrosoftWord, cancellationToken),
            DiscoverAsync(ExternalToolKind.LibreOffice, cancellationToken),
            DiscoverAsync(ExternalToolKind.Calibre, cancellationToken),
            DiscoverAsync(ExternalToolKind.Caj2Pdf, cancellationToken));
    }

    public Task<ExternalToolInfo> DiscoverAsync(
        ExternalToolKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return kind switch
        {
            ExternalToolKind.MicrosoftWord => Task.FromResult(DiscoverWord()),
            ExternalToolKind.LibreOffice => DiscoverExecutableAsync(
                kind, "LibreOffice", _configuration.LibreOfficePath,
                ["soffice.com", "soffice.exe"], KnownLibreOfficePaths(), cancellationToken),
            ExternalToolKind.Calibre => DiscoverCalibreAsync(_configuration.CalibreEbookConvertPath, cancellationToken),
            ExternalToolKind.Caj2Pdf => DiscoverExecutableAsync(
                kind, "caj2pdf", _configuration.Caj2PdfPath,
                ["caj2pdf.exe", "caj2pdf-rs.exe", "caj2pdf"], [], cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    async Task<IReadOnlyList<EngineAvailability>> IEngineDiscovery.DiscoverAsync(CancellationToken cancellationToken)
    {
        var tools = await DiscoverAsync(cancellationToken);
        return tools.Select(tool => new EngineAvailability(
            EngineId(tool.Kind),
            tool.IsAvailable ? EngineAvailabilityState.Available : EngineAvailabilityState.Missing,
            tool.Version,
            tool.ExecutablePath,
            tool.StatusMessage)).ToList();
    }

    public static string EngineId(ExternalToolKind kind) => kind switch
    {
        ExternalToolKind.MicrosoftWord => ExternalEngineIds.Word,
        ExternalToolKind.LibreOffice => ExternalEngineIds.LibreOffice,
        ExternalToolKind.Calibre => ExternalEngineIds.Calibre,
        ExternalToolKind.Caj2Pdf => ExternalEngineIds.Caj2Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ExternalToolInfo DiscoverWord()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Unavailable(ExternalToolKind.MicrosoftWord, "Microsoft Word", LocalizationService.T("ConversionUiWordConversionWindowsOnly"));
        }

        string? executable = FindAppPath("WINWORD.EXE") ?? FindOnPath(["WINWORD.EXE"]);
        bool registered;
        try
        {
            registered = Type.GetTypeFromProgID("Word.Application", throwOnError: false) is not null;
        }
        catch
        {
            registered = false;
        }

        if (!registered)
        {
            return Unavailable(ExternalToolKind.MicrosoftWord, "Microsoft Word", executable is null
                ? LocalizationService.T("ConversionUiWordNotDetected")
                : LocalizationService.T("ConversionUiWordComUnavailable"));
        }

        return Available(
            ExternalToolKind.MicrosoftWord,
            "Microsoft Word",
            executable,
            executable is null ? null : ReadFileVersion(executable),
            LocalizationService.T("ConversionUiWordFixedLayoutAvailable"));
    }

    private async Task<ExternalToolInfo> DiscoverExecutableAsync(
        ExternalToolKind kind,
        string displayName,
        string? configuredPath,
        IReadOnlyList<string> commandNames,
        IReadOnlyList<string> knownPaths,
        CancellationToken cancellationToken)
    {
        var engineId = EngineId(kind);
        var path = FindConfiguredPath(configuredPath, commandNames);
        if (path is not null && BundledEngineLocator.IsBundledExecutable(path, _baseDirectory) &&
            !await BundledEngineLocator.ValidateExecutableAsync(engineId, path, cancellationToken, _runProcess))
        {
            path = null;
        }
        path ??= await BundledEngineLocator.FindExecutableAsync(engineId, cancellationToken, _baseDirectory, _runProcess);
        path ??= FindExisting(knownPaths.Where(candidate => !BundledEngineLocator.IsBundledExecutable(candidate, _baseDirectory)))
            ?? FindOnPath(commandNames, _baseDirectory);

        if (path is null) return Unavailable(kind, displayName,
            BundledEngineLocator.GetArchitectureLimitation(engineId, displayName, _baseDirectory) ??
            LocalizationService.T("ConversionUiToolNotDetectedSetPath", displayName));
        var tool = Available(kind, displayName, path, ReadFileVersion(path), LocalizationService.T("ConversionUiAvailable"));
        return kind == ExternalToolKind.Caj2Pdf && BundledEngineLocator.IsBundledExecutable(path, _baseDirectory)
            ? tool with { ArgumentPrefix = BundledEngineLocator.GetCajArgumentPrefix(path) }
            : tool;
    }

    private async Task<ExternalToolInfo> DiscoverCalibreAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        var tool = await DiscoverExecutableAsync(
            ExternalToolKind.Calibre,
            "Calibre ebook-convert",
            configuredPath,
            ["ebook-convert.exe", "ebook-convert"],
            KnownCalibrePaths(), cancellationToken);
        if (!tool.IsAvailable) return tool;
        if (CalibreEbookArtifactValidator.FindMetadataExecutable(tool.ExecutablePath) is not null) return tool;
        return Unavailable(
            ExternalToolKind.Calibre,
            "Calibre ebook-convert",
            LocalizationService.T("ConversionUiCalibreMissingEbookMeta"));
    }

    private static ExternalToolInfo Available(
        ExternalToolKind kind,
        string displayName,
        string? executablePath,
        string? version,
        string statusMessage) =>
        new(kind, displayName, true, executablePath, version, statusMessage.Trim());

    private static ExternalToolInfo Unavailable(ExternalToolKind kind, string displayName, string message) =>
        new(kind, displayName, false, null, null, message.Trim());

    private static string? FindConfiguredPath(string? configuredPath, IReadOnlyList<string> commandNames)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (File.Exists(expanded) && !PortableExecutableInspector.RequiresUnavailableX64Support(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        if (!Directory.Exists(expanded))
        {
            return null;
        }

        return FindExisting(commandNames.Select(name => Path.Combine(expanded, name)));
    }

    private static string? FindExisting(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                !PortableExecutableInspector.RequiresUnavailableX64Support(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static string? FindOnPath(IReadOnlyList<string> commandNames, string? excludedBundleBaseDirectory = null)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalizedDirectory = directory.Trim('"');
            foreach (var command in commandNames)
            {
                var candidate = Path.Combine(normalizedDirectory, command);
                if (File.Exists(candidate) && !PortableExecutableInspector.RequiresUnavailableX64Support(candidate) &&
                    (excludedBundleBaseDirectory is null || !BundledEngineLocator.IsBundledExecutable(candidate, excludedBundleBaseDirectory)))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string? FindAppPath(string executableName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        const string baseKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey($@"{baseKey}\{executableName}");
                    var value = key?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(value) && File.Exists(value.Trim('"')))
                    {
                        return Path.GetFullPath(value.Trim('"'));
                    }
                }
                catch
                {
                    // A missing or unreadable registry view simply means the tool was not found there.
                }
            }
        }

        return null;
    }

    private static string? ReadFileVersion(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return FirstNonEmpty(info.ProductVersion, info.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static IReadOnlyList<string> KnownLibreOfficePaths()
    {
        var paths = new List<string>();
        AddProgramFilesPath(paths, "LibreOffice", "program", "soffice.com");
        AddProgramFilesPath(paths, "LibreOffice", "program", "soffice.exe");
        return paths;
    }

    private static IReadOnlyList<string> KnownCalibrePaths()
    {
        var paths = new List<string>();
        AddProgramFilesPath(paths, "Calibre2", "ebook-convert.exe");
        return paths;
    }

    private static void AddProgramFilesPath(List<string> paths, params string[] segments)
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                paths.Add(Path.Combine([folder, .. segments]));
            }
        }
    }
}
