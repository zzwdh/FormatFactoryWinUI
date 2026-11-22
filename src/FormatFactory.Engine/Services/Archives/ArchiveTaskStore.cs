using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Atomic, strict snapshots preserve archive work without ever storing credentials.</summary>
public sealed class ArchiveTaskStore : IArchiveTaskPersistence
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ErrorKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "ArchiveErrorEngineUnavailable", "ArchiveErrorUnsupportedInput", "ArchiveErrorMissingVolume",
        "ArchiveErrorPasswordRequired", "ArchiveErrorPasswordSkipped", "ArchiveErrorCorruptArchive",
        "ArchiveErrorUnsafeEntry", "ArchiveErrorInsufficientSpace", "ArchiveErrorValidationFailed",
        "ArchiveErrorEngineFailed", "ArchiveErrorInvalidOptions", "ArchiveErrorOutputUnavailable"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate;

    public ArchiveTaskStore() : this(Path.Combine(LogService.AppDataRoot, "archive-tasks.json")) { }

    public ArchiveTaskStore(string path)
    {
        _path = Path.GetFullPath(path);
        _gate = Gates.GetOrAdd(_path, _ => new(1, 1));
    }

    public async Task<ArchiveTaskSnapshot> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await LoadCoreAsync(token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ArchiveTaskSnapshot snapshot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Freeze caller-owned lists before the first asynchronous boundary.
        Validate(snapshot);
        snapshot = snapshot with { Tasks = snapshot.Tasks.ToArray() };
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A corrupt or future-version file must remain available for recovery.
            _ = await LoadCoreAsync(token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        finally { _gate.Release(); }
    }

    private async Task<ArchiveTaskSnapshot> LoadCoreAsync(CancellationToken token)
    {
        FileStream stream;
        try { stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        await using (stream)
        {
            ArchiveTaskSnapshot snapshot;
            try
            {
                snapshot = await JsonSerializer.DeserializeAsync<ArchiveTaskSnapshot>(stream, JsonOptions, token)
                    .ConfigureAwait(false) ?? throw InvalidSnapshot();
            }
            catch (JsonException exception) { throw new InvalidDataException("Invalid archive task snapshot.", exception); }
            Validate(snapshot);
            return snapshot;
        }
    }

    private static void Validate(ArchiveTaskSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Tasks is null || snapshot.OutputDirectory is null ||
            snapshot.Tasks.Count > 10000 || !Enum.IsDefined(snapshot.DefaultTargetFormat) ||
            snapshot.DefaultEncryptOutput && !ArchiveFormats.SupportsEncryption(snapshot.DefaultTargetFormat))
            throw InvalidSnapshot();
        var ids = new HashSet<Guid>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in snapshot.Tasks)
        {
            if (task is null || task.Id == Guid.Empty || !ids.Add(task.Id) ||
                string.IsNullOrWhiteSpace(task.SourcePath) || !Path.IsPathFullyQualified(task.SourcePath) ||
                !sources.Add(task.SourcePath) ||
                task.OutputDirectory is null || task.OutputPath is null || !Enum.IsDefined(task.TargetFormat) ||
                !Enum.IsDefined(task.State) || task.EncryptOutput && !ArchiveFormats.SupportsEncryption(task.TargetFormat) ||
                task.ErrorKey is not null && !ErrorKeys.Contains(task.ErrorKey) ||
                task.PreviousErrorKey is not null && !ErrorKeys.Contains(task.PreviousErrorKey) ||
                task.ResultStatistics is { ElapsedMilliseconds: < 0 } ||
                task.ResultStatistics?.InputBytes is < 0 || task.ResultStatistics?.OutputBytes is < 0 ||
                task.HistoryRecorded && task.State != ArchiveTaskState.Succeeded ||
                task.State == ArchiveTaskState.Succeeded && (string.IsNullOrWhiteSpace(task.OutputPath) ||
                    !Path.IsPathFullyQualified(task.OutputPath) || task.CompletedAtUtc is null))
                throw InvalidSnapshot();
        }
    }

    private static InvalidDataException InvalidSnapshot() => new("Invalid or unsupported archive task snapshot.");
}
