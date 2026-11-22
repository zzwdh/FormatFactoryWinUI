namespace FormatFactoryWinUI.Services;

internal enum ActivationArgumentSource { Unknown, Broker, OrdinaryFallback }
internal enum ActivationDeliveryStage
{
    ArgumentsRead, Registered, RedirectRequested, RedirectReturned, RedirectSkipped, ReceiverArrived,
    Resolved, Queued, QueueRejected, NoWork, WaitingForWindow, WindowReady, WindowClosed, DispatchCompleted, DispatchFailed
}

internal sealed record ActivationDeliveryObservation(Guid Operation, ActivationDiagnosticKind Kind,
    ActivationArgumentSource Source = ActivationArgumentSource.Unknown, bool? OriginalHasWork = null)
{
    internal void Write(ActivationDeliveryStage stage, bool? primary = null, bool? routedWork = null, Exception? error = null)
    {
        // The AppInstance broker does not pass a request ID back to the recipient.
        // The receipt has its own operation. It must never inherit a nearby redirect ID.
        SafeDiagnostics.Write(FormattableString.Invariant(
            $"Activation delivery: session={RuntimeFailureDiagnostics.SessionId:N} operation={Operation:N} observed_utc={DateTimeOffset.UtcNow:O} stage={stage} kind={Kind} source={Source} original_has_work={OriginalHasWork?.ToString() ?? "unknown"} routed_work={routedWork?.ToString() ?? "unknown"} primary={primary?.ToString() ?? "unknown"} broker_link=unbound {(error is null ? "exception=none" : RuntimeFailureDiagnostics.ExceptionFields(error))}"));
    }
}
