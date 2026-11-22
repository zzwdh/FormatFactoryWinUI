using System.Text.RegularExpressions;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum FailureResolutionAction
{
    None, SaveValidatedOutput, LocateSource, ChooseOutputDirectory, EditConversionSettings,
    OpenEngineSettings, OpenAutomationRules
}

/// <summary>Offers an action only for a recognized failure; the original diagnostic is never replaced.</summary>
public static class FailureResolutionService
{
    private static readonly string[] OutputKeys =
    [
        "OutputDiskInsufficientSpace", "ConversionUiOutputDirectoryNameInvalid",
        "ConversionUiOutputDirectoryNeedsParent", "ConversionUiOutputDirectoryNotDriveRoot"
    ];
    private static readonly string[] SettingsKeys =
    [
        "MediaSizeCannotMeetTarget", "MediaOptionsTargetUnsupported", "MediaOptionsSizeBitrateConflict",
        "MediaSizeDurationRequired", "RemuxUnsupportedStream", "MediaOptionsLosslessConflict",
        "MediaCompatibilityAacSelectionFailed", "MediaCompatibilityAacLossless"
    ];
    private static readonly string[] EngineKeys =
    ["MediaCompatibilityEngineUnavailable", "MediaCompatibilityMissingOutput", "MediaCompatibilityUnsupported",
        "MediaCompatibilityAacUnavailable", "PleaseSetFfmpeg", "FfmpegInvalid"];
    private static readonly Lazy<IReadOnlyDictionary<string, string[][]>> Patterns = new(BuildPatterns);

    public static FailureResolutionAction ForTask(MediaTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsRecoveringOutput || task.State is not (ConversionState.Failed or ConversionState.Blocked))
            return FailureResolutionAction.None;
        if (!string.IsNullOrWhiteSpace(task.RecoverableOutputId)) return FailureResolutionAction.SaveValidatedOutput;
        var manual = SourceRelocationService.IsManualTask(task);
        if (SourceRelocationService.HasMissingInputs(task))
            return manual ? FailureResolutionAction.LocateSource : FailureResolutionAction.OpenAutomationRules;

        var message = string.Join(Environment.NewLine, task.LastError, task.Message, task.BlockedReason);
        var action = Matches(message, OutputKeys) || HasOutputWriteFailure(message)
            ? FailureResolutionAction.ChooseOutputDirectory
            : Matches(message, SettingsKeys) && task.ContentKind != ContentKind.Disc
                ? FailureResolutionAction.EditConversionSettings
                : Matches(message, EngineKeys) ? FailureResolutionAction.OpenEngineSettings : FailureResolutionAction.None;
        // Rules own automatic task settings and output locations. Edit the rule instead of silently overriding it.
        return !manual && action is FailureResolutionAction.ChooseOutputDirectory or FailureResolutionAction.EditConversionSettings
            ? FailureResolutionAction.OpenAutomationRules : action;
    }

    public static string LabelKey(FailureResolutionAction action) => action switch
    {
        FailureResolutionAction.SaveValidatedOutput => "OutputRecoverySaveAs",
        FailureResolutionAction.LocateSource => "SourceRelocationAction",
        FailureResolutionAction.ChooseOutputDirectory => "ChooseFolder",
        FailureResolutionAction.EditConversionSettings => "MediaCustomSettings",
        FailureResolutionAction.OpenEngineSettings => "SettingsTitle",
        FailureResolutionAction.OpenAutomationRules => "NavAutomation",
        _ => string.Empty
    };

    private static bool Matches(string message, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            foreach (var pattern in Patterns.Value.GetValueOrDefault(key) ?? [])
            {
                var offset = 0;
                var matched = true;
                foreach (var part in pattern)
                {
                    var position = message.IndexOf(part, offset, StringComparison.OrdinalIgnoreCase);
                    if (position < 0) { matched = false; break; }
                    offset = position + part.Length;
                }
                if (matched) return true;
            }
        }
        return false;
    }

    private static IReadOnlyDictionary<string, string[][]> BuildPatterns()
    {
        // Cache all translations once. Classification never changes the active culture and does no disk I/O.
        return OutputKeys.Concat(SettingsKeys).Concat(EngineKeys).Distinct().ToDictionary(key => key, key =>
            LocalizedResourceModule.SupportedLanguageCodes.Select(code => LocalizationService.GetTextForLanguage(code, key)).Distinct()
                .Select(template => Regex.Split(template, @"\{\d+(?:[^}]*)\}"))
                .Where(parts => parts.Sum(part => part.Length) >= 6)
                .Select(parts => parts.Where(part => part.Length > 0).ToArray()).ToArray());
    }

    private static bool HasOutputWriteFailure(string message)
    {
        try
        {
            return Regex.IsMatch(message,
                @"(?:^|\r?\n|:\s+)(?:\[[^\r\n]*\]\s*)?(?:Error opening output files?\b|Error writing trailer\b|No space left on device\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) ||
                // Only the application's output-write probe creates this precise random filename.
                // Generic permission errors can refer to inputs, so they are deliberately insufficient.
                Regex.IsMatch(message, @"\.[a-f0-9]{32}\.write-check(?:\.renamed)?(?:['""\s]|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
