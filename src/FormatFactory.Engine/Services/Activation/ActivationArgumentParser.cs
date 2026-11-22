using System.Text.RegularExpressions;

namespace FormatFactoryWinUI.Services;

internal sealed record ActivationRequest(
    IReadOnlyList<string> FilePaths,
    string? OpenPath = null,
    bool OpenSettings = false,
    NotificationAction NotificationAction = NotificationAction.None,
    IActivationLifetime? Lifetime = null,
    Guid? TaskId = null,
    string? TargetFormatId = null,
    string? NavigationTarget = null)
{
    internal ActivationDeliveryObservation? Delivery { get; init; }
    public bool HasWork =>
        FilePaths.Count > 0 ||
        !string.IsNullOrWhiteSpace(OpenPath) ||
        OpenSettings ||
        !string.IsNullOrWhiteSpace(NavigationTarget) ||
        NotificationAction != NotificationAction.None;
}

internal interface IActivationLifetime
{
    void Complete();

    void Fail(string message);
}

internal enum NotificationAction
{
    None,
    RetryFailed,
    Pause,
    Resume,
    Cancel,
    Retry,
    OpenOutput
}

/// <summary>
/// Parses launch and protocol arguments without depending on WinUI or Windows activation types.
/// Windows-specific adapters remain in <see cref="ActivationService"/>.
/// </summary>
internal static class ActivationArgumentParser
{
    public static ActivationRequest FromNotificationArguments(IEnumerable<KeyValuePair<string, string>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            values[argument.Key] = argument.Value;
        }

        values.TryGetValue("action", out var action);
        values.TryGetValue("path", out var path);
        var taskId = TryGetTaskId(values);

        if (action?.Equals("retry-failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new ActivationRequest([], NotificationAction: NotificationAction.RetryFailed);
        }

        var notificationAction = action?.ToLowerInvariant() switch
        {
            "pause" => NotificationAction.Pause,
            "resume" => NotificationAction.Resume,
            "cancel" => NotificationAction.Cancel,
            "retry" => NotificationAction.Retry,
            "open-output" => NotificationAction.OpenOutput,
            _ => NotificationAction.None
        };

        if (notificationAction == NotificationAction.OpenOutput)
        {
            return new ActivationRequest(
                [],
                string.IsNullOrWhiteSpace(path) ? null : path,
                NotificationAction: taskId.HasValue ? notificationAction : NotificationAction.None,
                TaskId: taskId);
        }

        return notificationAction != NotificationAction.None && taskId.HasValue
            ? new ActivationRequest([], NotificationAction: notificationAction, TaskId: taskId)
            : !string.IsNullOrWhiteSpace(path)
                ? new ActivationRequest([], OpenPath: path)
                : action?.Equals("settings", StringComparison.OrdinalIgnoreCase) == true
                    ? new ActivationRequest([], OpenSettings: true)
                    : new ActivationRequest([]);
    }

    public static ActivationRequest FromLaunchArguments(
        string? arguments,
        IReadOnlySet<string> supportedExtensions,
        Func<string, bool>? fileExists = null,
        Func<string, IEnumerable<string>>? batchResolver = null)
    {
        ArgumentNullException.ThrowIfNull(supportedExtensions);
        fileExists ??= File.Exists;

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new ActivationRequest([]);
        }

        var value = arguments.Trim();
        if (value.Equals("--restore", StringComparison.OrdinalIgnoreCase))
        {
            return new ActivationRequest([]);
        }

        if (value.StartsWith("--open-output:", StringComparison.OrdinalIgnoreCase))
        {
            return new ActivationRequest([], SafeUnescape(value[14..]));
        }

        if (value.Equals("--open-settings", StringComparison.OrdinalIgnoreCase))
        {
            return new ActivationRequest([], null, true);
        }

        if (value.StartsWith("--navigate:", StringComparison.OrdinalIgnoreCase))
        {
            return new ActivationRequest([], NavigationTarget: NormalizeNavigationTarget(value[11..]));
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("formatfactory", StringComparison.OrdinalIgnoreCase))
        {
            return FromProtocol(uri, supportedExtensions, fileExists, batchResolver);
        }

        var paths = Regex.Matches(value, "\\\"([^\\\"]+)\\\"|([^\\s]+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Where(fileExists)
            .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ActivationRequest(paths);
    }

    public static ActivationRequest FromProtocol(
        Uri uri,
        IReadOnlySet<string> supportedExtensions,
        Func<string, bool>? fileExists = null,
        Func<string, IEnumerable<string>>? batchResolver = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(supportedExtensions);
        fileExists ??= File.Exists;

        if (!uri.Scheme.Equals("formatfactory", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return new ActivationRequest([], null, true);
        }

        var values = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new KeyValuePair<string, string>(parts[0], SafeUnescape(parts[1])))
            .ToArray();
        var directPaths = values
            .Where(pair => pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value);
        var batchPaths = values
            .Where(pair => pair.Key.Equals("batch", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => ResolveBatch(pair.Value, batchResolver));
        var targetFormatId = values
            .Where(pair => pair.Key.Equals("target", StringComparison.OrdinalIgnoreCase))
            .Select(pair => NormalizeTargetFormatId(pair.Value))
            .FirstOrDefault(value => value is not null);
        var paths = directPaths
            .Concat(batchPaths)
            .Select(path => TryNormalizeImportPath(path, supportedExtensions, fileExists))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Length > 0
            ? new ActivationRequest(paths, TargetFormatId: targetFormatId)
            : new ActivationRequest([], null, true);
    }

    public static string BuildJumpListOutputArgument(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return $"--open-output:{Uri.EscapeDataString(path)}";
    }

    public static string BuildJumpListNavigationArgument(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var target = NormalizeNavigationTarget(tag)
            ?? throw new ArgumentException("The navigation destination is not supported.", nameof(tag));
        return $"--navigate:{target}";
    }

    private static string? NormalizeNavigationTarget(string value) => value.ToLowerInvariant() switch
    {
        "all" => "all",
        "video" => "video",
        "audio" => "audio",
        "image" => "image",
        "document" => "document",
        "ebook" => "ebook",
        "disc" => "disc",
        "archive" => "archive",
        "automation" => "automation",
        "history" => "history",
        "settings" => "settings",
        _ => null
    };

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string? NormalizeTargetFormatId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimStart('.').ToLowerInvariant();
        return normalized.Length <= 32 &&
               normalized.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            ? normalized
            : null;
    }

    private static Guid? TryGetTaskId(IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("taskId", out var value) &&
            !arguments.TryGetValue("task-id", out value))
        {
            return null;
        }

        return Guid.TryParse(value, out var taskId) ? taskId : null;
    }

    private static string? TryNormalizeImportPath(
        string path,
        IReadOnlySet<string> supportedExtensions,
        Func<string, bool> fileExists)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return fileExists(fullPath) && supportedExtensions.Contains(Path.GetExtension(fullPath))
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ResolveBatch(
        string batchId,
        Func<string, IEnumerable<string>>? batchResolver)
    {
        if (batchResolver is null || !Guid.TryParseExact(batchId, "N", out _))
        {
            return [];
        }

        try
        {
            return batchResolver(batchId) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
