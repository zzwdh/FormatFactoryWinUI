using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>Independent output failures share the source retry budget without replaying permanent failures.</summary>
public static class AutomationOutputExecutionPolicy
{
    public const string RetryableOption = "automation.retryable";

    public static bool ShouldExecute(MediaTask task, AutomationRule rule) =>
        task.State != ConversionState.Completed &&
        (task.State != ConversionState.Failed || rule.ForceRetryFailedOutputs ||
         !task.Options.TryGetValue(RetryableOption, out var value) || !bool.TryParse(value, out var retryable) || retryable);

    public static AutomationExecutionResult Aggregate(IReadOnlyList<AutomationExecutionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0) throw new ArgumentException("At least one output result is required.", nameof(results));
        var failures = results.Where(result => !result.Success).ToArray();
        if (failures.Length == 0)
        {
            var paths = results.Select(result => result.OutputPath!).ToArray();
            return new(true, paths[0], OutputPaths: paths);
        }
        var cancelled = failures.Any(result => result.Cancelled);
        return new(false, Error: string.Join("\n", failures.Select(result => result.Error).Where(error => !string.IsNullOrWhiteSpace(error))),
            Cancelled: cancelled, Deferred: failures.Any(result => result.Deferred),
            Retryable: !cancelled && failures.Any(result => result.Retryable));
    }
}
