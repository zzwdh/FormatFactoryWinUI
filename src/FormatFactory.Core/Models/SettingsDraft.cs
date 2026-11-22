namespace FormatFactoryWinUI.Models;

// Only explicit-save fields belong to the draft. Theme, language, output choices,
// diagnostic preferences and hardware test metadata are committed independently.
public sealed record SettingsDraft
{
    public string FfmpegPath { get; init; } = string.Empty;
    public string CompatibilityFfmpegPath { get; init; } = string.Empty;
    public bool RepairDocxOnImport { get; init; }
    public string LibreOfficePath { get; init; } = string.Empty;
    public string CalibreEbookConvertPath { get; init; } = string.Empty;
    public string Caj2PdfPath { get; init; } = string.Empty;
    public string MakeMkvPath { get; init; } = string.Empty;
    public string AudioCdRipperPath { get; init; } = string.Empty;
    public string ReadomPath { get; init; } = string.Empty;
    public string EbookLayoutMode { get; init; } = "reflow";
    public string OcrLanguage { get; init; } = "auto";
    public int ParallelCount { get; init; } = 2;
    public int CpuThreadsPerTask { get; init; }
    public bool HardwareAcceleration { get; init; } = true;
    public bool Notifications { get; init; } = true;
    public bool NotificationSound { get; init; } = true;
    public bool AlwaysMinimizeToTray { get; init; }
    public bool StartWithWindows { get; init; }
    public bool StartInTray { get; init; } = true;
    public bool WatchFolderEnabled { get; init; }
    public string WatchFolderPath { get; init; } = string.Empty;
    public string? WatchFolderAccessToken { get; init; }

    public static SettingsDraft From(AppSettings settings) => new()
    {
        FfmpegPath = settings.FfmpegPath,
        CompatibilityFfmpegPath = settings.CompatibilityFfmpegPath,
        RepairDocxOnImport = settings.RepairDocxOnImport,
        LibreOfficePath = settings.LibreOfficePath,
        CalibreEbookConvertPath = settings.CalibreEbookConvertPath,
        Caj2PdfPath = settings.Caj2PdfPath,
        MakeMkvPath = settings.MakeMkvPath,
        AudioCdRipperPath = settings.AudioCdRipperPath,
        ReadomPath = settings.ReadomPath,
        EbookLayoutMode = settings.EbookLayoutMode,
        OcrLanguage = settings.OcrLanguage,
        ParallelCount = settings.ParallelCount,
        CpuThreadsPerTask = settings.CpuThreadsPerTask,
        HardwareAcceleration = settings.HardwareAcceleration,
        Notifications = settings.Notifications,
        NotificationSound = settings.NotificationSound,
        AlwaysMinimizeToTray = settings.AlwaysMinimizeToTray,
        StartWithWindows = settings.StartWithWindows,
        StartInTray = settings.StartInTray,
        WatchFolderEnabled = settings.WatchFolderEnabled,
        WatchFolderPath = settings.WatchFolderPath,
        WatchFolderAccessToken = settings.WatchFolderAccessToken
    };

    public SettingsDraft NormalizeToolPaths(Func<string, string> resolveFfmpegPath) => this with
    {
        FfmpegPath = AbsoluteToolPath(FfmpegPath, resolveFfmpegPath),
        CompatibilityFfmpegPath = AbsoluteToolPath(CompatibilityFfmpegPath, resolveFfmpegPath),
        LibreOfficePath = AbsoluteToolPath(LibreOfficePath),
        CalibreEbookConvertPath = AbsoluteToolPath(CalibreEbookConvertPath),
        Caj2PdfPath = AbsoluteToolPath(Caj2PdfPath),
        MakeMkvPath = AbsoluteToolPath(MakeMkvPath),
        AudioCdRipperPath = AbsoluteToolPath(AudioCdRipperPath),
        ReadomPath = AbsoluteToolPath(ReadomPath)
    };

    private static string AbsoluteToolPath(string path, Func<string, string>? resolvePath = null) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty
            : Path.GetFullPath(resolvePath is null ? path.Trim() : resolvePath(path.Trim()));

    public void ApplyTo(AppSettings settings)
    {
        settings.FfmpegPath = FfmpegPath;
        settings.CompatibilityFfmpegPath = CompatibilityFfmpegPath;
        settings.RepairDocxOnImport = RepairDocxOnImport;
        settings.LibreOfficePath = LibreOfficePath;
        settings.CalibreEbookConvertPath = CalibreEbookConvertPath;
        settings.Caj2PdfPath = Caj2PdfPath;
        settings.MakeMkvPath = MakeMkvPath;
        settings.AudioCdRipperPath = AudioCdRipperPath;
        settings.ReadomPath = ReadomPath;
        settings.EbookLayoutMode = EbookLayoutMode;
        settings.OcrLanguage = OcrLanguage;
        settings.ParallelCount = ParallelCount;
        settings.CpuThreadsPerTask = CpuThreadsPerTask;
        settings.HardwareAcceleration = HardwareAcceleration;
        settings.Notifications = Notifications;
        settings.NotificationSound = NotificationSound;
        settings.AlwaysMinimizeToTray = AlwaysMinimizeToTray;
        settings.StartWithWindows = StartWithWindows;
        settings.StartInTray = StartInTray;
        settings.WatchFolderEnabled = WatchFolderEnabled;
        settings.WatchFolderPath = WatchFolderPath;
        settings.WatchFolderAccessToken = WatchFolderAccessToken;
    }
}

public sealed class SettingsDraftSession(SettingsDraft baseline)
{
    public SettingsDraft Baseline { get; private set; } = baseline;
    public SettingsDraft Current { get; private set; } = baseline;
    public bool IsDirty => Current != Baseline;
    public void Edit(SettingsDraft draft) => Current = draft;
    // Saving an earlier snapshot must not discard edits made while it was saving.
    public void AcceptSaved(SettingsDraft saved) => Baseline = saved;
}
