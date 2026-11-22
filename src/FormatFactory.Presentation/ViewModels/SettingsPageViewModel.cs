using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.ViewModels;

/// <summary>Owns the explicit-save session independently of the lifetime of XAML controls.</summary>
public sealed class SettingsPageViewModel
{
    public SettingsDraftSession? DraftSession { get; private set; }
    public bool IsInitialized { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsSaving { get; set; }
    public string SaveStateKey { get; set; } = "ConversionUiSettingsSaved";
    public string? SaveMessage { get; set; }
    public string WatchFolderPath { get; set; } = string.Empty;
    public string? WatchFolderAccessToken { get; set; }

    public void InitializeDraft(AppSettings settings)
    {
        if (DraftSession is not null) return;
        ResetDraft(settings);
    }

    public void ResetDraft(AppSettings settings)
    {
        WatchFolderPath = settings.WatchFolderPath;
        WatchFolderAccessToken = settings.WatchFolderAccessToken;
        DraftSession = new(SettingsDraft.From(settings));
        SaveMessage = null;
        SaveStateKey = "ConversionUiSettingsSaved";
    }

    public void Edit(SettingsDraft draft)
    {
        if (DraftSession is null) return;
        DraftSession.Edit(draft);
        if (!IsSaving)
            SaveStateKey = DraftSession.IsDirty ? "ConversionUiSettingsUnsaved" : "ConversionUiSettingsSaved";
    }
}
