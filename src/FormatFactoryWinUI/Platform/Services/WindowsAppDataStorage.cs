using Windows.Storage;

namespace FormatFactoryWinUI.Services;

/// <summary>Selects the packaged or unpackaged data location for every desktop entry point.</summary>
internal static class WindowsAppDataStorage
{
    public static void Initialize()
    {
        string root;
        try
        {
            root = Path.Combine(ApplicationData.Current.LocalFolder.Path, "FormatFactoryWinUI");
        }
        catch
        {
            root = AppDataPaths.DefaultRoot;
        }

        LogService.ConfigureAppDataRoot(root, migrateLegacyData: true);
    }
}