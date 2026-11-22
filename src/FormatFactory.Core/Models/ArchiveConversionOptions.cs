namespace FormatFactoryWinUI.Models;

public enum ArchiveFormat { Zip, SevenZip, Tar, TarGZip, TarXz }

/// <summary>Passwords are task-local secrets. These options must never be serialized or logged.</summary>
public sealed record ArchiveConversionOptions(ArchiveFormat TargetFormat,
    bool StrictVerification = false, string? OutputPassword = null);

public sealed record ArchivePasswordRequest(string SourcePath, bool IncorrectPassword, int Attempt);

public sealed record ArchiveProgress(double Fraction, string Phase);

public static class ArchiveFormats
{
    public static IReadOnlyList<ArchiveFormat> All { get; } = Enum.GetValues<ArchiveFormat>();

    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ".zip", ArchiveFormat.SevenZip => ".7z", ArchiveFormat.Tar => ".tar",
        ArchiveFormat.TarGZip => ".tar.gz", ArchiveFormat.TarXz => ".tar.xz",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static string DisplayName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP", ArchiveFormat.SevenZip => "7Z", ArchiveFormat.Tar => "TAR",
        ArchiveFormat.TarGZip => "TAR.GZ", ArchiveFormat.TarXz => "TAR.XZ",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static bool SupportsEncryption(ArchiveFormat format) => format is ArchiveFormat.Zip or ArchiveFormat.SevenZip;

    public static bool IsValidOutputPassword(ArchiveFormat format, string? password) => string.IsNullOrEmpty(password) ||
        SupportsEncryption(format) && password.Length <= 1024 && !password.Any(char.IsControl) &&
        (format != ArchiveFormat.Zip || password.Length <= 99 && password.All(character => character is >= ' ' and <= '~'));
}
