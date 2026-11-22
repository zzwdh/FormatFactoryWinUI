using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Reads the persisted file-version identity shared by conversion and automation.</summary>
public static class FileFingerprintService
{
    public static AutomationFileFingerprint? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists ? new AutomationFileFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}
