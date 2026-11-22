using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>Explains an unsaved rule using read-only file access. Never reserves or creates output paths.</summary>
public static class AutomationPreviewService
{
    public static Task<AutomationPreviewResult> PreviewAsync(AutomationRule draft,
        IReadOnlyList<AutomationRule> savedRules, AutomationSnapshot snapshot, AppSettings settings,
        IReadOnlyCollection<string>? busyInputs = null, int limit = 500, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(savedRules);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        var rule = draft.Snapshot();
        rule.Validate();
        var rules = savedRules.Select(item => item.Id == rule.Id ? rule : item.Snapshot()).ToList();
        if (!rules.Any(item => item.Id == rule.Id)) rules.Add(rule);
        var records = snapshot.Records.ToArray();
        var suppressions = snapshot.Suppressions.ToArray();
        var capturedSettings = settings.Snapshot();
        capturedSettings.OutputMode = "custom";
        capturedSettings.CustomOutputDirectory = rule.OutputDirectory;
        if (rule.Options.TryGetValue("outputFileNameTemplate", out var template))
            capturedSettings.OutputFileNameTemplate = template;
        if (rule.Options.TryGetValue("preserveFolderStructure", out var preserve) && bool.TryParse(preserve, out var preserveValue))
            capturedSettings.PreserveFolderStructure = preserveValue;
        var busy = new HashSet<string>((busyInputs ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return Task.Run(() => Build(rule, rules, records, suppressions, capturedSettings, busy, limit, cancellationToken), cancellationToken);
    }

    private static AutomationPreviewResult Build(AutomationRule rule, IReadOnlyList<AutomationRule> rules,
        IReadOnlyList<AutomationProcessedFile> records, IReadOnlyList<AutomationSuppressedFile> suppressions,
        AppSettings settings, HashSet<string> busy, int limit, CancellationToken token)
    {
        var items = new List<AutomationPreviewItem>();
        var claimedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(rule.FolderPath))
                return new(items, false, LocalizationService.T("AutomationPreviewFolderUnavailable"));
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = rule.IncludeSubfolders, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false
            };
            foreach (var input in Directory.EnumerateFiles(rule.FolderPath, "*", enumeration))
            {
                token.ThrowIfCancellationRequested();
                if (items.Count >= limit) return new(items, true);
                var path = Path.GetFullPath(input);
                var fingerprint = FileFingerprintService.Read(path);
                var state = AutomationPreviewState.Ready;
                var reason = LocalizationService.T("AutomationPreviewReady");
                string? output = null;
                if (!rule.Enabled) Set(AutomationPreviewState.Disabled, "AutomationPreviewDisabled");
                else if (AutomationRuleMatcher.IsExcludedByDestinations(path, rules) || records.Any(record =>
                    (record.OutputPath is { } result && (Same(path, result) &&
                        FileFingerprintService.Read(path) == record.OutputFingerprint || Directory.Exists(result) && Within(path, result))) ||
                    record.Outputs?.Any(receipt => Same(path, receipt.OutputPath) && fingerprint == receipt.Fingerprint ||
                        Directory.Exists(receipt.OutputPath) && Within(path, receipt.OutputPath)) == true ||
                    record.ArchivedPath is { } archive && Same(path, archive) && fingerprint == record.Fingerprint))
                    Set(AutomationPreviewState.Excluded, "AutomationPreviewExcluded");
                else if (!AutomationRuleMatcher.Matches(rule, path))
                    Set(AutomationPreviewState.FilterMismatch, "AutomationPreviewFilterMismatch");
                else if (AutomationRuleMatcher.FirstMatch(rules, path) is { } owner && owner.Id != rule.Id)
                {
                    state = AutomationPreviewState.OwnedByOtherRule;
                    reason = LocalizationService.T("AutomationPreviewOwnedByOther", owner.Name);
                }
                else if (fingerprint is not null && records.Any(record => Same(record.InputPath, path) && record.Fingerprint == fingerprint))
                    Set(AutomationPreviewState.AlreadyProcessed, "AutomationPreviewAlreadyProcessed");
                else if (fingerprint is not null && suppressions.FirstOrDefault(record => Same(record.InputPath, path) && record.Fingerprint == fingerprint) is { } suppression)
                    Set(suppression.Cancelled ? AutomationPreviewState.Cancelled : AutomationPreviewState.FailedPreviously,
                        suppression.Cancelled ? "AutomationPreviewSuppressedCancelled" : "AutomationPreviewSuppressedFailed");
                else if (busy.Contains(path) || !IsReadable(path, fingerprint))
                    Set(AutomationPreviewState.Waiting, "AutomationPreviewBusy");
                else
                {
                    var source = FormatCatalog.FindByExtension(path)!;
                    foreach (var configuration in rule.GetOutputs())
                    {
                        if (items.Count >= limit) return new(items, true);
                        var target = FormatCatalog.FindById(configuration.TargetFormatId)!;
                        state = AutomationPreviewState.Ready;
                        reason = LocalizationService.T("AutomationPreviewReady");
                        output = null;
                        if (!FormatCatalog.TargetsFor(source.ContentKind, source.Id).Contains(target.DisplayName))
                        {
                            state = AutomationPreviewState.UnsupportedTarget;
                            reason = LocalizationService.T("AutomationTargetUnsupported", Path.GetFileName(path), target.DisplayName);
                        }
                        else
                        {
                            var task = new MediaTask
                            {
                                InputPath = path, SourceRoot = rule.FolderPath, ContentKind = source.ContentKind,
                                SourceFormatId = source.Id, TargetFormatId = target.Id,
                                OutputLabel = configuration.Label, ConversionMode = configuration.ConversionMode
                            };
                            foreach (var option in configuration.Options) task.Options[option.Key] = option.Value;
                            var outputSettings = settings.Snapshot();
                            if (configuration.Options.TryGetValue("outputFileNameTemplate", out var fileTemplate))
                                outputSettings.OutputFileNameTemplate = fileTemplate;
                            if (configuration.Options.TryGetValue("preserveFolderStructure", out var keep) && bool.TryParse(keep, out var keepFolders))
                                outputSettings.PreserveFolderStructure = keepFolders;
                            var directory = OutputPathService.ResolveOutputDirectory(task, outputSettings);
                            var name = OutputPathService.BuildFileName(task, outputSettings);
                            var suffix = FormatCatalog.IsFrameSequence(task.TargetFormat) ? " frames" : FormatCatalog.ExtensionFor(task.TargetFormat);
                            output = Path.Combine(directory, name + suffix);
                            for (var index = 1; File.Exists(output) || Directory.Exists(output) || claimedOutputs.Contains(output); index++)
                            {
                                token.ThrowIfCancellationRequested();
                                output = Path.Combine(directory, $"{name} ({index}){suffix}");
                            }
                            claimedOutputs.Add(output);
                        }
                        items.Add(new(path, output, state, reason, configuration.Id, configuration.Label));
                    }
                    continue;
                }
                items.Add(new(path, output, state, reason));

                void Set(AutomationPreviewState value, string key)
                {
                    state = value;
                    reason = LocalizationService.T(key);
                }
            }
            return new(items, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(items, false, LocalizationService.T("AutomationPreviewFolderUnavailable") + " " + exception.Message);
        }
    }

    private static bool IsReadable(string path, AutomationFileFingerprint? fingerprint)
    {
        if (fingerprint is null || fingerprint.Length == 0) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == fingerprint.Length && FileFingerprintService.Read(path) == fingerprint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
