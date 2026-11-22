using System.Reflection;

namespace FormatFactoryWinUI.Models;

public sealed class AppSettings
{
    public const int CurrentPerformanceDefaultsVersion = 2;

    private static readonly PropertyInfo[] StoredProperties = typeof(AppSettings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
        .ToArray();

    // All settings are value types or immutable strings; isolate a running operation from UI edits.
    public AppSettings Snapshot() => (AppSettings)MemberwiseClone();

    public static AppSettings CreateDefaultsPreservingFolders(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppSettings
        {
            PerformanceDefaultsVersion = CurrentPerformanceDefaultsVersion,
            WatchFolderPath = settings.WatchFolderPath,
            WatchFolderAccessToken = settings.WatchFolderAccessToken
        };
    }

    // Publish a complete persisted snapshot while retaining references held by running services.
    // Deriving the copy from the stored properties keeps future settings out of manual copy lists.
    public void CopyFrom(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var property in StoredProperties)
            property.SetValue(this, property.GetValue(settings));
    }

    public string FfmpegPath { get; set; } = string.Empty;
    public string CompatibilityFfmpegPath { get; set; } = string.Empty;
    public bool RepairDocxOnImport { get; set; }
    public string LibreOfficePath { get; set; } = string.Empty;
    public string CalibreEbookConvertPath { get; set; } = string.Empty;
    public string Caj2PdfPath { get; set; } = string.Empty;
    public string MakeMkvPath { get; set; } = string.Empty;
    public string AudioCdRipperPath { get; set; } = string.Empty;
    public string ReadomPath { get; set; } = string.Empty;
    public string EbookLayoutMode { get; set; } = "reflow";
    public string OcrLanguage { get; set; } = "auto";
    public string OutputMode { get; set; } = "custom";
    public string CustomOutputDirectory { get; set; } = OutputDirectoryDefaults.Resolve();
    public bool PreserveFolderStructure { get; set; } = true;
    public string OutputFileNameTemplate { get; set; } = "{name}";
    public int ParallelCount { get; set; } = 2;
    public int CpuThreadsPerTask { get; set; }
    public bool HardwareAcceleration { get; set; } = true;
    public bool HardwareDecodeTestCompleted { get; set; }
    public int PerformanceDefaultsVersion { get; set; }
    public bool Notifications { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    public bool EnableUsageTelemetry { get; set; } = true;
    public bool EnableAutomaticErrorReporting { get; set; } = true;
    public bool EnableAutomaticUpdates { get; set; } = true;
    public bool AlwaysMinimizeToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartInTray { get; set; } = true;
    public bool WatchFolderEnabled { get; set; }
    public string WatchFolderPath { get; set; } = string.Empty;
    public string? WatchFolderAccessToken { get; set; }
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "auto";
}
