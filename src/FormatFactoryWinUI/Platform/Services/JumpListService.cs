using Windows.UI.StartScreen;

namespace FormatFactoryWinUI.Services;

internal static class JumpListService
{
    private const int MaxRecentItems = 3;
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly ConversionHistoryStore History = new();
    private static readonly JumpListRecentOutputStore DismissedOutputs = new(
        Path.Combine(LogService.AppDataRoot, "jump-list-dismissed-outputs.json"));
    internal static readonly (string Target, string LabelKey, string GroupKey)[] Destinations =
    [
        ("all", "NavAll", "JumpListWorkspaceGroup"),
        ("video", "NavVideo", "JumpListConversionGroup"),
        ("audio", "NavAudio", "JumpListConversionGroup"),
        ("image", "NavImage", "JumpListConversionGroup"),
        ("document", "NavDocument", "JumpListConversionGroup"),
        ("ebook", "NavEbook", "JumpListConversionGroup"),
        ("disc", "NavDisc", "JumpListConversionGroup"),
        ("archive", "ArchiveTitle", "JumpListConversionGroup"),
        ("automation", "AutomationTitle", "JumpListWorkspaceGroup"),
        ("history", "HistoryTitle", "JumpListWorkspaceGroup"),
        ("settings", "NavSettings", "JumpListWorkspaceGroup")
    ];

    public static Task InitializeAsync() => RefreshAsync();

    public static async Task RefreshAsync()
    {
        if (!JumpList.IsSupported()) return;
        await RefreshGate.WaitAsync();
        try
        {
            var jumpList = await JumpList.LoadCurrentAsync();
            var removed = jumpList.Items.Where(item => item.RemovedByUser).ToArray();
            jumpList.Items.Clear();
            jumpList.SystemGroupKind = JumpListSystemGroupKind.None;
            var workspaceGroup = LocalizationService.T("JumpListWorkspaceGroup");
            var conversionGroup = LocalizationService.T("JumpListConversionGroup");
            foreach (var (target, labelKey, groupKey) in Destinations)
            {
                var arguments = ActivationArgumentParser.BuildJumpListNavigationArgument(target);
                if (target == "automation") jumpList.Items.Add(JumpListItem.CreateSeparator());
                var item = JumpListItem.CreateWithArguments(arguments, LocalizationService.T(labelKey));
                // Fixed actions belong to native Tasks: custom categories are capacity-limited
                // and can be disabled by the user's Windows recent-items preference.
                var group = groupKey == "JumpListWorkspaceGroup" ? workspaceGroup : conversionGroup;
                item.Description = $"{group}: {LocalizationService.T(labelKey)}";
                item.Logo = new Uri($"ms-appx:///Assets/JumpList-{target}.png");
                jumpList.Items.Add(item);
            }

            // History is supplementary; its failure must never remove page shortcuts.
            IReadOnlyList<ConversionHistoryItem> history = [];
            try { history = await History.LoadAsync(); }
            catch (Exception ex)
            {
                LogService.WriteApp(LocalizationService.T("OperationalLogJumpListUpdateFailed", ex.Message));
            }
            var dismissed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            var canShowRecent = true;
            try
            {
                foreach (var entry in await DismissedOutputs.LoadAsync()) dismissed[entry.Key] = entry.Value;
                var changed = false;
                foreach (var item in removed.Where(item => item.Arguments.StartsWith("--open-output:", StringComparison.OrdinalIgnoreCase)))
                {
                    dismissed[item.Arguments] = DateTimeOffset.UtcNow;
                    changed = true;
                }
                // A newly completed conversion to a previously hidden path may appear again.
                var newestOutputs = history.Where(item => !string.IsNullOrWhiteSpace(item.OutputPath))
                    .GroupBy(item => ActivationService.BuildJumpListOutputArgument(item.OutputPath), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Max(item => item.CompletedAtUtc), StringComparer.OrdinalIgnoreCase);
                foreach (var entry in dismissed.ToArray())
                {
                    if (history.Count > 0 && (!newestOutputs.TryGetValue(entry.Key, out var completedAt) || completedAt > entry.Value))
                        changed |= dismissed.Remove(entry.Key);
                }
                if (changed) await DismissedOutputs.SaveAsync(dismissed);
            }
            catch (Exception ex)
            {
                // Keep fixed navigation usable and never resurrect hidden output paths on a read failure.
                canShowRecent = false;
                LogService.WriteApp(LocalizationService.T("OperationalLogJumpListUpdateFailed", ex.Message));
                // Saving the native list consumes removal markers. Keep them intact until
                // newly observed dismissals are durable, so a retry cannot restore hidden outputs.
                if (removed.Any(item => item.Arguments.StartsWith("--open-output:", StringComparison.OrdinalIgnoreCase)))
                    return;
            }
            var recent = history
                .OrderByDescending(item => item.CompletedAtUtc)
                .Where(item => !string.IsNullOrWhiteSpace(item.OutputPath) &&
                    (File.Exists(item.OutputPath) || Directory.Exists(item.OutputPath)))
                .DistinctBy(item => item.OutputPath, StringComparer.OrdinalIgnoreCase)
                .Where(item => canShowRecent && !dismissed.ContainsKey(ActivationService.BuildJumpListOutputArgument(item.OutputPath)))
                .Take(MaxRecentItems);
            foreach (var item in recent)
            {
                var jumpItem = JumpListItem.CreateWithArguments(
                    ActivationService.BuildJumpListOutputArgument(item.OutputPath),
                    Path.GetFileName(item.OutputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                jumpItem.Description = $"{LocalizationService.T("RecentTasks")}: {item.OutputPath}";
                jumpItem.GroupName = LocalizationService.T("JumpListRecentOutputsGroup");
                jumpList.Items.Add(jumpItem);
            }
            try { await jumpList.SaveAsync(); }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005) &&
                jumpList.Items.Any(item => !string.IsNullOrEmpty(item.GroupName)))
            {
                // Windows can deny custom recent categories while still allowing Tasks.
                foreach (var item in jumpList.Items.Where(item => !string.IsNullOrEmpty(item.GroupName)).ToArray())
                    jumpList.Items.Remove(item);
                await jumpList.SaveAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogJumpListUpdateFailed", ex.Message));
        }
        finally { RefreshGate.Release(); }
    }
}
