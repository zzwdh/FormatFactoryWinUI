using System.IO.Enumeration;

namespace FormatFactoryWinUI.Models;

/// <summary>Shared by automation and ordinary imports, so the same file has one owner.</summary>
public static class AutomationRuleMatcher
{
    public static bool Matches(AutomationRule rule, string path)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!rule.Enabled || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rule.FolderPath)) return false;
        var format = FormatCatalog.FindByExtension(path);
        if (format is null || !format.CanImport || !IsWithin(path, rule.FolderPath)) return false;
        if (!rule.IncludeSubfolders && !Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.FolderPath)), StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.InputKind is { } kind && format.ContentKind != kind) return false;
        if (!string.IsNullOrWhiteSpace(rule.Extensions))
        {
            var extension = Path.GetExtension(path).TrimStart('.');
            if (!rule.Extensions.Split([',', ';', '，', '；'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(value => value.Trim().TrimStart('.').Equals(extension, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (!string.IsNullOrWhiteSpace(rule.FileNamePattern) && !FileSystemName.MatchesSimpleExpression(
                rule.FileNamePattern.Trim(), Path.GetFileName(path), ignoreCase: true)) return false;
        return !IsExcludedByDestinations(path, [rule]);
    }

    public static AutomationRule? FirstMatch(IEnumerable<AutomationRule> rules, string path)
    {
        var ordered = rules as IReadOnlyList<AutomationRule> ?? rules.ToArray();
        return IsExcludedByDestinations(path, ordered) ? null : ordered.FirstOrDefault(rule => Matches(rule, path));
    }

    public static bool IsExcludedByDestinations(string path, IEnumerable<AutomationRule> rules) =>
        rules.Any(rule =>
            (!string.IsNullOrWhiteSpace(rule.OutputDirectory) && IsWithin(path, rule.OutputDirectory)) ||
            (rule.ArchiveSource && !string.IsNullOrWhiteSpace(rule.ArchiveDirectory) && IsWithin(path, rule.ArchiveDirectory)));

    private static bool IsWithin(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}
