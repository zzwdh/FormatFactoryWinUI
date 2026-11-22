using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ViewModels;

/// <summary>Formats captured values only; rendering a queue or history row never touches the disk.</summary>
public static class ConversionResultSummaryFormatter
{
    public static string Format(ConversionResultStatistics? statistics)
    {
        if (statistics is null) return string.Empty;
        var elapsedSeconds = Math.Max(0, statistics.ElapsedMilliseconds) / 1000;
        var duration = string.Create(LocalizationService.CurrentCulture,
            $"{elapsedSeconds / 3600:00}:{elapsedSeconds / 60 % 60:00}:{elapsedSeconds % 60:00}");
        return FormatSizes(statistics.InputBytes, statistics.OutputBytes) + " · " +
            LocalizationService.T("ResultSummaryElapsed", duration);
    }

    public static string FormatBatch(IEnumerable<MediaTask> tasks)
    {
        var snapshot = tasks.ToArray();
        var completed = snapshot.Where(task => task.State == ConversionState.Completed).ToArray();
        var failed = snapshot.Count(task => task.State == ConversionState.Failed);
        var cancelled = snapshot.Count(task => task.State == ConversionState.Cancelled);
        if (completed.Length + failed + cancelled == 0) return string.Empty;
        var summary = LocalizationService.T("ResultSummaryBatch", completed.Length, failed, cancelled);
        if (completed.Length == 0) return summary;

        // Multi-output tasks share one source. Compare that source once against all
        // of its outputs, and never present a partial output group as space savings.
        long inputBytes = 0, outputBytes = 0;
        try
        {
            foreach (var group in snapshot.GroupBy(task => task.OutputGroupId ?? task.Id))
            {
                var outputs = group.Where(task => task.State == ConversionState.Completed).ToArray();
                if (outputs.Length == 0) continue;
                if (outputs.Length != group.Count() ||
                    outputs.Any(task => task.OutputExpectedCount > outputs.Length ||
                        task.ResultStatistics is not { InputBytes: >= 0, OutputBytes: >= 0 })) return summary;
                var sourceBytes = outputs[0].ResultStatistics!.InputBytes!.Value;
                if (outputs.Any(task => task.ResultStatistics!.InputBytes != sourceBytes)) return summary;
                inputBytes = checked(inputBytes + sourceBytes);
                foreach (var output in outputs) outputBytes = checked(outputBytes + output.ResultStatistics!.OutputBytes!.Value);
            }
        }
        catch (OverflowException) { return summary; }
        return summary + " · " + FormatSizes(inputBytes, outputBytes);
    }

    private static string FormatSizes(long? inputBytes, long? outputBytes)
    {
        var summary = FormatBytes(inputBytes) + " → " + FormatBytes(outputBytes);
        if (inputBytes is not >= 0 || outputBytes is not >= 0) return summary;
        if (inputBytes == outputBytes) return summary + " · " + LocalizationService.T("ResultSummaryUnchanged");
        if (inputBytes == 0) return summary;
        var percentage = Math.Abs((outputBytes.Value - (double)inputBytes.Value) / inputBytes.Value * 100)
            .ToString("0.#", LocalizationService.CurrentCulture);
        return summary + " · " + LocalizationService.T(outputBytes < inputBytes
            ? "ResultSummaryReduced" : "ResultSummaryIncreased", percentage);
    }

    private static string FormatBytes(long? bytes) => bytes switch
    {
        null or < 0 => "—",
        >= 1_099_511_627_776 => string.Create(LocalizationService.CurrentCulture, $"{bytes / 1_099_511_627_776d:0.##} TB"),
        >= 1_073_741_824 => string.Create(LocalizationService.CurrentCulture, $"{bytes / 1_073_741_824d:0.##} GB"),
        >= 1_048_576 => string.Create(LocalizationService.CurrentCulture, $"{bytes / 1_048_576d:0.##} MB"),
        >= 1024 => string.Create(LocalizationService.CurrentCulture, $"{bytes / 1024d:0.##} KB"),
        _ => string.Create(LocalizationService.CurrentCulture, $"{bytes} B")
    };
}
