using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private async Task CompleteRecoveredOutputAsync(MediaTaskViewModel task, string outputPath, OutputRecoveryRecord recovery)
    {
        await _queueSession.CompleteRecoveredOutputAsync(task.Model, outputPath, recovery);
        await HandleRecoveredAutomationOutputAsync(task.Model);
        if (task.Model.OutputGroupId is { } groupId)
            await _queueSession.TryFinalizeManualOutputGroupAsync(groupId);
        _feedbackTaskLogs.TryRemove(task.Id, out _);
        UpdateTaskbarProgress();
        UpdateQueueSummary();
        await RefreshHistoryAsync();
    }
}
