using System.Globalization;
using System.Text.RegularExpressions;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class OutputPathService
{
    public const string DirectoryOverrideKey = "output.directoryOverride";
    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex TemplateTokens = new(@"\{(?:name|format|date|preset|width|height)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DeviceNames = new(@"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly DiskSpaceBudgetService _spaceBudget;

    public OutputPathService(DiskSpaceBudgetService? spaceBudget = null) => _spaceBudget = spaceBudget ?? DiskSpaceBudgetService.Shared;

    public static string NormalizeFileNameTemplate(string? template) => string.IsNullOrWhiteSpace(template) ? "{name}" : template.Trim();

    public static string? ValidateFileNameTemplate(string? template)
    {
        var normalized = NormalizeFileNameTemplate(template);
        var literal = TemplateTokens.Replace(normalized, string.Empty);
        if (normalized.Length > 160 || literal.IndexOfAny(['{', '}', '<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            literal.Any(char.IsControl) || normalized is "." or ".." || DeviceNames.IsMatch(normalized) ||
            normalized.Trim(' ', '.').Length == 0)
            return LocalizationService.T("OutputFileNameTemplateInvalid");
        return null;
    }

    public static string BuildFileName(MediaTask task, AppSettings settings, string? sourceBaseName = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);
        var template = NormalizeFileNameTemplate(settings.OutputFileNameTemplate);
        if (ValidateFileNameTemplate(template) is { } error) throw new ArgumentException(error, nameof(settings));
        var name = sourceBaseName ?? ResolveSourceBaseName(task);
        if (string.IsNullOrWhiteSpace(name)) name = task.FileName;
        if (string.IsNullOrWhiteSpace(name)) name = "output";
        var expanded = TemplateTokens.Replace(template, match => match.Value.ToLowerInvariant() switch
        {
            "{name}" => name,
            "{format}" => task.TargetFormat,
            "{date}" => DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            "{preset}" => string.IsNullOrWhiteSpace(task.OutputLabel) ? task.ConversionMode : task.OutputLabel.Trim(),
            "{width}" => task.Probe.Width > 0 ? task.Probe.Width.ToString(CultureInfo.InvariantCulture) : "unknown",
            "{height}" => task.Probe.Height > 0 ? task.Probe.Height.ToString(CultureInfo.InvariantCulture) : "unknown",
            _ => string.Empty
        });
        if (!string.IsNullOrWhiteSpace(task.OutputLabel) && !template.Contains("{preset}", StringComparison.OrdinalIgnoreCase))
            expanded += "_" + task.OutputLabel.Trim();
        // Source names and disc labels are untrusted too: substitutions never become directories.
        var safe = new string(expanded.Select(character => char.IsControl(character) ||
            "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..") safe = "output";
        if (DeviceNames.IsMatch(safe)) safe = "_" + safe;
        return safe.Length > 180 ? safe[..180].TrimEnd(' ', '.') : safe;
    }

    public OutputPathLease Reserve(MediaTask task, AppSettings settings)
    {
        var (suffix, directory) = DescribeOutput(task);
        return Reserve(task, settings, suffix, directory);
    }

    /// <summary>Reads the current destination without creating folders, probing write access or reserving space.</summary>
    public OutputPathPreview Preview(MediaTask task, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var (suffix, directory) = DescribeOutput(task);
        return Preview(task, settings, suffix, directory, cancellationToken: cancellationToken);
    }

    public OutputPathPreview Preview(MediaTask task, AppSettings settings, string suffix, bool isDirectory,
        string? sourceBaseName = null, string? explicitOutputPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (directory, name, extension) = ResolvePathParts(task, settings, suffix, sourceBaseName, explicitOutputPath);
        var candidate = FindAvailablePath(directory, name, extension, reserved: SnapshotReservations(), cancellationToken: cancellationToken);
        return new(candidate, isDirectory, !candidate.Equals(Path.Combine(directory, name + extension), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Also accounts for collisions between outputs in this preview, without publishing reservations.</summary>
    public IReadOnlyList<OutputPathPreview> PreviewMany(IEnumerable<MediaTask> tasks, AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        var reserved = SnapshotReservations();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OutputPathPreview>();
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (suffix, isDirectory) = DescribeOutput(task);
            var (directory, name, extension) = ResolvePathParts(task, settings, suffix, null, null);
            var candidate = FindAvailablePath(directory, name, extension, planned, reserved, cancellationToken);
            result.Add(new(candidate, isDirectory,
                !candidate.Equals(Path.Combine(directory, name + extension), StringComparison.OrdinalIgnoreCase)));
            planned.Add(candidate);
        }
        return result;
    }

    private static (string Suffix, bool IsDirectory) DescribeOutput(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (FormatCatalog.IsFrameSequence(task.TargetFormat)) return (" frames", true);
        if (task.ContentKind == ContentKind.Disc &&
            (task.SourceFormatId == OpticalDiscFormatIds.BluRayDisc && task.TargetFormatId == "mkv" ||
             task.SourceFormatId == OpticalDiscFormatIds.AudioCd && FormatCatalog.IsAudioFormat(task.TargetFormat)))
            return (" " + task.TargetFormatId.ToUpperInvariant(), true);
        return (FormatCatalog.ExtensionFor(task.TargetFormat), false);
    }

    private static string ResolveSourceBaseName(MediaTask task)
    {
        if (task.ContentKind == ContentKind.Disc && OpticalDiscFormatIds.IsPhysicalDisc(task.SourceFormatId))
        {
            if (task.ContentProbe.Metadata.TryGetValue("volumeLabel", out var label) && !string.IsNullOrWhiteSpace(label)) return label;
            return $"{task.SourceFormatId}-{OpticalDiscPath.ToDriveSpecifier(task.InputPath)[0]}";
        }
        return Path.GetFileNameWithoutExtension(task.InputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static (string Directory, string Name, string Suffix) ResolvePathParts(MediaTask task, AppSettings settings,
        string suffix, string? sourceBaseName, string? explicitOutputPath)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetFullPath(explicitOutputPath is null
            ? ResolveOutputDirectory(task, settings) : Path.GetDirectoryName(Path.GetFullPath(explicitOutputPath))!);
        return (directory, explicitOutputPath is null ? BuildFileName(task, settings, sourceBaseName)
            : Path.GetFileNameWithoutExtension(explicitOutputPath),
            explicitOutputPath is null ? suffix : Path.GetExtension(explicitOutputPath));
    }

    private static HashSet<string> SnapshotReservations()
    {
        // Preview performs file-system reads outside the reservation lock, especially for slow network shares.
        lock (SyncRoot) return new HashSet<string>(ReservedPaths, StringComparer.OrdinalIgnoreCase);
    }

    private static string FindAvailablePath(string directory, string name, string suffix, ISet<string>? planned = null,
        ISet<string>? reserved = null, CancellationToken cancellationToken = default)
    {
        var candidate = Path.Combine(directory, name + suffix);
        var index = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate) && !Directory.Exists(candidate) &&
                !(reserved ?? ReservedPaths).Contains(candidate) && planned?.Contains(candidate) != true) return candidate;
            candidate = Path.Combine(directory, $"{name} ({index++}){suffix}");
        }
    }

    /// <summary>Shared output protection for media, document and multi-artifact disc pipelines.</summary>
    public OutputPathLease Reserve(MediaTask task, AppSettings settings, string suffix, bool isDirectory,
        string? sourceBaseName = null, string? explicitOutputPath = null, bool writeDirectly = false)
    {
        var operationId = Guid.NewGuid();
        var stage = StorageFailureStage.ResolveDirectory;
        string? outputDirectory = null;
        string? candidate = null;
        try
        {
            var parts = ResolvePathParts(task, settings, suffix, sourceBaseName, explicitOutputPath);
            outputDirectory = parts.Directory;
            suffix = parts.Suffix;
            stage = StorageFailureStage.CreateDirectory;
            Directory.CreateDirectory(outputDirectory);
            stage = StorageFailureStage.WriteProbe;
            VerifyWriteAccess(outputDirectory);
            stage = StorageFailureStage.ReserveName;
            lock (SyncRoot)
            {
                candidate = FindAvailablePath(outputDirectory, parts.Name, suffix);
                ReservedPaths.Add(candidate);
            }
            var temporaryName = isDirectory ? $"{Path.GetFileName(candidate)}.{Guid.NewGuid():N}.partial"
                : $"{Path.GetFileNameWithoutExtension(candidate)}.{Guid.NewGuid():N}.partial{suffix}";
            var temporaryPath = Path.Combine(outputDirectory, temporaryName);
            stage = StorageFailureStage.BudgetReserve;
            var budget = _spaceBudget.Reserve(task, outputDirectory, writeDirectly ? candidate : temporaryPath, operationId);
            return new OutputPathLease(candidate, temporaryPath, isDirectory, () =>
            {
                budget.Dispose();
                Release(candidate);
            }, budget.Dispose, task.OutputGroupId is null ? null : task, task.Id, operationId);
        }
        catch (Exception error)
        {
            StorageFailureDiagnostics.Record(error, new(stage, operationId, task?.Id,
                StorageFailureDiagnostics.ClassifyPath(outputDirectory), isDirectory));
            if (candidate is not null) Release(candidate);
            throw;
        }
    }

    private static void Release(string path)
    {
        lock (SyncRoot) ReservedPaths.Remove(path);
    }

    internal static void VerifyWriteAccess(string directory, Action<string, string>? moveFile = null, Action<int>? delay = null)
    {
        var probe = Path.Combine(directory, $".{Guid.NewGuid():N}.write-check");
        var renamed = probe + ".renamed";
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var move = moveFile ?? ((source, destination) => File.Move(source, destination, overwrite: false));
            for (var attempt = 0; ; attempt++)
            {
                try { move(probe, renamed); break; }
                catch (IOException exception) when (attempt < 3 &&
                    exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
                {
                    // A scanner or another reader may briefly deny rename after creation.
                    // Only sharing/lock conflicts retry; denied access and other I/O failures still fail immediately.
                    (delay ?? Thread.Sleep)(50 << attempt);
                }
            }
            File.Delete(renamed);
        }
        finally
        {
            foreach (var path in new[] { probe, renamed })
                try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static OutputPathLease ReserveRecoveryDestination(string directory, string fileName, bool isDirectory,
        Guid? taskId = null, Guid? operationId = null)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var suffix = isDirectory ? string.Empty : Path.GetExtension(fileName);
        var name = isDirectory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        lock (SyncRoot)
        {
            var index = 1;
            while (File.Exists(candidate) || Directory.Exists(candidate) || ReservedPaths.Contains(candidate))
                candidate = Path.Combine(directory, $"{name} ({index++}){suffix}");
            ReservedPaths.Add(candidate);
        }
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.recovery-partial" + suffix);
        return new OutputPathLease(candidate, temporary, isDirectory, () => Release(candidate),
            diagnosticTaskId: taskId, diagnosticOperationId: operationId);
    }

    public static string ResolveOutputDirectory(MediaTask task, AppSettings settings)
    {
        if (task.Options.TryGetValue(DirectoryOverrideKey, out var chosenDirectory) && !string.IsNullOrWhiteSpace(chosenDirectory))
            return Path.GetFullPath(chosenDirectory);
        if (settings.OutputMode == "source" && task.ContentKind != ContentKind.Disc && !string.IsNullOrWhiteSpace(task.InputDirectory))
            return task.InputDirectory;
        // File-based disc images have a normal source directory; physical media have only a drive.
        if (settings.OutputMode == "source" && task.ContentKind == ContentKind.Disc &&
            !OpticalDiscFormatIds.IsPhysicalDisc(task.SourceFormatId) && !string.IsNullOrWhiteSpace(task.InputDirectory))
            return task.InputDirectory;
        var root = OutputDirectoryDefaults.ResolveConfigured(settings.CustomOutputDirectory);
        if (!settings.PreserveFolderStructure || string.IsNullOrWhiteSpace(task.SourceRoot)) return root;
        var relative = Path.GetRelativePath(task.SourceRoot, task.InputDirectory);
        var outsideRoot = Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        return outsideRoot ? root : Path.Combine(root, relative == "." ? string.Empty : relative);
    }
}

public sealed record OutputPathPreview(string FinalPath, bool IsDirectory, bool HasCollision);

public sealed class OutputPathLease : IDisposable
{
    private Action? _release;
    private Action? _committed;
    private readonly bool _isDirectory;
    private bool _preserveTemporary;
    private readonly MediaTask? _task;
    private readonly Guid? _diagnosticTaskId;
    private readonly Guid _diagnosticOperationId;
    internal OutputPathLease(string finalPath, string temporaryPath, bool isDirectory, Action release, Action? committed = null,
        MediaTask? task = null, Guid? diagnosticTaskId = null, Guid? diagnosticOperationId = null)
    {
        FinalPath = finalPath;
        TemporaryPath = temporaryPath;
        _isDirectory = isDirectory;
        _release = release;
        _committed = committed;
        _task = task;
        _diagnosticTaskId = diagnosticTaskId ?? task?.Id;
        _diagnosticOperationId = diagnosticOperationId ?? Guid.NewGuid();
        if (task is not null) OutputCommitReceiptStore.Shared.Register(task, finalPath, temporaryPath, isDirectory);
    }
    public string FinalPath { get; }
    public string TemporaryPath { get; }
    /// <summary>Call only after artifact validation succeeds. Failed commits retain the validated private artifact.</summary>
    public void CommitValidated(Guid taskId, OutputRecoveryService? recovery = null, CancellationToken cancellationToken = default,
        string? targetFormatId = null, string? conversionMode = null, string? engineId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { Commit(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!(_isDirectory ? Directory.Exists(TemporaryPath) : File.Exists(TemporaryPath))) throw;
            // Preservation failure must never make Dispose destroy an already validated artifact.
            _preserveTemporary = true;
            OutputRecoveryRecord? record = null;
            Exception? preservationError = null;
            try
            {
                record = (recovery ?? OutputRecoveryService.Shared).PreserveValidated(taskId, TemporaryPath, FinalPath, _isDirectory,
                    targetFormatId, conversionMode, engineId, _diagnosticOperationId);
                _preserveTemporary = false; // An indexed durable copy exists; retry cleanup of the original on Dispose.
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                StorageFailureDiagnostics.Record(error, new(StorageFailureStage.Preserve, _diagnosticOperationId,
                    taskId, StorageFailureDiagnostics.ClassifyPath(FinalPath), _isDirectory,
                    Recovery: StorageRecoveryState.Unindexed));
                preservationError = error;
            }
            StorageFailureDiagnostics.RecoveryOutcome(exception,
                record is null ? StorageRecoveryState.Unindexed : StorageRecoveryState.Indexed);
            throw new OutputCommitException(exception, record?.Id, record?.ArtifactPath ?? TemporaryPath, preservationError);
        }
    }
    public void Commit()
    {
        var stage = StorageFailureStage.CommitReceipt;
        try
        {
            ObjectDisposedException.ThrowIf(_release is null, this);
            if (_task is not null)
                OutputCommitReceiptStore.Shared.Prepare(_task, FinalPath, TemporaryPath, _isDirectory);
            stage = StorageFailureStage.Commit;
            OutputRecoveryService.Publish(TemporaryPath, FinalPath, _isDirectory);
        }
        catch (Exception error)
        {
            StorageFailureDiagnostics.Record(error, new(stage, _diagnosticOperationId, _diagnosticTaskId,
                StorageFailureDiagnostics.ClassifyPath(FinalPath), _isDirectory));
            throw;
        }
        // The committed bytes are already reflected in real free space. Keeping
        // a reservation tied to the old path would count them a second time.
        Interlocked.Exchange(ref _committed, null)?.Invoke();
    }
    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        if (_task is not null) OutputCommitReceiptStore.Shared.Unregister(FinalPath);
        Interlocked.Exchange(ref _committed, null);
        try
        {
            // Only the private path owned by this lease is removed. A committed file
            // and an unrelated file that appeared at FinalPath are never touched.
            if (!_preserveTemporary)
            {
                if (_isDirectory && Directory.Exists(TemporaryPath)) Directory.Delete(TemporaryPath, recursive: true);
                else if (File.Exists(TemporaryPath)) File.Delete(TemporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StorageFailureDiagnostics.Record(exception, new(StorageFailureStage.DisposeCleanup, _diagnosticOperationId,
                _diagnosticTaskId, StorageFailureDiagnostics.ClassifyPath(TemporaryPath), _isDirectory));
            StorageFailureDiagnostics.WriteRecorded(exception);
        }
        finally { release(); }
    }
}
