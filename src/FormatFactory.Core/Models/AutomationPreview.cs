namespace FormatFactoryWinUI.Models;

public enum AutomationPreviewState
{
    Ready, Disabled, FilterMismatch, OwnedByOtherRule, AlreadyProcessed,
    Cancelled, FailedPreviously, Waiting, Excluded, UnsupportedTarget
}

public sealed record AutomationPreviewItem(
    string InputPath, string? OutputPath, AutomationPreviewState State, string Reason,
    Guid? OutputConfigurationId = null, string? OutputLabel = null);

public sealed record AutomationPreviewResult(
    IReadOnlyList<AutomationPreviewItem> Items, bool Truncated, string? Error = null)
{
    public int Ready => Items.Count(item => item.State == AutomationPreviewState.Ready);
    public int Waiting => Items.Count(item => item.State == AutomationPreviewState.Waiting);
    public int Skipped => Items.Count - Ready - Waiting;
}
