using System.ComponentModel;
using System.Diagnostics;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

public sealed record OpticalDiscCommandExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string? LogPath { get; init; }
    public bool OutputTruncated { get; init; }
}

public interface IOpticalDiscCommandExecutor
{
    Task<OpticalDiscCommandExecutionResult> ExecuteAsync(
        OpticalDiscToolCommand command,
        IProgress<OpticalDiscProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes an optical-disc command without a command shell. ArgumentList keeps
/// every path and option as a distinct argument, including paths with spaces or
/// command-line metacharacters.
/// </summary>
public sealed class OpticalDiscCommandExecutor : IOpticalDiscCommandExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(12);
    private readonly TimeSpan _timeout;

    public OpticalDiscCommandExecutor(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<OpticalDiscCommandExecutionResult> ExecuteAsync(
        OpticalDiscToolCommand command, IProgress<OpticalDiscProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        using var launch = await ConversionWorkerLaunch.StartAsync(command.ExecutablePath, command.Arguments,
            null, cancellationToken, workingDirectory: command.WorkingDirectory).ConfigureAwait(false);
        var lifetime = launch.Lifetime;
        var requireComplete = command.Operation == OpticalDiscOperation.InspectMakeMkvDrives;
        var logPath = LogService.CreateTaskLogPath(Guid.NewGuid());
        using var outputLogProtection = LogFilePolicy.Shared.Protect(logPath);
        using var errorLogProtection = LogFilePolicy.Shared.Protect(logPath + ".stderr");
        var standardOutput = new BoundedTextCapture(requireComplete);
        var standardError = new BoundedTextCapture(requireComplete);
        var outputTask = PumpLinesAsync(lifetime.Process.StandardOutput, standardOutput, logPath, command.Operation, progress);
        var errorTask = PumpLinesAsync(lifetime.Process.StandardError, standardError, logPath + ".stderr", command.Operation, progress);
        try
        {
            await lifetime.WaitAndDrainAsync(_timeout, cancellationToken, outputTask, errorTask).ConfigureAwait(false);
            return new OpticalDiscCommandExecutionResult(lifetime.Process.ExitCode,
                standardOutput.ToString(), standardError.ToString())
            {
                LogPath = logPath,
                OutputTruncated = standardOutput.Truncated || standardError.Truncated
            };
        }
        finally
        {
            // A diagnostics failure must not replace a conversion/cancellation result.
            try { LogService.FlushTask(logPath); LogService.FlushTask(logPath + ".stderr"); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    private static async Task PumpLinesAsync(StreamReader reader, BoundedTextCapture destination,
        string logPath, OpticalDiscOperation operation, IProgress<OpticalDiscProgress>? progress)
    {
        await foreach (var line in BoundedProcessOutput.ReadLinesAsync(reader, chunk => LogService.AppendTask(logPath, chunk),
            requireCompleteLine: destination.RequiresCompleteOutput, onTruncatedLine: destination.MarkTruncated))
        {
            destination.Append(line + Environment.NewLine);
            if (progress is null || line.StartsWith('…')) continue;
            try { progress.Report(new OpticalDiscProgress(operation, OpticalDiscProgressParser.ParsePercentage(operation, line), line)); }
            catch { /* A UI progress sink must not terminate conversion. */ }
        }
    }
}
public sealed record OpticalDiscConversionRequest(
    OpticalDiscOperation Operation,
    OpticalDiscInfo Source,
    string OutputPath);

public enum OpticalDiscReadBackend
{
    ExternalTool,
    WindowsNative,
    ExternalToolFallback
}

public sealed record OpticalDiscConversionResult(
    OpticalDiscOperation Operation,
    string OutputPath,
    IReadOnlyList<string> OutputArtifacts,
    string StandardOutput,
    string StandardError,
    OpticalDiscReadBackend Backend = OpticalDiscReadBackend.ExternalTool);

public enum OpticalDiscRouteKind
{
    Unsupported,
    BuiltInImageConversion,
    BluRayToMkv,
    AudioCdExtraction,
    PhysicalDiscImage
}

public enum OpticalDiscDependency
{
    BuiltInImageCodec,
    WindowsOpticalReader,
    Ffmpeg,
    MakeMkv,
    AudioCdRipper,
    Readom
}

public sealed record OpticalDiscBackendPlan(
    OpticalDiscRouteKind Route,
    IReadOnlyList<OpticalDiscDependency> RequiredDependencies,
    IReadOnlyList<OpticalDiscDependency> PreferredDependencies,
    IReadOnlyList<OpticalDiscDependency> OptionalFallbackDependencies)
{
    public bool IsSupported => Route != OpticalDiscRouteKind.Unsupported;
}

/// <summary>
/// Describes which dependencies block a disc route and which tools are only
/// fallbacks for Windows native reads. UI and queue availability checks use the
/// same policy as the conversion pipeline.
/// </summary>
public static class OpticalDiscBackendPlanner
{
    public static OpticalDiscBackendPlan Plan(string sourceFormatId, string targetFormatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFormatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFormatId);
        var source = FormatCatalog.NormalizeFormatId(sourceFormatId);
        var target = FormatCatalog.NormalizeFormatId(targetFormatId);

        if (source is "iso" or "cso")
        {
            return source != target && target is "iso" or "cso"
                ? Create(
                    OpticalDiscRouteKind.BuiltInImageConversion,
                    [OpticalDiscDependency.BuiltInImageCodec])
                : Unsupported();
        }

        if (source == OpticalDiscFormatIds.BluRayDisc && target == "mkv")
        {
            return Create(
                OpticalDiscRouteKind.BluRayToMkv,
                [OpticalDiscDependency.MakeMkv]);
        }

        if (source == OpticalDiscFormatIds.AudioCd && FormatCatalog.IsAudioFormat(target))
        {
            var required = target == "wav"
                ? Array.Empty<OpticalDiscDependency>()
                : new[] { OpticalDiscDependency.Ffmpeg };
            return Create(
                OpticalDiscRouteKind.AudioCdExtraction,
                required,
                [OpticalDiscDependency.WindowsOpticalReader],
                [OpticalDiscDependency.AudioCdRipper]);
        }

        if (source is OpticalDiscFormatIds.DvdDisc or OpticalDiscFormatIds.DataDisc &&
            target is "iso" or "cso")
        {
            return Create(
                OpticalDiscRouteKind.PhysicalDiscImage,
                [],
                [OpticalDiscDependency.WindowsOpticalReader],
                [OpticalDiscDependency.Readom]);
        }

        return Unsupported();
    }

    private static OpticalDiscBackendPlan Create(
        OpticalDiscRouteKind route,
        IReadOnlyList<OpticalDiscDependency> required,
        IReadOnlyList<OpticalDiscDependency>? preferred = null,
        IReadOnlyList<OpticalDiscDependency>? fallbacks = null) =>
        new(route, required, preferred ?? [], fallbacks ?? []);

    private static OpticalDiscBackendPlan Unsupported() =>
        new(OpticalDiscRouteKind.Unsupported, [], [], []);
}

/// <summary>
/// Coordinates Windows native optical reads and optional local tools. Native
/// CDDA/data reads are attempted first; cdda2wav/icedax and readom are fallbacks.
/// MakeMKV remains the Blu-ray backend. Destinations use sibling staging paths.
/// </summary>
public sealed class OpticalDiscConversionService
{
    private readonly IOpticalDiscToolDiscovery _toolDiscovery;
    private readonly IOpticalDiscCommandExecutor _commandExecutor;
    private readonly IWindowsOpticalDiscReader? _nativeReader;

    public OpticalDiscConversionService(
        IOpticalDiscToolDiscovery? toolDiscovery = null,
        IOpticalDiscCommandExecutor? commandExecutor = null)
    {
        _toolDiscovery = toolDiscovery ?? new OpticalDiscToolDiscovery();
        _commandExecutor = commandExecutor ?? new OpticalDiscCommandExecutor();
    }

    public OpticalDiscConversionService(
        IWindowsOpticalDiscReader nativeReader,
        IOpticalDiscToolDiscovery? toolDiscovery = null,
        IOpticalDiscCommandExecutor? commandExecutor = null)
        : this(toolDiscovery, commandExecutor)
    {
        _nativeReader = nativeReader ?? throw new ArgumentNullException(nameof(nativeReader));
    }

    public Task<OpticalDiscConversionResult> ConvertBluRayToMkvAsync(
        OpticalDiscInfo source,
        string outputDirectory,
        IProgress<OpticalDiscProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ConvertAsync(
            new OpticalDiscConversionRequest(
                OpticalDiscOperation.BluRayToMkv,
                source,
                outputDirectory),
            progress,
            cancellationToken);

    public Task<OpticalDiscConversionResult> ExtractAudioCdToWavTracksAsync(
        OpticalDiscInfo source,
        string outputDirectory,
        IProgress<OpticalDiscProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ConvertAsync(
            new OpticalDiscConversionRequest(
                OpticalDiscOperation.AudioCdToWavTracks,
                source,
                outputDirectory),
            progress,
            cancellationToken);

    public Task<OpticalDiscConversionResult> CreateIsoAsync(
        OpticalDiscInfo source,
        string outputFile,
        IProgress<OpticalDiscProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ConvertAsync(
            new OpticalDiscConversionRequest(
                OpticalDiscOperation.DiscToIso,
                source,
                outputFile),
            progress,
            cancellationToken);

    public async Task<OpticalDiscConversionResult> ConvertAsync(
        OpticalDiscConversionRequest request,
        IProgress<OpticalDiscProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        return request.Operation switch
        {
            OpticalDiscOperation.BluRayToMkv => await ConvertBluRayAsync(
                request,
                progress,
                cancellationToken),
            OpticalDiscOperation.AudioCdToWavTracks => await ConvertAudioCdAsync(
                request,
                progress,
                cancellationToken),
            OpticalDiscOperation.DiscToIso => await ConvertIsoAsync(
                request,
                progress,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Operation))
        };
    }

    private async Task<OpticalDiscConversionResult> ConvertBluRayAsync(
        OpticalDiscConversionRequest request,
        IProgress<OpticalDiscProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureMediaKind(request.Source, OpticalDiscMediaKind.BluRay);
        var tool = await RequireToolAsync(OpticalDiscToolKind.MakeMkv, cancellationToken);
        var scanCommand = OpticalDiscCommandFactory.BuildMakeMkvDriveScan(tool.ExecutablePath!);
        var scan = await _commandExecutor.ExecuteAsync(
            scanCommand,
            progress: null,
            cancellationToken);
        EnsureSuccessful(tool, scan);
        cancellationToken.ThrowIfCancellationRequested();

        var scanOutput = scan.StandardOutput + Environment.NewLine + scan.StandardError;
        var makeMkvDriveIndex = MakeMkvDriveListParser.ResolveDriveIndex(
            scanOutput,
            request.Source.RootPath);
        if (makeMkvDriveIndex is null)
        {
            throw new InvalidOperationException(
                LocalizationService.T("ConversionUiMakeMkvDriveNotFound", request.Source.RootPath));
        }

        return await ConvertDirectoryAsync(
            request,
            OpticalDiscMediaKind.BluRay,
            OpticalDiscToolKind.MakeMkv,
            ".mkv",
            (executable, staging) => OpticalDiscCommandFactory.BuildBluRayToMkv(
                executable,
                makeMkvDriveIndex.Value,
                staging),
            progress,
            cancellationToken,
            tool);
    }

    private async Task<OpticalDiscConversionResult> ConvertAudioCdAsync(
        OpticalDiscConversionRequest request,
        IProgress<OpticalDiscProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureMediaKind(request.Source, OpticalDiscMediaKind.AudioCd);
        if (_nativeReader is not null)
        {
            try
            {
                var artifacts = await _nativeReader.ExtractAudioCdToWaveAsync(
                    request.Source.DevicePath,
                    request.OutputPath,
                    AdaptNativeProgress(progress, request.Operation),
                    cancellationToken);
                return new OpticalDiscConversionResult(
                    request.Operation,
                    Path.GetFullPath(request.OutputPath),
                    artifacts,
                    LocalizationService.T("ConversionUiNativeCddaReadCompleted"),
                    string.Empty,
                    OpticalDiscReadBackend.WindowsNative);
            }
            catch (Exception exception) when (CanFallbackFromNativeRead(exception, cancellationToken))
            {
                ReportProgress(
                    progress,
                    request.Operation,
                    0,
                    LocalizationService.T("ConversionUiUsingExternalAudioRipper"));
                var fallback = await ConvertAudioCdWithExternalToolAsync(
                    request,
                    progress,
                    cancellationToken);
                return MarkAsFallback(fallback, exception);
            }
        }

        return await ConvertAudioCdWithExternalToolAsync(request, progress, cancellationToken);
    }

    private Task<OpticalDiscConversionResult> ConvertAudioCdWithExternalToolAsync(
        OpticalDiscConversionRequest request,
        IProgress<OpticalDiscProgress>? progress,
        CancellationToken cancellationToken) =>
        ConvertDirectoryAsync(
            request,
            OpticalDiscMediaKind.AudioCd,
            OpticalDiscToolKind.AudioCdRipper,
            ".wav",
            (executable, staging) => OpticalDiscCommandFactory.BuildAudioCdToWavTracks(
                executable,
                request.Source.RootPath,
                staging),
            progress,
            cancellationToken);

    private async Task<OpticalDiscConversionResult> ConvertDirectoryAsync(
        OpticalDiscConversionRequest request,
        OpticalDiscMediaKind expectedMediaKind,
        OpticalDiscToolKind toolKind,
        string artifactExtension,
        Func<string, string, OpticalDiscToolCommand> commandFactory,
        IProgress<OpticalDiscProgress>? progress,
        CancellationToken cancellationToken,
        OpticalDiscToolInfo? resolvedTool = null)
    {
        EnsureMediaKind(request.Source, expectedMediaKind);
        var tool = resolvedTool ?? await RequireToolAsync(toolKind, cancellationToken);
        var destination = PrepareDirectoryDestination(request.OutputPath);
        ReportProgress(progress, request.Operation, 0d, LocalizationService.T("ConversionUiPreparingStagingDirectory"));

        try
        {
            var command = commandFactory(tool.ExecutablePath!, destination.StagingPath);
            var execution = await _commandExecutor.ExecuteAsync(command, progress, cancellationToken);
            EnsureSuccessful(tool, execution);
            cancellationToken.ThrowIfCancellationRequested();

            var stagedArtifacts = Directory.EnumerateFiles(
                    destination.StagingPath,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(
                    artifactExtension,
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => new FileInfo(path).Length > 0)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (stagedArtifacts.Count == 0)
            {
                throw new InvalidDataException(
                    LocalizationService.T("ConversionUiToolNoValidOutput", tool.DisplayName, artifactExtension));
            }

            var relativeArtifacts = stagedArtifacts
                .Select(path => Path.GetRelativePath(destination.StagingPath, path))
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(destination.StagingPath, destination.FinalPath);
            var committedArtifacts = relativeArtifacts
                .Select(path => Path.Combine(destination.FinalPath, path))
                .ToList()
                .AsReadOnly();
            ReportProgress(progress, request.Operation, 100d, LocalizationService.T("ConversionUiOutputCommitted"));
            return new OpticalDiscConversionResult(
                request.Operation,
                destination.FinalPath,
                committedArtifacts,
                execution.StandardOutput,
                execution.StandardError);
        }
        finally
        {
            TryDeleteStagingDirectory(destination.StagingPath);
        }
    }

    private async Task<OpticalDiscConversionResult> ConvertIsoAsync(
        OpticalDiscConversionRequest request,
        IProgress<OpticalDiscProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Source.MediaKind is not (
            OpticalDiscMediaKind.DvdVideo or
            OpticalDiscMediaKind.DataDisc or
            OpticalDiscMediaKind.AudioCd))
        {
            throw new InvalidOperationException(
                LocalizationService.T("ConversionUiReadomRequiresRecognizedDisc"));
        }

        if (_nativeReader is not null &&
            request.Source.MediaKind is OpticalDiscMediaKind.DvdVideo or OpticalDiscMediaKind.DataDisc)
        {
            try
            {
                var outputPath = await _nativeReader.ReadDataDiscToIsoAsync(
                    request.Source.DevicePath,
                    request.OutputPath,
                    AdaptNativeProgress(progress, request.Operation),
                    cancellationToken);
                return new OpticalDiscConversionResult(
                    request.Operation,
                    outputPath,
                    Array.AsReadOnly([outputPath]),
                    LocalizationService.T("ConversionUiNativeDiscReadCompleted"),
                    string.Empty,
                    OpticalDiscReadBackend.WindowsNative);
            }
            catch (Exception exception) when (CanFallbackFromNativeRead(exception, cancellationToken))
            {
                ReportProgress(
                    progress,
                    request.Operation,
                    0,
                    LocalizationService.T("ConversionUiUsingReadomFallback"));
                var fallback = await ConvertIsoWithReadomAsync(
                    request,
                    progress,
                    cancellationToken);
                return MarkAsFallback(fallback, exception);
            }
        }

        return await ConvertIsoWithReadomAsync(request, progress, cancellationToken);
    }

    private async Task<OpticalDiscConversionResult> ConvertIsoWithReadomAsync(
        OpticalDiscConversionRequest request,
        IProgress<OpticalDiscProgress>? progress,
        CancellationToken cancellationToken)
    {

        var tool = await RequireToolAsync(OpticalDiscToolKind.Readom, cancellationToken);
        var destination = PrepareIsoDestination(request.OutputPath);
        ReportProgress(progress, request.Operation, 0d, LocalizationService.T("ConversionUiPreparingStagingFile"));

        try
        {
            var command = OpticalDiscCommandFactory.BuildDiscToIso(
                tool.ExecutablePath!,
                request.Source.RootPath,
                destination.StagingPath);
            var execution = await _commandExecutor.ExecuteAsync(command, progress, cancellationToken);
            EnsureSuccessful(tool, execution);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(destination.StagingPath) ||
                new FileInfo(destination.StagingPath).Length == 0)
            {
                throw new InvalidDataException(LocalizationService.T("ConversionUiReadomNoValidIso"));
            }

            File.Move(destination.StagingPath, destination.FinalPath, overwrite: false);
            ReportProgress(progress, request.Operation, 100d, LocalizationService.T("ConversionUiOutputCommitted"));
            return new OpticalDiscConversionResult(
                request.Operation,
                destination.FinalPath,
                Array.AsReadOnly(new[] { destination.FinalPath }),
                execution.StandardOutput,
                execution.StandardError);
        }
        finally
        {
            TryDeleteStagingFile(destination.StagingPath);
        }
    }

    internal static bool CanFallbackFromNativeRead(
        Exception exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is Win32Exception or IOException or UnauthorizedAccessException or NotSupportedException;

    private static IProgress<OpticalOperationProgress>? AdaptNativeProgress(
        IProgress<OpticalDiscProgress>? progress,
        OpticalDiscOperation operation) =>
        progress is null ? null : new NativeProgressAdapter(progress, operation);

    private static OpticalDiscConversionResult MarkAsFallback(
        OpticalDiscConversionResult result,
        Exception nativeError)
    {
        var note = LocalizationService.T("ConversionUiNativeReadFailed", nativeError.Message);
        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? note
            : note + Environment.NewLine + result.StandardError;
        return result with
        {
            StandardError = error,
            Backend = OpticalDiscReadBackend.ExternalToolFallback
        };
    }

    private async Task<OpticalDiscToolInfo> RequireToolAsync(
        OpticalDiscToolKind kind,
        CancellationToken cancellationToken)
    {
        var tool = await _toolDiscovery.DiscoverAsync(kind, cancellationToken);
        if (!tool.IsAvailable || string.IsNullOrWhiteSpace(tool.ExecutablePath))
        {
            throw new InvalidOperationException(tool.StatusMessage);
        }

        return tool;
    }

    private static void EnsureMediaKind(
        OpticalDiscInfo source,
        OpticalDiscMediaKind expectedMediaKind)
    {
        if (source.MediaKind != expectedMediaKind)
        {
            throw new InvalidOperationException(
                LocalizationService.T("ConversionUiBackendMediaKindMismatch", expectedMediaKind, source.MediaKind));
        }
    }

    private static void EnsureSuccessful(
        OpticalDiscToolInfo tool,
        OpticalDiscCommandExecutionResult execution)
    {
        if (execution.ExitCode == 0)
        {
            return;
        }

        var details = FirstNonEmpty(
            execution.StandardError,
            execution.StandardOutput,
            LocalizationService.T("ConversionUiNoErrorDetails"));
        throw new InvalidOperationException(
            LocalizationService.T("ConversionUiToolExecutionFailed", tool.DisplayName, execution.ExitCode, details));
    }

    private static (string FinalPath, string StagingPath) PrepareDirectoryDestination(
        string outputDirectory)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiOutputDirectoryNotDriveRoot"), nameof(outputDirectory));
        }

        EnsureDestinationDoesNotExist(fullPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiOutputDirectoryNeedsParent"), nameof(outputDirectory));
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiOutputDirectoryNameInvalid"), nameof(outputDirectory));
        }

        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{name}.{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        return (fullPath, staging);
    }

    private static (string FinalPath, string StagingPath) PrepareIsoDestination(string outputFile)
    {
        var fullPath = Path.GetFullPath(outputFile);
        if (!Path.GetExtension(fullPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiReadomOutputMustBeIso"), nameof(outputFile));
        }

        EnsureDestinationDoesNotExist(fullPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiIsoOutputNeedsParent"), nameof(outputFile));
        Directory.CreateDirectory(parent);
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var staging = Path.Combine(parent, $".{baseName}.{Guid.NewGuid():N}.partial.iso");
        return (fullPath, staging);
    }

    private static void EnsureDestinationDoesNotExist(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException(LocalizationService.T("ConversionUiOutputExistsRefuseOverwrite", path));
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value)).Trim();

    private static void ReportProgress(
        IProgress<OpticalDiscProgress>? progress,
        OpticalDiscOperation operation,
        double percentage,
        string message)
    {
        try
        {
            progress?.Report(new OpticalDiscProgress(operation, percentage, message));
        }
        catch
        {
            // A progress sink must not affect conversion correctness.
        }
    }

    private sealed class NativeProgressAdapter(
        IProgress<OpticalDiscProgress> destination,
        OpticalDiscOperation operation) : IProgress<OpticalOperationProgress>
    {
        public void Report(OpticalOperationProgress value)
        {
            try
            {
                destination.Report(new OpticalDiscProgress(
                    operation,
                    value.Percentage,
                    value.Message));
            }
            catch
            {
                // Progress observers do not control conversion correctness.
            }
        }
    }

    private static void TryDeleteStagingDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(".", StringComparison.Ordinal) ||
            !name.EndsWith(".partial", StringComparison.Ordinal) ||
            !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup is best effort and must not hide the conversion error.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort and must not hide the conversion error.
        }
    }

    private static void TryDeleteStagingFile(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(".", StringComparison.Ordinal) ||
            !name.EndsWith(".partial.iso", StringComparison.Ordinal) ||
            !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup is best effort and must not hide the conversion error.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort and must not hide the conversion error.
        }
    }
}

