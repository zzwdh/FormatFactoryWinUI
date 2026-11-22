using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Models;

internal static class OutputDirectoryDefaults
{
    internal const string FolderName = "FluentFormatConverter";
    private const string LegacyFolderName = "Format Factory WinUI";

    public static string Resolve() => Resolve(Environment.GetFolderPath);

    internal static string Resolve(Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> getFolderPath)
    {
        // The verified known-folder query returns an empty string when Videos has not
        // been created. Never turn that into a path inside the app's working directory.
        const Environment.SpecialFolderOption option = Environment.SpecialFolderOption.DoNotVerify;
        var videos = getFolderPath(Environment.SpecialFolder.MyVideos, option);
        if (Path.IsPathFullyQualified(videos)) return Path.Combine(videos, FolderName);

        var profile = getFolderPath(Environment.SpecialFolder.UserProfile, option);
        if (Path.IsPathFullyQualified(profile)) return Path.Combine(profile, "Videos", FolderName);

        var appData = getFolderPath(Environment.SpecialFolder.LocalApplicationData, option);
        if (Path.IsPathFullyQualified(appData)) return Path.Combine(appData, FolderName, "Output");

        throw new DirectoryNotFoundException(LocalizationService.T("ConversionUiOutputPathNeedsDirectory"));
    }

    public static string ResolveConfigured(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ||
        Path.TrimEndingDirectorySeparator(directory).Equals(FolderName, StringComparison.OrdinalIgnoreCase) ||
        Path.TrimEndingDirectorySeparator(directory).Equals(LegacyFolderName, StringComparison.OrdinalIgnoreCase)
            ? Resolve() : directory;
}
