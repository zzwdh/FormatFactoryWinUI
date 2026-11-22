using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Strict, atomic persistence for reusable conversion presets.</summary>
public sealed class AutomationPresetStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate;

    public AutomationPresetStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(LogService.AppDataRoot, "automation-presets.json"));
        _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<AutomationConversionPreset>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadCoreAsync(cancellationToken).ConfigureAwait(false))
                .Select(preset => preset.Snapshot()).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IEnumerable<AutomationConversionPreset> presets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presets);
        cancellationToken.ThrowIfCancellationRequested();
        var copies = presets.Select(preset =>
        {
            if (preset is null) throw InvalidDocument();
            preset.Validate();
            var copy = preset.Snapshot();
            copy.MigrateOutputs();
            return copy;
        }).ToArray();
        ValidatePresets(copies);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A save without a preceding load must not erase a corrupt or unreadable document.
            _ = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            await SaveCoreAsync(copies, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<AutomationConversionPreset>> LoadCoreAsync(CancellationToken token)
    {
        FileStream stream;
        try { stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        await using (stream)
        {
            PresetDocument document;
            try
            {
                document = await JsonSerializer.DeserializeAsync<PresetDocument>(stream, JsonOptions, token)
                    .ConfigureAwait(false) ?? throw InvalidDocument();
            }
            catch (JsonException exception) { throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"), exception); }
            if (document.Version is not (1 or 2) || document.Presets is null) throw InvalidDocument();
            ValidatePresets(document.Presets);
            if (document.Version == 2 && document.Presets.Any(preset => preset.Outputs.Count == 0)) throw InvalidDocument();
            foreach (var preset in document.Presets) preset.MigrateOutputs();
            return document.Presets;
        }
    }

    private static void ValidatePresets(IEnumerable<AutomationConversionPreset> presets)
    {
        var ids = new HashSet<Guid>();
        foreach (var preset in presets)
        {
            if (preset is null || !ids.Add(preset.Id)) throw InvalidDocument();
            preset.Validate();
        }
    }

    private async Task SaveCoreAsync(IReadOnlyList<AutomationConversionPreset> presets, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new PresetDocument { Presets = presets }, JsonOptions, token)
                    .ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static InvalidDataException InvalidDocument() => new(LocalizationService.T("AutomationValidationOptions"));

    private sealed record PresetDocument
    {
        [JsonRequired] public int Version { get; set; } = 2;
        [JsonRequired] public IReadOnlyList<AutomationConversionPreset> Presets { get; set; } = [];
    }
}
