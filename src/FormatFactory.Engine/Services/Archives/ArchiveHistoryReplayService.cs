using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Archive history retains conversion choices, never archive passwords.</summary>
public static class ArchiveHistoryReplayService
{
    public const string StrictVerificationKey = "archive.strictVerification";
    public const string EncryptOutputKey = "archive.encryptOutput";

    public static bool IsArchive(ConversionHistoryItem item) =>
        item.ContentKind == ContentKind.Archive ||
        string.Equals(item.EngineId, "archive", StringComparison.OrdinalIgnoreCase);

    public static ArchiveFormat ParseFormat(string formatId) => formatId.ToLowerInvariant() switch
    {
        "zip" => ArchiveFormat.Zip,
        "7z" => ArchiveFormat.SevenZip,
        "tar" => ArchiveFormat.Tar,
        "tar.gz" => ArchiveFormat.TarGZip,
        "tar.xz" => ArchiveFormat.TarXz,
        _ => throw new InvalidDataException(LocalizationService.T("HistoryReplayTargetUnavailable", formatId))
    };

    public static (ArchiveConversionOptions Options, bool EncryptOutput) ReadOptions(ConversionHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var format = ParseFormat(item.TargetFormatId);
        bool ReadFlag(string key)
        {
            if (!item.Options.TryGetValue(key, out var value)) return false;
            if (bool.TryParse(value, out var flag)) return flag;
            throw new InvalidDataException(LocalizationService.T("ArchiveErrorInvalidOptions"));
        }
        var encrypt = ReadFlag(EncryptOutputKey);
        if (encrypt && !ArchiveFormats.SupportsEncryption(format))
            throw new InvalidDataException(LocalizationService.T("ArchiveEncryptionUnavailable"));
        return (new(format, ReadFlag(StrictVerificationKey)), encrypt);
    }

    public static string DescribeParameters(ConversionHistoryItem item)
    {
        try
        {
            var (options, encrypt) = ReadOptions(item);
            var labels = new List<string> { ArchiveFormats.DisplayName(options.TargetFormat) };
            if (options.StrictVerification) labels.Add(LocalizationService.T("ArchiveStrictVerification"));
            if (encrypt) labels.Add(LocalizationService.T("ArchiveEncryptOutput"));
            return string.Join(" · ", labels);
        }
        catch (InvalidDataException) { return item.TargetFormatId; }
    }
}
