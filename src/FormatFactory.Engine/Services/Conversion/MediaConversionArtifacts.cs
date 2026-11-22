namespace FormatFactoryWinUI.Services;

/// <summary>Checks and removes only the private artifacts owned by a conversion attempt.</summary>
internal static class MediaConversionArtifacts
{
    internal static bool HasOutput(string path, bool isDirectory) => isDirectory
        ? Directory.Exists(path) && Directory.EnumerateFiles(path, "frame-*.png", SearchOption.TopDirectoryOnly).Any()
        : File.Exists(path);

    internal static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 后续日志会记录主错误，清理失败不覆盖它。
        }
    }
}
