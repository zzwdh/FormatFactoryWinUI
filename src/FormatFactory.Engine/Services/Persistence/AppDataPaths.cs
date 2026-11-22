namespace FormatFactoryWinUI.Services;

/// <summary>
/// Process-wide storage location selected by the host before services are created.
/// Resolving a path alone never creates directories or starts logging.
/// </summary>
public static class AppDataPaths
{
    private static readonly object Gate = new();
    private static string? _root;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FormatFactoryWinUI");

    public static string Root
    {
        get
        {
            lock (Gate)
            {
                return _root ??= Path.GetFullPath(DefaultRoot);
            }
        }
    }

    public static void Configure(string root, bool migrateLegacyData = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (Gate)
        {
            if (_root is not null)
            {
                if (!string.Equals(_root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The application data path must be configured before creating services or accessing application data.");
                }
                return;
            }

            _root = normalizedRoot;
            if (migrateLegacyData)
            {
                MigrateLegacyData(normalizedRoot);
            }
        }
    }

    private static void MigrateLegacyData(string root)
    {
        try
        {
            LegacyAppDataMigration.Run(DefaultRoot, root);
        }
        catch (Exception ex)
        {
            // Migration failure must not prevent the packaged app from starting.
            System.Diagnostics.Debug.WriteLine($"Legacy app data migration failed: {ex.Message}");
        }
    }
}
