using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class OutputPathService
{
    private readonly object _syncRoot = new();
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);

    public (string FinalPath, string TemporaryPath) Reserve(MediaTask task, AppSettings settings)
    {
        var outputDirectory = ResolveOutputDirectory(task, settings);
        Directory.CreateDirectory(outputDirectory);

        var extension = FormatCatalog.ExtensionFor(task.TargetFormat);
        var baseName = Path.GetFileNameWithoutExtension(task.InputPath);
        var candidate = Path.Combine(outputDirectory, baseName + extension);

        lock (_syncRoot)
        {
            var suffix = 1;
            while (File.Exists(candidate) || _reservedPaths.Contains(candidate))
            {
                candidate = Path.Combine(outputDirectory, $"{baseName} ({suffix++}){extension}");
            }
            _reservedPaths.Add(candidate);
        }

        var temporaryName = $"{Path.GetFileNameWithoutExtension(candidate)}.{Guid.NewGuid():N}.partial{extension}";
        return (candidate, Path.Combine(outputDirectory, temporaryName));
    }

    private static string ResolveOutputDirectory(MediaTask task, AppSettings settings)
    {
        if (settings.OutputMode == "源文件目录")
        {
            return task.InputDirectory;
        }

        var root = string.IsNullOrWhiteSpace(settings.CustomOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "格式工厂 WinUI")
            : settings.CustomOutputDirectory;

        if (string.IsNullOrWhiteSpace(task.SourceRoot))
        {
            return root;
        }

        var relative = Path.GetRelativePath(task.SourceRoot, task.InputDirectory);
        return relative.StartsWith("..", StringComparison.Ordinal) ? root : Path.Combine(root, relative == "." ? string.Empty : relative);
    }
}
