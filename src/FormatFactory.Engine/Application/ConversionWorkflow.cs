using System.Diagnostics;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public sealed record ConversionWorkflowResult(ConversionOutcome Outcome, ConversionExecutionResult Execution, string EngineId)
{
    public string OutputPath => Execution.Artifacts.LastOrDefault(item => !item.IsTemporary)?.Path ?? string.Empty;
    public ConversionResultStatistics? Statistics { get; init; }
}

/// <summary>Engine selection and execution, independent of window and notification lifetimes.</summary>
public sealed class ConversionWorkflow(IEnumerable<IConversionEngine> engines)
{
    private readonly IConversionEngine[] _engines = engines.ToArray();

    public async Task<ConversionWorkflowResult> ExecuteAsync(ConversionRequest request,
        IProgress<ConversionExecutionProgress>? progress, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var engineId = string.Empty;
        long? inputBytes = null;
        ConversionExecutionResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            ValidateDocumentPreservation(request);
            var engine = _engines.FirstOrDefault(candidate =>
                candidate.CanHandle(request.Inputs[0].FormatId, request.TargetFormatId))
                ?? throw new InvalidOperationException(LocalizationService.T("ConversionUiRouteNotImplemented",
                    request.Inputs[0].FormatId, request.TargetFormatId));
            engineId = engine.EngineId;
            inputBytes = await Task.Run(() => ConversionResultMeasurement.MeasureInputs(request.Inputs), cancellationToken)
                .ConfigureAwait(false);
            result = await Task.Run(() => engine.ConvertAsync(request, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new(false, true, [], LocalizationService.T("Cancelled"));
        }
        catch (Exception exception)
        {
            // Keep the full local stack; the completion callback reports this conversion once.
            SafeDiagnostics.Report(exception, notifySubscribers: false);
            result = new(false, false, [], LocalizationService.T("ConversionFailed", exception.Message))
            { Retryable = ConversionFailurePolicy.IsTransient(exception) };
        }
        var outcome = result.Cancelled ? ConversionOutcome.Cancelled
            : result.Success ? ConversionOutcome.Completed : ConversionOutcome.Failed;
        var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var outputBytes = outcome == ConversionOutcome.Completed
            ? await Task.Run(() => ConversionResultMeasurement.MeasureOutputs(result.Artifacts, request)).ConfigureAwait(false)
            : (long?)null;
        NativeDiagnostics.Log.ConversionFinished(request.TaskId.ToString("N"), engineId,
            outcome.ToString().ToLowerInvariant(), elapsedMilliseconds);
        return new(outcome, result, engineId)
        {
            Statistics = new(inputBytes, outputBytes, elapsedMilliseconds)
        };
    }

    private static void ValidateDocumentPreservation(ConversionRequest request)
    {
        try { DocumentPreservationPolicy.Validate(request); }
        catch (NotSupportedException exception)
        {
            FormatFailureDiagnostics.Record(exception, new(FormatFailureStage.DocumentPolicy, FormatFailureReason.UnsupportedRoute)
            {
                Source = request.Inputs[0].FormatId, Target = request.TargetFormatId,
                Count = request.Inputs.Count, Lossless = true
            });
            FormatFailureDiagnostics.WriteApp(exception, request.TaskId);
            throw;
        }
    }
}
