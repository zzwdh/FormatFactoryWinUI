using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class QueueStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(LogService.AppDataRoot, "pending-queue.json");

    public async Task SaveAsync(IEnumerable<MediaTask> tasks)
    {
        var items = tasks
            .Where(task => task.State is ConversionState.Waiting or ConversionState.Probing or ConversionState.Running or ConversionState.Paused)
            .Select(task => new PersistedQueueItem
            {
                InputPath = task.InputPath,
                SourceRoot = task.SourceRoot,
                TargetFormat = task.TargetFormat,
                ConversionMode = task.ConversionMode,
                SelectedAudioTrack = task.SelectedAudioTrack
            })
            .ToArray();

        Directory.CreateDirectory(LogService.AppDataRoot);
        if (items.Length == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            return;
        }

        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, items, Options);
    }

    public async Task<IReadOnlyList<PersistedQueueItem>> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<PersistedQueueItem[]>(stream, Options) ?? [];
        }
        catch (Exception ex)
        {
            LogService.WriteApp($"恢复队列失败: {ex.Message}");
            return [];
        }
    }
}

public sealed class PersistedQueueItem
{
    public string InputPath { get; set; } = string.Empty;
    public string? SourceRoot { get; set; }
    public string TargetFormat { get; set; } = string.Empty;
    public string ConversionMode { get; set; } = "平衡";
    public int SelectedAudioTrack { get; set; }
}
