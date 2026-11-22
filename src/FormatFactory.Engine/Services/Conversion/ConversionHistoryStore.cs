using System.Text.Json;
using System.Text.Json.Serialization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed record ConversionHistoryItem
{
    public Guid TaskId { get; set; }
    public IReadOnlyList<string> InputPaths { get; set; } = [];
    public string OutputPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ContentKind ContentKind { get; set; }
    public string SourceFormatId { get; set; } = string.Empty;
    public string TargetFormatId { get; set; } = string.Empty;
    public string EngineId { get; set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; set; }
    public ConversionResultStatistics? ResultStatistics { get; set; }
    public int SnapshotVersion { get; set; }
    public string ConversionMode { get; set; } = FormatCatalog.DefaultConversionMode;
    public int ParametersVersion { get; set; } = 1;
    public int SelectedAudioTrack { get; set; }
    public string? SourceRoot { get; set; }
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HistoryConversionSettings? SettingsSnapshot { get; set; }

    public static ConversionHistoryItem FromTask(MediaTask task) => new()
    {
        TaskId = task.Id,
        InputPaths = task.InputPaths.ToArray(),
        OutputPath = task.OutputPath,
        DisplayName = Path.GetFileName(task.OutputPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)),
        ContentKind = task.ContentKind,
        SourceFormatId = task.SourceFormatId,
        TargetFormatId = task.TargetFormatId,
        EngineId = task.EngineId,
        SnapshotVersion = 1,
        ConversionMode = task.ConversionMode,
        ParametersVersion = task.ParametersVersion,
        SelectedAudioTrack = task.SelectedAudioTrack,
        SourceRoot = task.SourceRoot,
        Options = HistoryReplayService.CopySafeOptions(task),
        SettingsSnapshot = HistoryReplayService.ReadCapturedSettings(task),
        ResultStatistics = task.ResultStatistics,
        CompletedAtUtc = DateTimeOffset.UtcNow
    };
}

public sealed class ConversionHistoryStore
{
    private const int MaximumItems = 1000;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;
    private HistorySnapshot? _cache;

    public ConversionHistoryStore()
        : this(Path.Combine(LogService.AppDataRoot, "conversion-history.json"))
    {
    }

    internal ConversionHistoryStore(string path) => _path = Path.GetFullPath(path);

    public async Task<IReadOnlyList<ConversionHistoryItem>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return CloneItems(await LoadCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _cache = null;
            // Reading history must not break navigation or search. Writes use the
            // strict loader below so unreadable history is never replaced with an empty list.
            try
            {
                LogService.WriteApp(LocalizationService.T("FileReadFailed", _path, ex.Message));
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(logError);
            }
            return [];
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<ConversionHistoryItem?> RecordCompletedAsync(
        MediaTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return RecordCompletedAsync(ConversionHistoryItem.FromTask(task), cancellationToken);
    }

    public async Task<ConversionHistoryItem?> RecordCompletedAsync(
        ConversionHistoryItem completedItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedItem);
        var item = CloneItem(completedItem);
        if (string.IsNullOrWhiteSpace(item.OutputPath) ||
            (!File.Exists(item.OutputPath) && !Directory.Exists(item.OutputPath)))
        {
            return null;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Always read the actual file before replacing it, even with a warm
            // cache: unreadable/corrupt history must never be overwritten.
            var items = (await LoadCoreAsync(cancellationToken, allowCache: false).ConfigureAwait(false)).ToList();
            items.RemoveAll(existing =>
                existing.TaskId == item.TaskId ||
                existing.OutputPath.Equals(item.OutputPath, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, item);
            if (items.Count > MaximumItems)
            {
                items.RemoveRange(MaximumItems, items.Count - MaximumItems);
            }
            await SaveCoreAsync(items, cancellationToken);
            return item;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversionHistoryItem>> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        var items = await LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
        {
            return items;
        }

        var value = query.Trim();
        return items.Where(item =>
                item.DisplayName.Contains(value, StringComparison.CurrentCultureIgnoreCase) ||
                item.OutputPath.Contains(value, StringComparison.CurrentCultureIgnoreCase) ||
                item.InputPaths.Any(path => path.Contains(value, StringComparison.CurrentCultureIgnoreCase)) ||
                item.TargetFormatId.Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private async Task<IReadOnlyList<ConversionHistoryItem>> LoadCoreAsync(
        CancellationToken token,
        bool allowCache = true)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(_path))
        {
            _cache = null;
            return [];
        }
        // Open even on cache hits so sharing/access failures keep their original
        // read-safe, write-strict behavior. No JSON is parsed for an unchanged file.
        await using var stream = File.OpenRead(_path);
        var stamp = ReadStamp(_path);
        if (allowCache && _cache is { } cached && cached.Stamp == stamp)
        {
            return cached.Items;
        }
        var items = await JsonSerializer.DeserializeAsync<ConversionHistoryItem[]>(stream, JsonOptions, token)
            ?? throw new InvalidDataException("Conversion history must contain an array of records.");
        if (items.Any(item => item is null ||
                item.InputPaths is null || item.InputPaths.Any(path => path is null) ||
                item.OutputPath is null || item.DisplayName is null || item.TargetFormatId is null ||
                item.SourceFormatId is null || item.EngineId is null || item.ConversionMode is null ||
                item.Options is null || item.Options.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) ||
                item.Options.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Options.Count ||
                item.ResultStatistics is { } statistics &&
                    (statistics.InputBytes < 0 || statistics.OutputBytes < 0 || statistics.ElapsedMilliseconds < 0) ||
                item.SettingsSnapshot is { } settings && (settings.EbookLayoutMode is null || settings.OcrLanguage is null ||
                    settings.OutputMode is null || settings.CustomOutputDirectory is null || settings.OutputFileNameTemplate is null)))
        {
            throw new InvalidDataException("Conversion history contains an invalid record.");
        }
        items = CloneItems(items);
        _cache = new HistorySnapshot(stamp, items);
        return items;
    }

    private static ConversionHistoryItem CloneItem(ConversionHistoryItem item) =>
        item with
        {
            InputPaths = item.InputPaths.ToArray(),
            Options = HistoryReplayService.CopySafeOptions(item.Options),
            SettingsSnapshot = item.SettingsSnapshot is { } settings ? settings with { } : null
        };

    private static ConversionHistoryItem[] CloneItems(IEnumerable<ConversionHistoryItem> items) =>
        items.Select(CloneItem).ToArray();

    private static FileStamp ReadStamp(string path)
    {
        var file = new FileInfo(path);
        return new FileStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
    }

    private readonly record struct FileStamp(long Length, DateTime ModifiedUtc, DateTime CreatedUtc);
    private sealed record HistorySnapshot(FileStamp Stamp, ConversionHistoryItem[] Items);

    private async Task SaveCoreAsync(
        IReadOnlyCollection<ConversionHistoryItem> items,
        CancellationToken token)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PersistenceFailureDiagnostics.Record(error, DiagnosticStore.History, DiagnosticPersistenceStage.CreateDirectory);
            throw;
        }
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        var diagnosticStage = DiagnosticPersistenceStage.WriteSnapshot;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, items, JsonOptions, token);
                await stream.FlushAsync(token);
            }
            // The temporary file's stamp describes precisely the content written
            // here, even if another process replaces the destination immediately.
            var stamp = ReadStamp(temporaryPath);
            diagnosticStage = DiagnosticPersistenceStage.CommitSnapshot;
            File.Move(temporaryPath, _path, true);
            _cache = new HistorySnapshot(stamp, CloneItems(items));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PersistenceFailureDiagnostics.Record(error, DiagnosticStore.History, diagnosticStage, items: items.Count);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
