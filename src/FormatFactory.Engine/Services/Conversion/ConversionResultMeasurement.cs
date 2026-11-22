using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Best-effort measurements run by the workflow off the UI thread.</summary>
internal static class ConversionResultMeasurement
{
    internal static long? MeasureInputs(IReadOnlyList<ConversionInput> inputs) =>
        Measure(inputs.Select(input => input.Path), includeDirectories: false);

    internal static long? MeasureOutputs(IReadOnlyList<ConversionArtifact> artifacts, ConversionRequest request)
    {
        var committed = artifacts.Where(artifact => !artifact.IsTemporary).ToArray();
        // The document engine reports the .md file but not its sibling resources.
        // An incomplete artifact list cannot establish the total output size.
        if (committed.Length == 1 && committed[0].FormatId == "md") return null;
        var sharedDirectories = request.Inputs.Select(input => Path.GetDirectoryName(input.Path))
            .Append(request.OutputDirectory).Append(request.SourceRoot)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!);
        return Measure(committed.Select(artifact => artifact.Path), includeDirectories: true,
            artifacts.Where(artifact => artifact.IsTemporary).Select(artifact => artifact.Path), sharedDirectories);
    }

    private static long? Measure(IEnumerable<string> paths, bool includeDirectories,
        IEnumerable<string>? excludedPaths = null, IEnumerable<string>? sharedDirectories = null)
    {
        var started = Stopwatch.GetTimestamp();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(paths);
        if (pending.Count == 0) return null;
        long total = 0;
        try
        {
            var excluded = (excludedPaths ?? []).Select(Path.GetFullPath).ToArray();
            var shared = (sharedDirectories ?? []).Select(Path.GetFullPath).ToArray();
            while (pending.TryPop(out var path))
            {
                // A summary must not delay a completed batch indefinitely on a large
                // output tree. An incomplete measurement is reported as unavailable.
                if (visited.Count >= 100_000 || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2)) return null;
                var fullPath = Path.GetFullPath(path);
                if (excluded.Any(excludedPath => IsSameOrAncestor(excludedPath, fullPath))) continue;
                if (!visited.Add(fullPath)) continue;
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // A source volume or disc's capacity is not the size of its selected content.
                    if (!includeDirectories) return null;
                    // Directory artifacts must be dedicated results, not a shared
                    // source/destination folder or a volume containing unrelated files.
                    if (string.Equals(Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                        shared.Any(sharedPath => IsSameOrAncestor(fullPath, sharedPath))) return null;
                    foreach (var child in Directory.EnumerateFileSystemEntries(fullPath))
                    {
                        if (pending.Count + visited.Count >= 100_000 || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2)) return null;
                        pending.Push(child);
                    }
                }
                else total = checked(total + new FileInfo(fullPath).Length);
            }
            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            // A missing or unreadable size must never turn a successful conversion into a failure.
            return null;
        }
    }

    private static bool IsSameOrAncestor(string ancestor, string path)
    {
        ancestor = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
