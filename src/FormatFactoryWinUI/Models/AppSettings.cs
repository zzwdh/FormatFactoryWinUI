namespace FormatFactoryWinUI.Models;

public sealed class AppSettings
{
    public string FfmpegPath { get; set; } = string.Empty;
    public string OutputMode { get; set; } = "自定义目录";
    public string CustomOutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "格式工厂 WinUI");
    public int ParallelCount { get; set; } = 2;
    public int CpuThreadsPerTask { get; set; }
    public bool HardwareAcceleration { get; set; } = true;
    public int PerformanceDefaultsVersion { get; set; }
    public bool Notifications { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    public bool AlwaysMinimizeToTray { get; set; }
    public string Theme { get; set; } = "跟随系统";
}
