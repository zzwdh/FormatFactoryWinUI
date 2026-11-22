using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public sealed record MediaImportItemResult(
    string RequestedPath,
    MediaTask? Task,
    Exception? Error)
{
    public bool IsIgnored => Task is null && Error is null;
    internal ImportSourceSnapshot? SourceDiagnostic { get; init; }
}

public sealed record MediaImportBatchResult(IReadOnlyList<MediaImportItemResult> Items)
{
    public IReadOnlyList<MediaTask> AddedTasks =>
        Items.Where(item => item.Task is not null).Select(item => item.Task!).ToArray();

    public IReadOnlyList<MediaImportItemResult> Failures =>
        Items.Where(item => item.Error is not null).ToArray();

    public int IgnoredCount => Items.Count(item => item.IsIgnored);
}

/// <summary>
/// Owns the complete media-import use case: path recovery, duplicate reservation,
/// probing and initialization of the conversion task.
/// </summary>
public sealed partial class MediaImportCoordinator
{
    private readonly object _knownPathsGate = new();
    private readonly Dictionary<string, HashSet<PathReservation>> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, PathReservation> _taskReservations = [];
    private const int MaximumConcurrentImports = 2;
    private readonly Func<string, string, CancellationToken, Task<MediaProbeResult>> _probeAsync;
    private readonly OpticalDiscDiscovery _opticalDiscDiscovery;
    private readonly IImportStorageAccess _storageAccess;

    public MediaImportCoordinator(
        MediaProbeService? probeService = null,
        OpticalDiscDiscovery? opticalDiscDiscovery = null,
        IImportStorageAccess? storageAccess = null)
    {
        _probeAsync = (probeService ?? new MediaProbeService()).ProbeAsync;
        _opticalDiscDiscovery = opticalDiscDiscovery ?? new OpticalDiscDiscovery();
        _storageAccess = storageAccess ?? new LocalImportStorageAccess();
    }

    internal MediaImportCoordinator(
        Func<string, string, CancellationToken, Task<MediaProbeResult>> probeAsync,
        OpticalDiscDiscovery? opticalDiscDiscovery = null,
        IImportStorageAccess? storageAccess = null)
    {
        _probeAsync = probeAsync ?? throw new ArgumentNullException(nameof(probeAsync));
        _opticalDiscDiscovery = opticalDiscDiscovery ?? new OpticalDiscDiscovery();
        _storageAccess = storageAccess ?? new LocalImportStorageAccess();
    }

    public Task<MediaImportBatchResult> ImportPathsAsync(
        string ffprobePath,
        IEnumerable<string> paths,
        string? sourceRoot = null,
        string? sourceRootAccessToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ImportBatchAsync(
            ffprobePath,
            paths.Select(path => new BatchImportRequest(path, null)),
            sourceRoot,
            sourceRootAccessToken,
            cancellationToken);
    }

    public Task<MediaImportBatchResult> ImportPersistedBatchAsync(
        string ffprobePath,
        IEnumerable<PersistedQueueItem> persistedItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistedItems);
        return ImportBatchAsync(
            ffprobePath,
            persistedItems.Select(item => new BatchImportRequest(item.InputPath, item)),
            null,
            null,
            cancellationToken);
    }

    private async Task<MediaImportBatchResult> ImportBatchAsync(
        string ffprobePath,
        IEnumerable<BatchImportRequest> requests,
        string? sourceRoot,
        string? sourceRootAccessToken,
        CancellationToken cancellationToken)
    {
        using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var batchToken = batchCancellation.Token;
        var imports = new List<Task<MediaImportItemResult>>();
        var active = new List<(string PathKey, Task<MediaImportItemResult> Import)>();
        try
        {
            foreach (var request in requests)
            {
                batchToken.ThrowIfCancellationRequested();
                var pathKey = GetBatchPathKey(request.Path);
                var exclusive = request.Persisted is { } persisted &&
                    (persisted.Inputs.Count > 1 ||
                     (persisted.ContentKind == ContentKind.Disc &&
                      OpticalDiscFormatIds.IsPhysicalDisc(persisted.SourceFormatId)));
                // A duplicate must see the preceding attempt's final reservation,
                // including its release on failure. Groups and devices are barriers
                // because a group can reserve several paths and a device is shared.
                if (exclusive || active.Any(item =>
                        StringComparer.OrdinalIgnoreCase.Equals(item.PathKey, pathKey)))
                {
                    await Task.WhenAll(active.Select(item => item.Import)).ConfigureAwait(false);
                    active.Clear();
                }

                batchToken.ThrowIfCancellationRequested();
                var import = request.Persisted is not null
                    ? ImportPersistedAsync(ffprobePath, request.Persisted, batchToken)
                    : ImportWithReservationAsync(request.Path, persisted: null,
                        reservation => ImportCoreAsync(ffprobePath, request.Path, sourceRoot,
                            sourceRootAccessToken, persisted: null, batchToken, reservation));
                imports.Add(import);
                active.Add((pathKey, import));
                if (exclusive)
                {
                    await import.ConfigureAwait(false);
                    active.Clear();
                }
                else if (active.Count >= MaximumConcurrentImports)
                {
                    var completed = await Task.WhenAny(active.Select(item => item.Import)).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                    active.RemoveAll(item => ReferenceEquals(item.Import, completed));
                }
            }

            var results = await Task.WhenAll(imports).ConfigureAwait(false);
            batchToken.ThrowIfCancellationRequested();
            return new MediaImportBatchResult(results);
        }
        catch
        {
            batchCancellation.Cancel();
            // Wait for every in-flight import before releasing reservations. No
            // result reaches the caller when cancellation or enumeration fails.
            try { await Task.WhenAll(imports).ConfigureAwait(false); }
            catch { }
            foreach (var import in imports)
            {
                if (import.IsCompletedSuccessfully && import.Result.Task is { } task)
                {
                    Forget(task);
                }
            }
            throw;
        }
    }

    private sealed record BatchImportRequest(string Path, PersistedQueueItem? Persisted);

    private static string GetBatchPathKey(string path)
    {
        try { return NormalizePath(path); }
        catch (ArgumentException) { return path; }
        catch (NotSupportedException) { return path; }
        catch (PathTooLongException) { return path; }
    }

    /// <summary>
    /// Creates one ordered image-collection task. Image decoding is deferred to
    /// the Windows document pipeline, so FFprobe is intentionally optional.
    /// </summary>
    public Task<MediaImportItemResult> ImportImageCollectionAsync(
        string ffprobePath,
        IEnumerable<string> orderedPaths,
        string? sourceRoot = null,
        string? sourceRootAccessToken = null,
        CancellationToken cancellationToken = default)
    {
        _ = ffprobePath;
        ArgumentNullException.ThrowIfNull(orderedPaths);
        var inputs = orderedPaths.Select(path => new PersistedConversionInput { Path = path }).ToArray();
        return ImportWithReservationAsync(inputs.FirstOrDefault()?.Path ?? string.Empty, persisted: null,
            reservation => ImportImageCollectionCoreAsync(inputs, sourceRoot,
                sourceRootAccessToken, persisted: null, cancellationToken, reservation));
    }

    public Task<MediaImportItemResult> ImportPersistedAsync(
        string ffprobePath,
        PersistedQueueItem persisted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        return ImportWithReservationAsync(persisted.InputPath, persisted, reservation =>
        {
            if (persisted.ContentKind == ContentKind.Disc &&
                OpticalDiscFormatIds.IsPhysicalDisc(persisted.SourceFormatId))
            {
                return RestoreOpticalDiscAsync(persisted, cancellationToken, reservation);
            }
            if (persisted.Inputs.Count > 1)
            {
                return ImportPersistedGroupAsync(ffprobePath, persisted, cancellationToken, reservation);
            }
            return ImportCoreAsync(ffprobePath, persisted.InputPath, persisted.SourceRoot,
                persisted.SourceRootAccessToken, persisted, cancellationToken, reservation);
        });
    }

    public Task<MediaImportItemResult> ImportOpticalDiscAsync(
        OpticalDiscInfo disc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        cancellationToken.ThrowIfCancellationRequested();
        return ImportWithReservationAsync(disc.RootPath, persisted: null,
            reservation => Task.FromResult(ImportOpticalDiscCore(disc, persisted: null, reservation)));
    }

    public async Task<MediaImportBatchResult> ImportFolderAsync(
        string ffprobePath,
        string folderPath,
        string? sourceRootAccessToken = null,
        CancellationToken cancellationToken = default)
    {
        var files = await DiscoverFolderAsync(folderPath, cancellationToken: cancellationToken);

        return await ImportPathsAsync(
            ffprobePath,
            files,
            folderPath,
            sourceRootAccessToken,
            cancellationToken);
    }

    public void Forget(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_knownPathsGate)
        {
            if (_taskReservations.TryGetValue(task.Id, out var reservation))
            {
                ReleaseReservation(reservation);
            }
        }
    }

    /// <summary>
    /// Registers a task restored through another importer, such as a reprobed
    /// history entry. Its paths remain reserved until that task is forgotten.
    /// </summary>
    public void RememberTask(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var paths = task.InputPaths.Select(NormalizePath).ToArray();
        lock (_knownPathsGate)
        {
            if (_taskReservations.ContainsKey(task.Id)) return;
            var reservation = new PathReservation(task.Id, allowSharedPaths: true);
            _taskReservations.Add(task.Id, reservation);
            foreach (var path in paths) ReservePath(path, reservation);
        }
    }

    private async Task<MediaImportItemResult> ImportWithReservationAsync(
        string requestedPath,
        PersistedQueueItem? persisted,
        Func<PathReservation, Task<MediaImportItemResult>> importAsync)
    {
        var taskId = persisted?.TaskId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        using var diagnostic = persisted is null ? null : ImportSourceDiagnostics.Begin(taskId, persisted: true);
        var reservation = new PathReservation(taskId, allowSharedPaths: persisted is not null);
        lock (_knownPathsGate)
        {
            if (!_taskReservations.TryAdd(taskId, reservation))
            {
                ImportSourceDiagnostics.Observe(ImportSourceStage.Reservation, outcome: ImportSourceOutcome.Duplicate);
                return new MediaImportItemResult(requestedPath, null, null) { SourceDiagnostic = diagnostic?.Complete(null, null) };
            }
        }
        try
        {
            var result = await importAsync(reservation).ConfigureAwait(false);
            if (result.Task is null) ReleaseReservation(reservation);
            return diagnostic is null ? result : result with { SourceDiagnostic = diagnostic.Complete(result.Task, result.Error) };
        }
        catch (Exception error)
        {
            ReleaseReservation(reservation);
            diagnostic?.Complete(null, error);
            throw;
        }
    }

    private async Task<MediaImportItemResult> RestoreOpticalDiscAsync(
        PersistedQueueItem persisted,
        CancellationToken cancellationToken,
        PathReservation reservation)
    {
        try
        {
            var disc = await _opticalDiscDiscovery.FindByRootPathAsync(
                persisted.InputPath,
                cancellationToken);
            if (disc is null || !disc.IsReady)
            {
                var rootPath = OpticalDiscPath.NormalizeRootPath(persisted.InputPath);
                disc = new OpticalDiscInfo(
                    rootPath,
                    OpticalDiscPath.ToDevicePath(rootPath),
                    null,
                    OpticalDiscFormatIds.ToMediaKind(persisted.SourceFormatId),
                    isReady: false,
                    isFileSystemAccessible: false,
                    driveOrdinal: 0);
                return ImportOpticalDiscCore(disc, persisted, reservation, allowUnavailable: true);
            }

            return ImportOpticalDiscCore(disc, persisted, reservation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MediaImportItemResult(persisted.InputPath, null, ex);
        }
    }

    private MediaImportItemResult ImportOpticalDiscCore(
        OpticalDiscInfo disc,
        PersistedQueueItem? persisted,
        PathReservation reservation,
        bool allowUnavailable = false)
    {
        if ((!disc.IsReady && !allowUnavailable) ||
            disc.MediaKind == OpticalDiscMediaKind.Unknown)
        {
            return new MediaImportItemResult(
                disc.RootPath,
                null,
                new InvalidDataException(LocalizationService.T("ConversionUiDiscNoRecognizableMedia")));
        }

        var rootPath = OpticalDiscPath.NormalizeRootPath(disc.RootPath);
        if (!ReservePath(rootPath, reservation))
        {
            return new MediaImportItemResult(rootPath, null, null);
        }

        try
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["displayName"] = disc.DisplayName,
                ["mediaType"] = disc.MediaKind switch
                {
                    OpticalDiscMediaKind.BluRay => "Blu-ray",
                    OpticalDiscMediaKind.DvdVideo => "DVD-Video",
                    OpticalDiscMediaKind.DataDisc => "Data CD / DVD",
                    OpticalDiscMediaKind.AudioCd => "Audio CD",
                    _ => "Optical disc"
                },
                ["devicePath"] = disc.DevicePath,
                ["driveOrdinal"] = disc.DriveOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isReady"] = disc.IsReady ? "true" : "false"
            };
            if (!string.IsNullOrWhiteSpace(disc.VolumeLabel))
            {
                metadata["volumeLabel"] = disc.VolumeLabel;
            }

            var probe = new ContentProbeResult
            {
                ContentKind = ContentKind.Disc,
                FormatId = disc.SourceFormatId,
                Metadata = metadata
            };
            var task = new MediaTask
            {
                Id = reservation.TaskId,
                InputPath = rootPath,
                ContentKind = ContentKind.Disc,
                ContentProbe = probe,
                SourceFormatId = disc.SourceFormatId,
                FileSize = 0,
                EngineId = "disc-pipeline"
            };
            task.Inputs.Add(new ConversionInput(rootPath, disc.SourceFormatId, probe));
            foreach (var target in FormatCatalog.TargetsFor(ContentKind.Disc, disc.SourceFormatId))
            {
                task.AvailableFormats.Add(target);
            }

            var persistedTarget = FormatCatalog.FindById(persisted?.TargetFormatId)
                ?? FormatCatalog.FindById(persisted?.TargetFormat);
            task.TargetFormat = persistedTarget is not null &&
                                task.AvailableFormats.Contains(persistedTarget.DisplayName)
                ? persistedTarget.DisplayName
                : task.AvailableFormats.FirstOrDefault() ?? FormatCatalog.DefaultTarget(ContentKind.Disc);
            task.EngineId = "disc-pipeline";
            task.ConversionMode = NormalizeConversionMode(persisted?.ConversionMode);
            task.ParametersVersion = persisted?.ParametersVersion ?? 1;
            foreach (var option in persisted?.Options ?? [])
            {
                task.Options[option.Key] = option.Value;
            }

            return new MediaImportItemResult(rootPath, task, null);
        }
        catch (Exception ex)
        {
            return new MediaImportItemResult(rootPath, null, ex);
        }
    }

    private async Task<MediaImportItemResult> ImportCoreAsync(
        string ffprobePath,
        string path,
        string? sourceRoot,
        string? sourceRootAccessToken,
        PersistedQueueItem? persisted,
        CancellationToken cancellationToken,
        PathReservation reservation)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !FormatCatalog.SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            return new MediaImportItemResult(path, null, null);
        }

        string fullPath = path;
        try
        {
            var sourceFormat = FormatCatalog.FindById(persisted?.SourceFormatId)
                ?? FormatCatalog.FindByExtension(path);
            if (sourceFormat is null || !sourceFormat.CanImport)
            {
                return new MediaImportItemResult(path, null, null);
            }

            ImportSourceDiagnostics.Observe(ImportSourceStage.FileResolve, nextInput: true);
            fullPath = await _storageAccess.ResolveFilePathAsync(path, persisted?.InputAccessToken, cancellationToken);
            var accessToken = sourceRootAccessToken ?? persisted?.SourceRootAccessToken;
            ImportSourceDiagnostics.Observe(ImportSourceStage.RootResolve);
            var resolvedSourceRoot = await _storageAccess.ResolveFolderPathAsync(sourceRoot, accessToken, cancellationToken);
            var useResolvedRoot = !string.IsNullOrWhiteSpace(resolvedSourceRoot) &&
                !string.IsNullOrWhiteSpace(persisted?.SourceRoot) &&
                !string.IsNullOrWhiteSpace(accessToken);
            ImportSourceDiagnostics.Observe(ImportSourceStage.RootSelection,
                rootResolved: !string.IsNullOrWhiteSpace(resolvedSourceRoot), rootApplied: useResolvedRoot);
            if (useResolvedRoot)
            {
                var relativePath = Path.GetRelativePath(persisted!.SourceRoot!, path);
                fullPath = Path.Combine(resolvedSourceRoot!, relativePath);
            }

            fullPath = NormalizePath(fullPath);
            var exists = File.Exists(fullPath);
            ImportSourceDiagnostics.Observe(ImportSourceStage.Availability, fileExists: exists,
                outcome: exists ? null : ImportSourceOutcome.Unavailable);
            if (!exists)
            {
                return new MediaImportItemResult(path, null, null);
            }

            if (!ReservePath(fullPath, reservation))
            {
                ImportSourceDiagnostics.Observe(ImportSourceStage.Reservation, outcome: ImportSourceOutcome.Duplicate);
                return new MediaImportItemResult(path, null, null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var inputAccessToken = persisted?.InputAccessToken;
            if (string.IsNullOrWhiteSpace(inputAccessToken) && string.IsNullOrWhiteSpace(accessToken))
            {
                inputAccessToken = await _storageAccess.RememberFileAsync(fullPath, cancellationToken);
            }

            MediaProbeResult? mediaProbe = null;
            ContentProbeResult contentProbe;
            if (ImportPolicy.RequiresFfprobe(sourceFormat.ContentKind))
            {
                if (string.IsNullOrWhiteSpace(ffprobePath) && sourceFormat.ContentKind != ContentKind.Image)
                {
                    throw new InvalidOperationException(LocalizationService.T("ConversionUiMediaImportNeedsFfprobe"));
                }
                mediaProbe = await _probeAsync(ffprobePath, fullPath, cancellationToken);
                mediaProbe.FormatId = sourceFormat.Id;
                mediaProbe.FileSize = new FileInfo(fullPath).Length;
                contentProbe = mediaProbe;
            }
            else
            {
                contentProbe = new ContentProbeResult
                {
                    ContentKind = sourceFormat.ContentKind,
                    FormatId = sourceFormat.Id,
                    FileSize = new FileInfo(fullPath).Length
                };
            }

            var task = new MediaTask
            {
                Id = reservation.TaskId,
                InputPath = fullPath,
                SourceRoot = resolvedSourceRoot,
                InputAccessToken = inputAccessToken,
                SourceRootAccessToken = accessToken,
                ContentKind = contentProbe.ContentKind,
                ContentProbe = contentProbe,
                SourceFormatId = sourceFormat.Id,
                FileSize = contentProbe.FileSize
            };
            task.Inputs.Add(new ConversionInput(fullPath, sourceFormat.Id, contentProbe, inputAccessToken));

            var targets = mediaProbe is null
                ? FormatCatalog.TargetsFor(contentProbe.ContentKind, sourceFormat.Id)
                : FormatCatalog.TargetsFor(mediaProbe.Kind, mediaProbe.AudioTracks.Count > 0);
            foreach (var format in targets)
            {
                task.AvailableFormats.Add(format);
            }
            foreach (var track in mediaProbe?.AudioTracks ?? [])
            {
                task.AudioTracks.Add(track);
            }

            var persistedTarget = FormatCatalog.FindById(persisted?.TargetFormatId)
                ?? FormatCatalog.FindById(persisted?.TargetFormat);
            // Retain an older queue's explicit EPUB target so the UI can explain
            // the unsupported route instead of silently changing the requested output.
            if (sourceFormat.Id == "epub" && persistedTarget?.Id == "epub" &&
                !task.AvailableFormats.Contains(persistedTarget.DisplayName))
            {
                task.AvailableFormats.Add(persistedTarget.DisplayName);
            }
            var defaultTarget = FormatCatalog.DefaultTarget(contentProbe.ContentKind);
            task.TargetFormat = persistedTarget is not null && task.AvailableFormats.Contains(persistedTarget.DisplayName)
                ? persistedTarget.DisplayName
                : task.AvailableFormats.Contains(defaultTarget)
                    ? defaultTarget
                    : task.AvailableFormats.FirstOrDefault() ?? defaultTarget;
            if (!string.IsNullOrWhiteSpace(persisted?.EngineId))
            {
                task.EngineId = persisted.EngineId;
            }
            task.ConversionMode = NormalizeConversionMode(persisted?.ConversionMode);
            task.ParametersVersion = persisted?.ParametersVersion ?? 1;
            if (persisted?.Options is not null)
            {
                foreach (var option in persisted.Options)
                {
                    task.Options[option.Key] = option.Value;
                }
            }
            task.SelectedAudioTrack = Math.Clamp(
                persisted?.SelectedAudioTrack ?? 0,
                0,
                Math.Max(0, task.AudioTracks.Count - 1));

            cancellationToken.ThrowIfCancellationRequested();
            return new MediaImportItemResult(path, task, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MediaImportItemResult(path, null, ex);
        }
    }

    private async Task<MediaImportItemResult> ImportPersistedGroupAsync(
        string ffprobePath,
        PersistedQueueItem persisted,
        CancellationToken cancellationToken,
        PathReservation reservation)
    {
        if (persisted.Inputs.All(input =>
                FormatCatalog.FindById(input.FormatId)?.ContentKind == ContentKind.Image ||
                FormatCatalog.FindByExtension(input.Path)?.ContentKind == ContentKind.Image))
        {
            return await ImportImageCollectionCoreAsync(
                persisted.Inputs,
                persisted.SourceRoot,
                persisted.SourceRootAccessToken,
                persisted,
                cancellationToken,
                reservation);
        }

        var imported = new List<MediaTask>();
        foreach (var input in persisted.Inputs)
        {
            var single = new PersistedQueueItem
            {
                InputPath = input.Path,
                InputAccessToken = input.AccessToken,
                SourceRoot = persisted.SourceRoot,
                SourceRootAccessToken = persisted.SourceRootAccessToken,
                SourceFormatId = input.FormatId,
                ContentKind = persisted.ContentKind,
                TargetFormat = persisted.TargetFormat,
                TargetFormatId = persisted.TargetFormatId,
                EngineId = persisted.EngineId,
                ConversionMode = persisted.ConversionMode,
                ParametersVersion = persisted.ParametersVersion,
                Options = new Dictionary<string, string>(persisted.Options, StringComparer.OrdinalIgnoreCase),
                SelectedAudioTrack = persisted.SelectedAudioTrack
            };
            var result = await ImportCoreAsync(
                ffprobePath,
                input.Path,
                persisted.SourceRoot,
                persisted.SourceRootAccessToken,
                single,
                cancellationToken,
                reservation);
            if (result.Task is null)
            {
                return result.Error is null
                    ? new MediaImportItemResult(persisted.InputPath, null, new InvalidDataException(LocalizationService.T("ConversionUiMultiInputDuplicateOrUnavailable")))
                    : result;
            }
            imported.Add(result.Task);
        }

        var primary = imported[0];
        if (imported.Any(task => task.ContentKind != primary.ContentKind))
        {
            return new MediaImportItemResult(
                persisted.InputPath,
                null,
                new InvalidDataException(LocalizationService.T("ConversionUiMultiInputSameContentType")));
        }

        // Every input belongs to the same reservation, including shared paths
        // already used by another restored queue task.
        var inputs = imported.SelectMany(task => task.Inputs).ToArray();
        primary.Inputs.Clear();
        foreach (var input in inputs)
        {
            primary.Inputs.Add(input);
        }
        primary.FileSize = imported.Sum(task => task.FileSize);
        primary.AvailableFormats.Clear();
        foreach (var target in FormatCatalog.TargetsFor(primary.ContentKind, primary.SourceFormatId))
        {
            primary.AvailableFormats.Add(target);
        }
        var persistedTarget = FormatCatalog.FindById(persisted.TargetFormatId)
            ?? FormatCatalog.FindById(persisted.TargetFormat);
        primary.TargetFormat = persistedTarget is not null && primary.AvailableFormats.Contains(persistedTarget.DisplayName)
            ? persistedTarget.DisplayName
            : FormatCatalog.DefaultTarget(primary.ContentKind);
        if (!string.IsNullOrWhiteSpace(persisted.EngineId))
        {
            primary.EngineId = persisted.EngineId;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new MediaImportItemResult(persisted.InputPath, primary, null);
    }

    private async Task<MediaImportItemResult> ImportImageCollectionCoreAsync(
        IReadOnlyList<PersistedConversionInput> orderedInputs,
        string? sourceRoot,
        string? sourceRootAccessToken,
        PersistedQueueItem? persisted,
        CancellationToken cancellationToken,
        PathReservation reservation)
    {
        if (orderedInputs.Count == 0)
        {
            return new MediaImportItemResult(string.Empty, null, null);
        }

        try
        {
            ImportSourceDiagnostics.Observe(ImportSourceStage.RootResolve);
            var resolvedSourceRoot = await _storageAccess.ResolveFolderPathAsync(
                sourceRoot,
                sourceRootAccessToken,
                cancellationToken);
            var inputs = new List<ConversionInput>(orderedInputs.Count);
            long totalSize = 0;
            foreach (var requested in orderedInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = FormatCatalog.FindById(requested.FormatId)
                    ?? FormatCatalog.FindByExtension(requested.Path);
                if (descriptor is null ||
                    descriptor.ContentKind != ContentKind.Image ||
                    !descriptor.CanImport)
                {
                    throw new InvalidDataException(LocalizationService.T("ConversionUiUnsupportedImageInput", requested.Path));
                }

                ImportSourceDiagnostics.Observe(ImportSourceStage.FileResolve, nextInput: true);
                var fullPath = await _storageAccess.ResolveFilePathAsync(
                    requested.Path,
                    requested.AccessToken,
                    cancellationToken);
                var useResolvedRoot = !string.IsNullOrWhiteSpace(resolvedSourceRoot) &&
                    !string.IsNullOrWhiteSpace(sourceRoot) &&
                    !string.IsNullOrWhiteSpace(sourceRootAccessToken);
                ImportSourceDiagnostics.Observe(ImportSourceStage.RootSelection,
                    rootResolved: !string.IsNullOrWhiteSpace(resolvedSourceRoot), rootApplied: useResolvedRoot);
                if (useResolvedRoot)
                {
                    var relativePath = Path.GetRelativePath(sourceRoot!, requested.Path);
                    fullPath = Path.Combine(resolvedSourceRoot!, relativePath);
                }

                fullPath = NormalizePath(fullPath);
                var exists = File.Exists(fullPath);
                ImportSourceDiagnostics.Observe(ImportSourceStage.Availability, fileExists: exists,
                    outcome: exists ? null : ImportSourceOutcome.Unavailable);
                if (!exists)
                {
                    throw new FileNotFoundException(LocalizationService.T("ConversionUiImageFileMissing"), fullPath);
                }

                if (!ReservePath(fullPath, reservation))
                {
                    throw new InvalidDataException(LocalizationService.T("ConversionUiImageAlreadyQueued", fullPath));
                }

                var accessToken = requested.AccessToken;
                if (string.IsNullOrWhiteSpace(accessToken) &&
                    string.IsNullOrWhiteSpace(sourceRootAccessToken))
                {
                    accessToken = await _storageAccess.RememberFileAsync(fullPath, cancellationToken);
                }

                var size = new FileInfo(fullPath).Length;
                totalSize += size;
                inputs.Add(new ConversionInput(
                    fullPath,
                    descriptor.Id,
                    new ContentProbeResult
                    {
                        ContentKind = ContentKind.Image,
                        FormatId = descriptor.Id,
                        FileSize = size
                    },
                    accessToken));
            }

            var first = inputs[0];
            var task = new MediaTask
            {
                Id = reservation.TaskId,
                InputPath = first.Path,
                InputAccessToken = first.AccessToken,
                SourceRoot = resolvedSourceRoot,
                SourceRootAccessToken = sourceRootAccessToken,
                ContentKind = ContentKind.Image,
                ContentProbe = first.Probe!,
                SourceFormatId = first.FormatId,
                FileSize = totalSize
            };
            foreach (var input in inputs)
            {
                task.Inputs.Add(input);
            }
            foreach (var target in FormatCatalog.TargetsFor(ContentKind.Image, first.FormatId))
            {
                task.AvailableFormats.Add(target);
            }

            var persistedTarget = FormatCatalog.FindById(persisted?.TargetFormatId)
                ?? FormatCatalog.FindById(persisted?.TargetFormat);
            task.TargetFormat = persistedTarget is not null &&
                                task.AvailableFormats.Contains(persistedTarget.DisplayName)
                ? persistedTarget.DisplayName
                : "PDF";
            if (!string.IsNullOrWhiteSpace(persisted?.EngineId))
            {
                task.EngineId = persisted.EngineId;
            }
            task.ConversionMode = NormalizeConversionMode(persisted?.ConversionMode);
            task.ParametersVersion = persisted?.ParametersVersion ?? 1;
            foreach (var option in persisted?.Options ?? [])
            {
                task.Options[option.Key] = option.Value;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new MediaImportItemResult(orderedInputs[0].Path, task, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MediaImportItemResult(orderedInputs[0].Path, null, ex);
        }
    }

    private bool ReservePath(string path, PathReservation reservation)
    {
        lock (_knownPathsGate)
        {
            if (_knownPaths.TryGetValue(path, out var owners))
            {
                if (!reservation.AllowSharedPaths) return false;
            }
            else _knownPaths.Add(path, owners = []);
            owners.Add(reservation);
            reservation.Paths.Add(path);
            return true;
        }
    }

    private void ReleaseReservation(PathReservation reservation)
    {
        lock (_knownPathsGate)
        {
            if (!_taskReservations.TryGetValue(reservation.TaskId, out var current) ||
                !ReferenceEquals(current, reservation)) return;
            _taskReservations.Remove(reservation.TaskId);
            foreach (var path in reservation.Paths)
            {
                if (!_knownPaths.TryGetValue(path, out var owners)) continue;
                owners.Remove(reservation);
                if (owners.Count == 0) _knownPaths.Remove(path);
            }
        }
    }

    private sealed class PathReservation(Guid taskId, bool allowSharedPaths)
    {
        public Guid TaskId { get; } = taskId;
        public bool AllowSharedPaths { get; } = allowSharedPaths;
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string NormalizeConversionMode(string? mode) => mode switch
    {
        "快速" or "fast" => "fast",
        "高质量" or "quality" => "quality",
        "无损" or "lossless" => "lossless",
        "平衡" or "balanced" => "balanced",
        _ => FormatCatalog.DefaultConversionMode
    };
}
