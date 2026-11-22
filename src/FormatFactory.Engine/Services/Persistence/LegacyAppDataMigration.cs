using System.Text.Json;

namespace FormatFactoryWinUI.Services;

/// <summary>A completed migration is a one-time snapshot; failed copies remain retryable.</summary>
internal static class LegacyAppDataMigration
{
    internal const string MarkerName = ".legacy-migration-v1.json";
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    internal static (int Examined, int Copied) Run(string sourceRoot, string destinationRoot)
    {
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (sourceRoot.Equals(destinationRoot, PathComparison)) return default;
        var marker = Path.Combine(destinationRoot, MarkerName);
        if (IsComplete(marker, sourceRoot) || !Directory.Exists(sourceRoot)) return default;
        var sourcePrefix = Path.EndsInDirectorySeparator(sourceRoot) ? sourceRoot : sourceRoot + Path.DirectorySeparatorChar;
        if (destinationRoot.StartsWith(sourcePrefix, PathComparison))
            throw new ArgumentException("Migration destination must be outside the legacy directory.");

        Directory.CreateDirectory(destinationRoot);
        var examined = 0;
        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            examined++;
            var relative = Path.GetRelativePath(sourceRoot, source);
            if (relative.Equals(MarkerName, PathComparison)) continue;
            var destination = Path.Combine(destinationRoot, relative);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Never leave an interrupted copy at a final data path: a retry must not skip partial bytes.
            var temporary = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                try { File.Move(temporary, destination); copied++; }
                catch (IOException) when (File.Exists(destination)) { } // A concurrent host completed it first.
            }
            finally { TryDelete(temporary); }
        }

        var markerTemporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(markerTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Completion(1, sourceRoot));
                stream.Flush(flushToDisk: true);
            }
            File.Move(markerTemporary, marker, overwrite: true);
        }
        finally { TryDelete(markerTemporary); }
        return (examined, copied);
    }

    private static bool IsComplete(string path, string sourceRoot)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 32 * 1024) return false;
            return JsonSerializer.Deserialize<Completion>(stream) is { Version: 1, Source: { } source } &&
                source.Equals(sourceRoot, PathComparison);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Completion(int Version, string Source);
}
