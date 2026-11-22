using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>
/// A small write-ahead receipt closes the gap between publishing a validated artifact and
/// checkpointing its queue row. It records only paths owned by an output reservation.
/// </summary>
public sealed class OutputCommitReceiptStore
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    public static OutputCommitReceiptStore Shared { get; } = new();

    public OutputCommitReceiptStore(string? root = null) =>
        _root = Path.GetFullPath(root ?? Path.Combine(LogService.AppDataRoot, "output-commit-receipts"));

    internal void Register(MediaTask task, string finalPath, string temporaryPath, bool isDirectory) =>
        _reservations[Path.GetFullPath(finalPath)] = CaptureReservation(task, finalPath, temporaryPath, isDirectory);

    internal void Unregister(string finalPath) => _reservations.TryRemove(Path.GetFullPath(finalPath), out _);

    internal void Prepare(MediaTask task, string finalPath, string temporaryPath, bool isDirectory)
    {
        // The encoder may temporarily replace an input or audio selection for compatibility.
        // The receipt describes the user's admitted configuration, captured before encoding.
        var reservation = _reservations.TryGetValue(Path.GetFullPath(finalPath), out var admitted) && admitted.TaskId == task.Id
            ? admitted with { TemporaryPath = temporaryPath }
            : CaptureReservation(task, finalPath, temporaryPath, isDirectory);
        Write(new Receipt(reservation, Fingerprint(temporaryPath, isDirectory), CreationTime(temporaryPath, isDirectory)));
    }

    /// <summary>Used by Markdown, which owns the document and its resource-directory commit.</summary>
    public void PrepareRegisteredPath(string finalPath, string temporaryPath,
        string? companionTemporaryPath = null, string? companionFinalPath = null)
    {
        if (!_reservations.TryGetValue(Path.GetFullPath(finalPath), out var reservation)) return;
        var receipt = new Receipt(reservation with { TemporaryPath = temporaryPath }, Fingerprint(temporaryPath, false),
            CreationTime(temporaryPath, false));
        if (companionTemporaryPath is not null && companionFinalPath is not null)
            receipt = receipt with
            {
                CompanionTemporaryPath = companionTemporaryPath,
                CompanionFinalPath = companionFinalPath,
                CompanionFiles = Fingerprint(companionTemporaryPath, true),
                CompanionCreationTimeUtcTicks = CreationTime(companionTemporaryPath, true)
            };
        Write(receipt);
    }

    /// <summary>Recover only a byte-for-byte verified private or committed artifact; never reserve a new name.</summary>
    public string? TryRecover(MediaTask task)
    {
        lock (_gate)
        {
            if (task.OutputGroupId is null || !File.Exists(PathFor(task.Id))) return null;
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(PathFor(task.Id)));
            if (receipt?.Reservation is not { } reservation || receipt.Files is null || receipt.CompanionFiles is null ||
                receipt.CreationTimeUtcTicks <= 0 || reservation.TaskId != task.Id ||
                reservation.FinalPath is null || reservation.TemporaryPath is null ||
                !Path.IsPathFullyQualified(reservation.FinalPath) ||
                !Path.IsPathFullyQualified(reservation.TemporaryPath) ||
                !string.Equals(Path.GetDirectoryName(reservation.FinalPath), Path.GetDirectoryName(reservation.TemporaryPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reservation.TargetFormatId, task.TargetFormatId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reservation.ConversionMode, task.ConversionMode, StringComparison.OrdinalIgnoreCase) ||
                reservation.OutputGroupId != task.OutputGroupId ||
                !string.Equals(reservation.OutputLabel, task.OutputLabel, StringComparison.Ordinal) ||
                !string.Equals(reservation.MediaOptionsSignature, MediaOptionsSignature(task.Options), StringComparison.Ordinal) ||
                reservation.SelectedAudioTrack != task.SelectedAudioTrack) return null;
            if (receipt.CompanionFinalPath is { } finalAssets)
            {
                if (!Path.IsPathFullyQualified(finalAssets) || receipt.CompanionCreationTimeUtcTicks <= 0 ||
                    !string.Equals(Path.GetDirectoryName(finalAssets), Path.GetDirectoryName(reservation.FinalPath), StringComparison.OrdinalIgnoreCase)) return null;
                if (!Verify(finalAssets, true, receipt.CompanionFiles, receipt.CompanionCreationTimeUtcTicks))
                {
                    if (Directory.Exists(finalAssets) || File.Exists(finalAssets) || receipt.CompanionTemporaryPath is not { } stagedAssets ||
                        !Path.IsPathFullyQualified(stagedAssets) ||
                        !string.Equals(Path.GetDirectoryName(stagedAssets), Path.GetDirectoryName(finalAssets), StringComparison.OrdinalIgnoreCase) ||
                        !Verify(stagedAssets, true, receipt.CompanionFiles, receipt.CompanionCreationTimeUtcTicks)) return null;
                    Directory.Move(stagedAssets, finalAssets);
                }
            }
            if (Verify(reservation.FinalPath, reservation.IsDirectory, receipt.Files, receipt.CreationTimeUtcTicks))
                return reservation.FinalPath;
            if (File.Exists(reservation.FinalPath) || Directory.Exists(reservation.FinalPath) ||
                !Verify(reservation.TemporaryPath, reservation.IsDirectory, receipt.Files, receipt.CreationTimeUtcTicks)) return null;
            if (reservation.IsDirectory) Directory.Move(reservation.TemporaryPath, reservation.FinalPath);
            else File.Move(reservation.TemporaryPath, reservation.FinalPath, overwrite: false);
            return reservation.FinalPath;
        }
    }

    public void Forget(Guid taskId, string? finalPath = null)
    {
        lock (_gate)
        {
            try
            {
                if (finalPath is not null && File.Exists(PathFor(taskId)))
                {
                    var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(PathFor(taskId)));
                    if (receipt?.Reservation is not { } reservation ||
                        !string.Equals(reservation.FinalPath, finalPath, StringComparison.OrdinalIgnoreCase)) return;
                }
                File.Delete(PathFor(taskId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { SafeDiagnostics.Report(exception); }
        }
    }

    private void Write(Receipt receipt)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var path = PathFor(receipt.Reservation.TaskId);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, receipt);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private string PathFor(Guid taskId) => Path.Combine(_root, taskId.ToString("N") + ".json");
    private static long CreationTime(string path, bool isDirectory) =>
        (isDirectory ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path)).Ticks;
    private static bool Verify(string path, bool isDirectory, IReadOnlyList<FileFingerprint> files, long creationTime)
    {
        if (!(isDirectory ? Directory.Exists(path) : File.Exists(path))) return false;
        return CreationTime(path, isDirectory) == creationTime && files.SequenceEqual(Fingerprint(path, isDirectory));
    }

    private static FileFingerprint[] Fingerprint(string root, bool isDirectory)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("An output commit receipt cannot contain links.");
        IEnumerable<string> paths = isDirectory
            ? Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }).Order(StringComparer.Ordinal)
            : [root];
        return paths.Select(path =>
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An output commit receipt cannot contain links.");
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (directory) return new FileFingerprint(Path.GetRelativePath(root, path), true, 0, string.Empty);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new FileFingerprint(isDirectory ? Path.GetRelativePath(root, path) : string.Empty,
                false, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }).ToArray();
    }

    private static Reservation CaptureReservation(MediaTask task, string finalPath, string temporaryPath, bool isDirectory) =>
        new(task.Id, task.TargetFormatId, task.ConversionMode, task.SelectedAudioTrack, task.EngineId,
            finalPath, temporaryPath, isDirectory, task.OutputGroupId, task.OutputLabel, MediaOptionsSignature(task.Options));

    private static string MediaOptionsSignature(IReadOnlyDictionary<string, string> options) =>
        JsonSerializer.Serialize(MediaConversionOptions.OptionNames.Order(StringComparer.Ordinal)
            .Select(key => new KeyValuePair<string, string?>(key, options.TryGetValue(key, out var value) ? value : null))
            .ToArray());

    private sealed record Reservation(Guid TaskId, string TargetFormatId, string ConversionMode, int SelectedAudioTrack,
        string EngineId, string FinalPath, string TemporaryPath, bool IsDirectory, Guid? OutputGroupId,
        string OutputLabel, string MediaOptionsSignature);
    private sealed record FileFingerprint(string Path, bool IsDirectory, long Length, string Sha256);
    private sealed record Receipt(Reservation Reservation, FileFingerprint[] Files, long CreationTimeUtcTicks)
    {
        public string? CompanionTemporaryPath { get; init; }
        public string? CompanionFinalPath { get; init; }
        public FileFingerprint[] CompanionFiles { get; init; } = [];
        public long CompanionCreationTimeUtcTicks { get; init; }
    }
}
