using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Publishes only validated output; cancellation and recovery retain the existing path-lease contract.</summary>
internal sealed class MediaOutputCommitter(OutputRecoveryService recovery)
{
    internal ConversionResult Commit(MediaTask task, OutputPathLease outputPathLease, string logPath,
        Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        outputPathLease.CommitValidated(task.Id, recovery: recovery, cancellationToken: cancellationToken,
            targetFormatId: task.TargetFormatId, conversionMode: task.ConversionMode, engineId: task.EngineId);
        ReportCommittedProgress(report);
        return new ConversionResult(true, false, outputPathLease.FinalPath, LocalizationService.T("ConversionFinished"), logPath);
    }

    internal static void ReportCommittedProgress(Action<ConversionProgress> report)
    {
        try { report(new ConversionProgress(100, "—", "0 s", LocalizationService.T("ConversionFinished"))); }
        catch (Exception exception)
        {
            // An observer cannot invalidate an output that has already been validated and committed.
            Debug.WriteLine($"Committed conversion progress observer failed: {exception.Message}");
        }
    }
}
