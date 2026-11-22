namespace FormatFactoryWinUI.Services;

internal enum ErrorCorrelationKind { ConversionTask, SourceImport, Activation }

/// <summary>Identifies the operation that produced a marker, independent of when its report is sent.</summary>
internal sealed record ErrorCorrelationFacts(ErrorCorrelationKind Kind, Guid? Operation, Guid? TaskId);
