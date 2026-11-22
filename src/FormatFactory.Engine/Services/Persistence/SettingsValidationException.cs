namespace FormatFactoryWinUI.Services;

/// <summary>An expected rejection of user settings before they are committed.</summary>
public sealed class SettingsValidationException(string message) : Exception(message)
{
}
