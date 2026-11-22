namespace FormatFactoryWinUI.Services;

/// <summary>An expected rejection of a source replacement before it is imported.</summary>
public sealed class SourceRelocationValidationException(string message) : InvalidOperationException(message)
{
}
