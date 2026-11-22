namespace FormatFactoryWinUI.Services;

internal static class AppServiceErrorCodes
{
    public const string None = "none";
    public const string InvalidRequest = "invalid-request";
    public const string UnsupportedSchema = "unsupported-schema";
    public const string UnknownCommand = "unknown-command";
    public const string Canceled = "canceled";
    public const string InternalError = "internal-error";
}

internal sealed record AppServiceCommandRequest(
    int SchemaVersion,
    string RequestId,
    string Command,
    IReadOnlyDictionary<string, object?> Arguments,
    bool IsLegacy,
    string ErrorCode = AppServiceErrorCodes.None)
{
    public bool IsValid => ErrorCode == AppServiceErrorCodes.None;
}

internal static class AppServiceProtocol
{
    public const int CurrentSchemaVersion = 2;

    internal static bool IsStateQuery(string command) =>
        command.Equals("status", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("get-state", StringComparison.OrdinalIgnoreCase);

    public static AppServiceCommandRequest Parse(IEnumerable<KeyValuePair<string, object?>> message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in message)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                values[pair.Key] = pair.Value;
            }
        }

        var command = GetString(values, "command");
        var requestId = GetString(values, "requestId");

        if (!values.TryGetValue("schemaVersion", out var schemaValue))
        {
            var legacyArguments = values
                .Where(pair => !pair.Key.Equals("command", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return new AppServiceCommandRequest(
                1,
                requestId,
                string.IsNullOrWhiteSpace(command) ? "status" : command,
                legacyArguments,
                IsLegacy: true);
        }

        if (!TryGetInt32(schemaValue, out var schemaVersion) || schemaVersion != CurrentSchemaVersion)
        {
            return new AppServiceCommandRequest(
                schemaVersion,
                requestId,
                command,
                EmptyArguments,
                IsLegacy: false,
                AppServiceErrorCodes.UnsupportedSchema);
        }

        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(command))
        {
            return new AppServiceCommandRequest(
                schemaVersion,
                requestId,
                command,
                EmptyArguments,
                IsLegacy: false,
                AppServiceErrorCodes.InvalidRequest);
        }

        return new AppServiceCommandRequest(
            schemaVersion,
            requestId,
            command,
            GetArguments(values),
            IsLegacy: false);
    }

    private static IReadOnlyDictionary<string, object?> EmptyArguments { get; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static string GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value?.ToString()?.Trim() ?? string.Empty : string.Empty;

    private static bool TryGetInt32(object? value, out int result)
    {
        if (value is int number)
        {
            result = number;
            return true;
        }

        return int.TryParse(value?.ToString(), out result);
    }

    private static IReadOnlyDictionary<string, object?> GetArguments(
        IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("arguments", out var value) || value is null)
        {
            return EmptyArguments;
        }

        if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return pairs
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        return EmptyArguments;
    }
}
