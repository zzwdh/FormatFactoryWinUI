using System.Text.RegularExpressions;

namespace FormatFactoryWinUI.Services;

/// <summary>Identifies one logical archive even when several of its volumes were selected.</summary>
public static partial class ArchiveInput
{
    [GeneratedRegex(@"^(?<stem>.+)\.part(?<number>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartRar();
    [GeneratedRegex(@"^(?<stem>.+\.(?:7z|zip))\.(?<number>\d{3,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedVolume();
    [GeneratedRegex(@"^(?<stem>.+)\.r\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldRar();
    [GeneratedRegex(@"^(?<stem>.+)\.z(?<number>\d{2,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitZip();

    public static bool IsSupported(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".txz", StringComparison.OrdinalIgnoreCase) ||
            NumberedVolume().IsMatch(name) || OldRar().IsMatch(name) || SplitZip().IsMatch(name);
    }

    public static string GetPrimaryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        var part = PartRar().Match(name);
        if (part.Success)
        {
            var stem = part.Groups["stem"].Value;
            var width = part.Groups["number"].Length;
            var primary = Path.Combine(directory, stem + ".part" + "1".PadLeft(width, '0') + ".rar");
            if (File.Exists(primary)) return primary;
            if (Directory.Exists(directory))
            {
                var found = Directory.EnumerateFiles(directory).FirstOrDefault(candidate =>
                {
                    var match = PartRar().Match(Path.GetFileName(candidate));
                    return match.Success && string.Equals(match.Groups["stem"].Value, stem, StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(match.Groups["number"].Value, out var number) && number == 1;
                });
                if (found is not null) return found;
            }
            return primary;
        }
        var volume = NumberedVolume().Match(name);
        if (volume.Success)
        {
            var stem = volume.Groups["stem"].Value;
            var standard = Path.Combine(directory, stem + ".001");
            if (File.Exists(standard)) return standard;
            var sameWidth = Path.Combine(directory, stem + "." + "1".PadLeft(volume.Groups["number"].Length, '0'));
            if (File.Exists(sameWidth)) return sameWidth;
            if (Directory.Exists(directory))
            {
                var found = Directory.EnumerateFiles(directory).FirstOrDefault(candidate =>
                {
                    var match = NumberedVolume().Match(Path.GetFileName(candidate));
                    return match.Success && string.Equals(match.Groups["stem"].Value, stem, StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(match.Groups["number"].Value, out var number) && number == 1;
                });
                if (found is not null) return found;
            }
            return standard;
        }
        var old = OldRar().Match(name);
        if (old.Success) return Path.Combine(directory, old.Groups["stem"].Value + ".rar");
        var splitZip = SplitZip().Match(name);
        // ZIP's central directory is stored in the terminal .zip volume. 7-Zip
        // starts there and reads .z01, .z02, ... from the same directory.
        return splitZip.Success ? Path.Combine(directory, splitZip.Groups["stem"].Value + ".zip") : full;
    }

    public static IReadOnlyList<string> NormalizeInputs(IEnumerable<string> paths) =>
        paths.Select(GetPrimaryPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static void ValidateVolumes(string primary)
    {
        if (!File.Exists(primary))
            throw new ArchiveConversionException(ArchiveFailureCode.MissingVolume, "The archive or its first volume is missing.");
        var name = Path.GetFileName(primary);
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var zipStem = name[..^4];
            var zipNumbers = Directory.EnumerateFiles(Path.GetDirectoryName(primary)!)
                .Select(path => SplitZip().Match(Path.GetFileName(path)))
                .Where(match => match.Success && string.Equals(match.Groups["stem"].Value, zipStem, StringComparison.OrdinalIgnoreCase))
                .Select(match => long.TryParse(match.Groups["number"].Value, out var number) ? number : -1)
                .Order().ToArray();
            if (zipNumbers.Where((number, index) => number != index + 1L).Any())
                throw new ArchiveConversionException(ArchiveFailureCode.MissingVolume, "One or more ZIP archive volumes are missing.");
        }
        var part = PartRar().Match(name);
        var numbered = NumberedVolume().Match(name);
        if (!part.Success && !numbered.Success) return;
        var matcher = part.Success ? PartRar() : NumberedVolume();
        var stem = (part.Success ? part : numbered).Groups["stem"].Value;
        var numbers = Directory.EnumerateFiles(Path.GetDirectoryName(primary)!).Select(path => matcher.Match(Path.GetFileName(path)))
            .Where(match => match.Success && string.Equals(match.Groups["stem"].Value, stem, StringComparison.OrdinalIgnoreCase))
            .Select(match => long.TryParse(match.Groups["number"].Value, out var number) ? number : -1).Order().ToArray();
        if (numbers.Length == 0 || numbers[0] != 1 || numbers.Where((number, index) => number != index + 1L).Any())
            throw new ArchiveConversionException(ArchiveFailureCode.MissingVolume, "One or more archive volumes are missing.");
    }

    internal static bool IsCompressedTar(string path) => new[] { ".tar.gz", ".tar.xz", ".tgz", ".txz" }
        .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    internal static string OutputStem(string primary)
    {
        var name = Path.GetFileName(primary);
        var part = PartRar().Match(name);
        if (part.Success) return part.Groups["stem"].Value;
        var numbered = NumberedVolume().Match(name);
        if (numbered.Success) name = numbered.Groups["stem"].Value;
        foreach (var extension in new[] { ".tar.gz", ".tar.xz", ".tgz", ".txz", ".zip", ".7z", ".rar", ".tar" })
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return name[..^extension.Length];
        return Path.GetFileNameWithoutExtension(name);
    }
}
