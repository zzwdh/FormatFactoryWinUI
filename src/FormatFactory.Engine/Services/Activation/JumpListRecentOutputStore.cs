using System.Text.Json;

namespace FormatFactoryWinUI.Services;

internal sealed class JumpListRecentOutputStore
{
    private readonly string _path;

    public JumpListRecentOutputStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LoadAsync()
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(_path).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }

        var stored = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json)
            ?? throw new JsonException("The dismissed recent-output data must be a JSON object.");
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (arguments, dismissedAt) in stored) result[arguments] = dismissedAt;
        return result;
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, DateTimeOffset> dismissedOutputs)
    {
        ArgumentNullException.ThrowIfNull(dismissedOutputs);
        var snapshot = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (arguments, dismissedAt) in dismissedOutputs) snapshot[arguments] = dismissedAt;

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(snapshot)).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
