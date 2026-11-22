using System.Globalization;
using System.Text.Json.Serialization;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Models;

/// <summary>The order of rules is their priority; ids and format ids are persisted.</summary>
public sealed record AutomationRule
{
    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired] public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    [JsonRequired] public string FolderPath { get; set; } = string.Empty;
    public string? FolderAccessToken { get; set; }
    public bool IncludeSubfolders { get; set; }
    public bool WatchEnabled { get; set; } = true;
    public bool CatchUpOnStartup { get; set; }
    public int RetryCount { get; set; }
    public int RetryDelaySeconds { get; set; } = 30;
    public bool ScheduleEnabled { get; set; }
    public string ScheduleTime { get; set; } = "09:00";
    public ContentKind? InputKind { get; set; }
    public string Extensions { get; set; } = string.Empty;
    public string FileNamePattern { get; set; } = string.Empty;
    [JsonRequired] public string TargetFormatId { get; set; } = "mp4";
    public string ConversionMode { get; set; } = FormatCatalog.DefaultConversionMode;
    public string OutputDirectory { get; set; } = string.Empty;
    public bool ArchiveSource { get; set; }
    public string ArchiveDirectory { get; set; } = string.Empty;
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>An empty collection represents the legacy single-output fields until migration.</summary>
    public List<OutputConfiguration> Outputs { get; set; } = [];
    /// <summary>The first round of an explicit run can retry failures that automatic rounds skip.</summary>
    [JsonIgnore] public bool ForceRetryFailedOutputs { get; set; }

    public IReadOnlyList<OutputConfiguration> GetOutputs() => Outputs.Count > 0
        ? Outputs.Select(output => output.Snapshot()).ToArray()
        : [new OutputConfiguration
        {
            Id = Id, TargetFormatId = TargetFormatId, ConversionMode = ConversionMode,
            SelectedAudioTrack = Options.TryGetValue("selectedAudioTrack", out var track) && int.TryParse(track, out var index) ? index : 0,
            Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase)
        }];

    public void MigrateOutputs()
    {
        if (Outputs.Count == 0) Outputs = GetOutputs().Select(output => output.Snapshot()).ToList();
    }

    public AutomationRule Snapshot() => this with
    {
        Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase),
        Outputs = Outputs.Select(output => output.Snapshot()).ToList()
    };

    public void Validate()
    {
        if (Id == Guid.Empty) throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException(LocalizationService.T("AutomationValidationName"));
        ValidatePath(FolderPath, LocalizationService.T("AutomationValidationFolder"));
        if (InputKind is { } kind && !Enum.IsDefined(kind))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationKind"));
        if (RetryCount is < 0 or > 5 || RetryDelaySeconds is < 1 or > 3600)
            throw new InvalidDataException(LocalizationService.T("AutomationValidationRetry"));
        if (ScheduleEnabled && (ScheduleTime is null || ScheduleTime.Length != 5 ||
                !TimeOnly.TryParseExact(ScheduleTime, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var scheduledTime) ||
                !scheduledTime.ToString("HH:mm", CultureInfo.InvariantCulture).Equals(ScheduleTime, StringComparison.Ordinal)))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationSchedule"));
        if (FormatCatalog.FindById(TargetFormatId) is not { CanExport: true })
            throw new InvalidDataException(LocalizationService.T("AutomationValidationTarget"));
        if (!FormatCatalog.ConversionModes.Contains(ConversionMode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationMode"));
        if (Extensions is null || FileNamePattern is null || Options is null ||
            Options.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) ||
            Options.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Options.Count)
            throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
        if (FileNamePattern.Length > 256 || FileNamePattern.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException(LocalizationService.T("AutomationValidationPattern"));
        if (Outputs is null || Outputs.Count > 16 || Outputs.Select(output => output?.Id).Distinct().Count() != Outputs.Count)
            throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
        foreach (var output in Outputs)
        {
            if (output is null) throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
            output.Validate();
            ValidateMediaOptions(output.TargetFormatId, output.ConversionMode, output.Options);
        }
        if (Outputs.Count == 0) ValidateMediaOptions(TargetFormatId, ConversionMode, Options);
        foreach (var extension in Extensions.Split([',', ';', '，', '；'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = extension.Trim().TrimStart('.');
            if (value.Length == 0 || value.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
                throw new InvalidDataException(LocalizationService.T("AutomationValidationExtensions"));
        }
        ValidatePath(OutputDirectory, LocalizationService.T("AutomationValidationDestination"));
        ValidateDestination(OutputDirectory);
        if (ArchiveSource)
        {
            ValidatePath(ArchiveDirectory, LocalizationService.T("AutomationValidationArchive"));
            ValidateDestination(ArchiveDirectory);
            if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputDirectory)).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(ArchiveDirectory)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(LocalizationService.T("AutomationValidationOverlap"));
        }
    }

    private static void ValidatePath(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException(message);
        try { _ = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException(message, exception); }
    }

    private static void ValidateMediaOptions(string targetId, string mode, IReadOnlyDictionary<string, string> options)
    {
        try
        {
            MediaConversionOptions.Parse(options).Validate(new MediaTask
            {
                TargetFormatId = targetId, ContentKind = FormatCatalog.FindById(targetId)!.ContentKind, ConversionMode = mode
            });
        }
        catch (NotSupportedException exception) { throw new InvalidDataException(exception.Message, exception); }
    }

    private void ValidateDestination(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        ValidatePath(directory, LocalizationService.T("AutomationValidationDestination"));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FolderPath));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination.EndsWith(Path.DirectorySeparatorChar) ? destination : destination + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationOverlap"));
    }
}

public sealed record AutomationFileFingerprint(long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks);

public sealed record AutomationOutputReceipt(string OutputPath, AutomationFileFingerprint? Fingerprint);

/// <summary>A durable archive destination and content identity, written before moving a source.</summary>
public sealed record AutomationArchiveReceipt(
    string DestinationPath,
    string Sha256,
    AutomationFileFingerprint? Fingerprint = null);

public sealed record AutomationProcessedFile(
    Guid RuleId,
    string InputPath,
    AutomationFileFingerprint Fingerprint,
    string? OutputPath,
    AutomationFileFingerprint? OutputFingerprint,
    DateTimeOffset CompletedAtUtc,
    string? ArchivedPath = null,
    IReadOnlyList<AutomationOutputReceipt>? Outputs = null,
    AutomationArchiveReceipt? ArchiveReceipt = null);

public sealed record AutomationSnapshot
{
    [JsonRequired] public int Version { get; set; } = 2;
    [JsonRequired] public IReadOnlyList<AutomationRule> Rules { get; set; } = [];
    [JsonRequired] public IReadOnlyList<AutomationProcessedFile> Records { get; set; } = [];
    /// <summary>Terminal source versions skipped by automatic scans; older snapshots omit this collection.</summary>
    public IReadOnlyList<AutomationSuppressedFile> Suppressions { get; set; } = [];
    /// <summary>Partly used automatic retry budgets survive deferred jobs and application restarts.</summary>
    public IReadOnlyList<AutomationRetryProgress> RetryProgress { get; set; } = [];
    /// <summary>Local calendar dates, yyyy-MM-dd, of completed daily scans.</summary>
    [JsonRequired] public Dictionary<Guid, string> ScheduleRuns { get; set; } = [];
}

public sealed record AutomationSuppressedFile(
    Guid RuleId,
    string InputPath,
    AutomationFileFingerprint Fingerprint,
    bool Cancelled,
    DateTimeOffset SuppressedAtUtc);

public sealed record AutomationRetryProgress(
    Guid RuleId,
    string InputPath,
    AutomationFileFingerprint Fingerprint,
    int FailedAttempts,
    DateTimeOffset? RetryAfterUtc = null);

public sealed record AutomationExecutionResult(
    bool Success,
    string? OutputPath = null,
    string? Error = null,
    bool Cancelled = false,
    bool Deferred = false,
    bool Retryable = false,
    IReadOnlyList<string>? OutputPaths = null,
    bool Checkpointed = false,
    bool ArchivePending = false);

public sealed record AutomationRunSummary(int Scanned, int Succeeded, int Failed, int Skipped);

public enum AutomationActivityState { Scanning, Running, Succeeded, Failed, Skipped, Waiting }

public sealed record AutomationActivity(
    Guid RuleId,
    string RuleName,
    string InputPath,
    AutomationActivityState State,
    string Message,
    DateTimeOffset Timestamp);
