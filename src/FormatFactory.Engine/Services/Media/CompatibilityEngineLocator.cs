namespace FormatFactoryWinUI.Services;

public static class CompatibilityEngineLocator
{
    public static string? Find(string? configuredPath) => Find(configuredPath, AppContext.BaseDirectory);

    internal static string? Find(string? configuredPath, string baseDirectory)
    {
        // A configured path takes priority and is validated by preflight before use.
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath.Trim();
        var executable = Path.Combine(baseDirectory, "Engines", "ffmpeg-compatibility", "ffmpeg.exe");
        return File.Exists(executable) && !PortableExecutableInspector.RequiresUnavailableX64Support(executable) &&
            File.Exists(FfmpegLocator.GetFfprobePath(executable)) ? executable : null;
    }
}
