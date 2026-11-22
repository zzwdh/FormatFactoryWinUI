using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

/// <summary>
/// Unified pipeline for physical optical media and ISO/CSO images.
/// Managed image conversion is always available. Windows native sector reads
/// are preferred for audio CDs and data discs; local tools are fallbacks.
/// </summary>
public sealed class DiscConversionService
{
    public const string EngineId = "disc-pipeline";

    private readonly OpticalDiscDiscovery _discDiscovery;
    private readonly DiscImageConverter _imageConverter;
    private readonly IWindowsOpticalDiscReader _nativeDiscReader;
    private readonly OutputPathService _paths = new();
    private readonly OutputRecoveryService _recovery = OutputRecoveryService.Shared;

    public DiscConversionService(
        OpticalDiscDiscovery? discDiscovery = null,
        DiscImageConverter? imageConverter = null,
        IWindowsOpticalDiscReader? nativeDiscReader = null)
    {
        _discDiscovery = discDiscovery ?? new OpticalDiscDiscovery();
        _imageConverter = imageConverter ?? new DiscImageConverter();
        _nativeDiscReader = nativeDiscReader ?? new WindowsOpticalDiscReader();
    }

    internal DiscConversionService(OutputPathService paths, OutputRecoveryService recovery,
        DiscImageConverter? imageConverter = null)
        : this(imageConverter: imageConverter)
    {
        _paths = paths;
        _recovery = recovery;
    }

    public async Task<ConversionResult> ConvertAsync(
        MediaTask task,
        AppSettings settings,
        string ffmpegPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(report);
        if (task.ContentKind != ContentKind.Disc)
        {
            throw new InvalidOperationException(LocalizationService.T("ConversionUiDiscPipelineOnlyDiscTasks"));
        }

        var targetId = string.IsNullOrWhiteSpace(task.TargetFormatId)
            ? FormatCatalog.NormalizeFormatId(task.TargetFormat)
            : task.TargetFormatId;
        var sourceId = task.SourceFormatId;
        var logPath = LogService.CreateTaskLogPath(task.Id);
        using var logProtection = LogFilePolicy.Shared.Protect(logPath);
        try
        {
            return OpticalDiscFormatIds.IsPhysicalDisc(sourceId)
                ? await ConvertPhysicalDiscAsync(
                    task,
                    settings,
                    ffmpegPath,
                    targetId,
                    logPath,
                    report,
                    cancellationToken)
                : await ConvertImageAsync(
                    task,
                    settings,
                    sourceId,
                    targetId,
                    logPath,
                    report,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ConversionResult(
                false,
                true,
                string.Empty,
                LocalizationService.T("Cancelled"),
                logPath);
        }
        catch (OutputCommitException error)
        {
            LogService.AppendTask(logPath, $"Validated disc output could not be committed: {error}\nPreserved output: {error.PreservedPath}\n");
            return new(false, false, string.Empty, error.Message, logPath)
            {
                RecoverableOutputId = error.RecoveryId,
                PreservedOutputPath = error.PreservedPath
            };
        }
        catch (Exception error)
        {
            FormatFailureDiagnostics.WriteTask(logPath, error, task.Id);
            throw;
        }
        finally
        {
            try { LogService.FlushTask(logPath); }
            catch (Exception error) { SafeDiagnostics.Report(error, notifySubscribers: false); }
        }
    }

    private async Task<ConversionResult> ConvertImageAsync(
        MediaTask task,
        AppSettings settings,
        string sourceId,
        string targetId,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        var formats = (Source: ParseImageFormat(sourceId), Target: ParseImageFormat(targetId));
        if (formats.Source == formats.Target)
        {
            throw new InvalidOperationException(LocalizationService.T("ConversionUiDiscImageTargetsMustDiffer"));
        }

        using var output = ReserveOutput(task, settings, FormatCatalog.ExtensionFor(targetId));
        var progress = new Progress<DiscImageConversionProgress>(value =>
            report(new ConversionProgress(
                value.Percent,
                "—",
                "—",
                LocalizationService.T("Converting"))));
        await _imageConverter.ConvertAsync(
            task.InputPath,
            formats.Source,
            output.Path,
            formats.Target,
            progress,
            cancellationToken);
        return CommitOutput(output, logPath, report, cancellationToken);
    }

    private async Task<ConversionResult> ConvertPhysicalDiscAsync(
        MediaTask task,
        AppSettings settings,
        string ffmpegPath,
        string targetId,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        var source = await _discDiscovery.FindByRootPathAsync(task.InputPath, cancellationToken)
            ?? throw new IOException(LocalizationService.T("ConversionUiTaskDriveNotFound"));
        if (!source.IsReady || source.MediaKind == OpticalDiscMediaKind.Unknown)
        {
            throw new IOException(LocalizationService.T("ConversionUiDiscNoRecognizableMedia"));
        }

        var optical = new OpticalDiscConversionService(
            _nativeDiscReader,
            new OpticalDiscToolDiscovery(new OpticalDiscToolConfiguration(
                settings.MakeMkvPath,
                settings.AudioCdRipperPath,
                settings.ReadomPath)));
        var plan = OpticalDiscBackendPlanner.Plan(source.SourceFormatId, targetId);
        if (!plan.IsSupported)
        {
            throw new NotSupportedException(
                LocalizationService.T("ConversionUiDiscRouteUnsupported", source.MediaKind, targetId.ToUpperInvariant()));
        }
        var opticalProgress = new Progress<OpticalDiscProgress>(value =>
            report(new ConversionProgress(
                value.Percentage ?? 0,
                "—",
                "—",
                string.IsNullOrWhiteSpace(value.Message)
                    ? LocalizationService.T("Converting")
                    : value.Message)));

        if (source.MediaKind == OpticalDiscMediaKind.BluRay && targetId == "mkv")
        {
            using var output = ReserveOutputDirectory(task, settings, " MKV");
            var result = await optical.ConvertBluRayToMkvAsync(
                source,
                output.Path,
                opticalProgress,
                cancellationToken);
            WriteExternalLog(logPath, result.StandardOutput, result.StandardError);
            return CommitOutput(output, logPath, report, cancellationToken);
        }

        if (source.MediaKind == OpticalDiscMediaKind.AudioCd &&
            FormatCatalog.IsAudioFormat(targetId))
        {
            return await ConvertAudioCdAsync(
                task,
                settings,
                source,
                optical,
                ffmpegPath,
                targetId,
                logPath,
                report,
                cancellationToken);
        }

        if (source.MediaKind is OpticalDiscMediaKind.DvdVideo or OpticalDiscMediaKind.DataDisc &&
            targetId is "iso" or "cso")
        {
            return await CreateDiscImageAsync(
                task,
                settings,
                source,
                optical,
                targetId,
                logPath,
                report,
                cancellationToken);
        }

        throw new NotSupportedException(
            LocalizationService.T("ConversionUiDiscRouteUnsupported", source.MediaKind, targetId.ToUpperInvariant()));
    }

    private async Task<ConversionResult> CreateDiscImageAsync(
        MediaTask task,
        AppSettings settings,
        OpticalDiscInfo source,
        OpticalDiscConversionService optical,
        string targetId,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        using var output = ReserveOutput(task, settings, FormatCatalog.ExtensionFor(targetId));
        if (targetId == "iso")
        {
            var progress = new Progress<OpticalDiscProgress>(value =>
                report(new ConversionProgress(
                    value.Percentage ?? 0,
                    "—",
                    "—",
                    value.Message)));
            var result = await optical.CreateIsoAsync(source, output.Path, progress, cancellationToken);
            WriteExternalLog(logPath, result.StandardOutput, result.StandardError);
            return CommitOutput(output, logPath, report, cancellationToken);
        }

        var intermediateIso = Path.Combine(
            Path.GetDirectoryName(output.Path)!,
            $".{Path.GetFileNameWithoutExtension(output.Path)}.{Guid.NewGuid():N}.source.iso");
        using var intermediateBudget = DiskSpaceBudgetService.Shared.Reserve(task,
            Path.GetDirectoryName(intermediateIso)!, intermediateIso);
        try
        {
            var readProgress = new Progress<OpticalDiscProgress>(value =>
                report(new ConversionProgress(
                    (value.Percentage ?? 0) * 0.75,
                    "—",
                    "—",
                    value.Message)));
            var image = await optical.CreateIsoAsync(
                source,
                intermediateIso,
                readProgress,
                cancellationToken);
            WriteExternalLog(logPath, image.StandardOutput, image.StandardError);

            var compressProgress = new Progress<DiscImageConversionProgress>(value =>
                report(new ConversionProgress(
                    75 + (value.Percent * 0.25),
                    "—",
                    "—",
                    LocalizationService.T("Converting"))));
            await _imageConverter.ConvertIsoToCsoAsync(
                intermediateIso,
                output.Path,
                compressProgress,
                cancellationToken);
            return CommitOutput(output, logPath, report, cancellationToken);
        }
        finally
        {
            TryDeleteFile(intermediateIso);
        }
    }

    private async Task<ConversionResult> ConvertAudioCdAsync(
        MediaTask task,
        AppSettings settings,
        OpticalDiscInfo source,
        OpticalDiscConversionService optical,
        string ffmpegPath,
        string targetId,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        using var output = ReserveOutputDirectory(
            task,
            settings,
            $" {targetId.ToUpperInvariant()}");
        if (targetId == "wav")
        {
            var progress = new Progress<OpticalDiscProgress>(value =>
                report(new ConversionProgress(
                    value.Percentage ?? 0,
                    "—",
                    "—",
                    value.Message)));
            var result = await optical.ExtractAudioCdToWavTracksAsync(
                source,
                output.Path,
                progress,
                cancellationToken);
            WriteExternalLog(logPath, result.StandardOutput, result.StandardError);
            return CommitOutput(output, logPath, report, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new InvalidOperationException(LocalizationService.T("RequiresFfmpegForCdAudio"));
        }

        var root = Path.GetDirectoryName(output.Path)!;
        var wavSource = Path.Combine(
            root,
            $".{Path.GetFileName(output.Path)}.{Guid.NewGuid():N}.wav-source");
        var staging = Path.Combine(
            root,
            $".{Path.GetFileName(output.Path)}.{Guid.NewGuid():N}.partial");
        using var intermediateBudget = DiskSpaceBudgetService.Shared.Reserve(task, root, wavSource);
        try
        {
            var ripProgress = new Progress<OpticalDiscProgress>(value =>
                report(new ConversionProgress(
                    (value.Percentage ?? 0) * 0.45,
                    "—",
                    "—",
                    value.Message)));
            var ripped = await optical.ExtractAudioCdToWavTracksAsync(
                source,
                wavSource,
                ripProgress,
                cancellationToken);
            WriteExternalLog(logPath, ripped.StandardOutput, ripped.StandardError);
            var tracks = ripped.OutputArtifacts
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (tracks.Length == 0)
            {
                throw new InvalidDataException(LocalizationService.T("ConversionUiAudioCdNoWavTracks"));
            }

            Directory.CreateDirectory(staging);
            for (var index = 0; index < tracks.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report(new ConversionProgress(
                    45 + (index * 54d / tracks.Length),
                    "—",
                    "—",
                    LocalizationService.T("ConversionUiEncodingTrackProgress", index + 1, tracks.Length)));
                var targetPath = Path.Combine(
                    staging,
                    Path.GetFileNameWithoutExtension(tracks[index]) +
                    FormatCatalog.ExtensionFor(targetId));
                var arguments = BuildAudioArguments(tracks[index], targetPath, targetId);
                var execution = await ProcessRunner.RunLoggedAsync(
                    ffmpegPath,
                    arguments,
                    TimeSpan.FromHours(4),
                    cancellationToken);
                LogService.AppendTask(
                    logPath,
                    execution.StandardOutput + Environment.NewLine + execution.StandardError);
                if (execution.ExitCode != 0 ||
                    !File.Exists(targetPath) ||
                    new FileInfo(targetPath).Length == 0)
                {
                    throw new InvalidDataException(
                        LocalizationService.T("ConversionUiTrackEncodingFailed", index + 1, targetId.ToUpperInvariant()));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, output.Path);
            return CommitOutput(output, logPath, report, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(wavSource);
            TryDeleteDirectory(staging);
        }
    }

    internal static IReadOnlyList<string> BuildAudioArguments(
        string inputPath,
        string outputPath,
        string targetFormatId)
    {
        var codec = FormatCatalog.NormalizeFormatId(targetFormatId) switch
        {
            "mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
            "m4a" or "aac" => new[] { "-c:a", "aac", "-b:a", "256k" },
            "opus" => new[] { "-c:a", "libopus", "-b:a", "192k" },
            "ogg" or "vorbis" => new[] { "-c:a", "libvorbis", "-q:a", "6" },
            "flac" => new[] { "-c:a", "flac" },
            "alac" => new[] { "-c:a", "alac" },
            "wav" => new[] { "-c:a", "pcm_s16le" },
            "aiff" => new[] { "-c:a", "pcm_s16be" },
            "ac3" => new[] { "-c:a", "ac3", "-b:a", "448k" },
            "eac3" => new[] { "-c:a", "eac3", "-b:a", "640k" },
            "wma" => new[] { "-c:a", "wmav2", "-b:a", "256k" },
            var id => throw new NotSupportedException(LocalizationService.T("ConversionUiAudioCdOutputUnsupported", id))
        };
        return
        [
            "-y",
            "-nostdin",
            "-i",
            inputPath,
            "-map_metadata",
            "0",
            "-vn",
            .. codec,
            outputPath
        ];
    }

    private static DiscImageFormat ParseImageFormat(string formatId) =>
        FormatCatalog.NormalizeFormatId(formatId) switch
        {
            "iso" => DiscImageFormat.Iso,
            "cso" => DiscImageFormat.Cso,
            var id => throw new NotSupportedException(LocalizationService.T("ConversionUiDiscImageFormatUnsupported", id))
        };

    private static ConversionResult Success(string outputPath, string logPath) =>
        new(
            true,
            false,
            outputPath,
            LocalizationService.T("ConversionFinished"),
            logPath);

    private static ConversionResult CommitOutput(OutputReservation output, string logPath,
        Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        output.CommitValidated(cancellationToken);
        ConversionService.ReportCommittedProgress(report);
        return Success(output.FinalPath, logPath);
    }

    private static void WriteExternalLog(
        string logPath,
        string standardOutput,
        string standardError) =>
        LogService.AppendTask(
            logPath,
            standardOutput + Environment.NewLine + standardError);

    private OutputReservation ReserveOutput(
        MediaTask task,
        AppSettings settings,
        string extension) =>
        Reserve(task, settings, extension, directory: false);

    private OutputReservation ReserveOutputDirectory(
        MediaTask task,
        AppSettings settings,
        string suffix) =>
        Reserve(task, settings, suffix, directory: true);

    private OutputReservation Reserve(
        MediaTask task,
        AppSettings settings,
        string suffix,
        bool directory)
    {
        return new OutputReservation(_paths.Reserve(task, settings, suffix, directory,
            ResolveBaseName(task)), task, _recovery);
    }

    private static string ResolveBaseName(MediaTask task)
    {
        if (!OpticalDiscFormatIds.IsPhysicalDisc(task.SourceFormatId))
        {
            return Path.GetFileNameWithoutExtension(task.InputPath);
        }
        if (task.ContentProbe.Metadata.TryGetValue("volumeLabel", out var label) &&
            !string.IsNullOrWhiteSpace(label))
        {
            return label;
        }
        return $"{task.SourceFormatId}-{OpticalDiscPath.ToDriveSpecifier(task.InputPath)[0]}";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class OutputReservation(OutputPathLease lease, MediaTask task, OutputRecoveryService recovery) : IDisposable
    {
        public string Path => lease.TemporaryPath;
        public string FinalPath => lease.FinalPath;
        public void CommitValidated(CancellationToken cancellationToken) => lease.CommitValidated(task.Id, recovery,
            cancellationToken, task.TargetFormatId, task.ConversionMode, EngineId);
        public void Dispose() => lease.Dispose();
    }
}
