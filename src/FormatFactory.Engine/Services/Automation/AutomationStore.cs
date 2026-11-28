using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Strict reads and same-directory atomic replacement protect rule and completion data.</summary>
public sealed class AutomationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private readonly Action<AutomationSnapshot>? _beforeCommit;
    private byte[]? _cachedHash;
    private AutomationSnapshot? _cachedSnapshot;
    private bool _cachedNeedsUpgrade;
    private long _deserializationCount;
    private long _writeCount;
    internal long DeserializationCount => Interlocked.Read(ref _deserializationCount);
    internal long WriteCount => Interlocked.Read(ref _writeCount);

    public AutomationStore() : this(Path.Combine(LogService.AppDataRoot, "automation.json")) { }

    public AutomationStore(string path) : this(path, null) { }

    internal AutomationStore(string path, Action<AutomationSnapshot>? beforeCommit)
    {
        _path = Path.GetFullPath(path);
        _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        _beforeCommit = beforeCommit;
    }

    public async Task<AutomationSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task SaveRulesAsync(IEnumerable<AutomationRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var copies = rules.Select(rule => rule.Snapshot()).ToArray();
        ValidateRules(copies);
        foreach (var rule in copies) rule.MigrateOutputs();
        return UpdateAsync(snapshot => JsonSerializer.SerializeToUtf8Bytes(snapshot.Rules, JsonOptions)
            .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(copies, JsonOptions))
                ? snapshot : snapshot with { Rules = copies }, cancellationToken);
    }

    public Task RecordSuccessAsync(AutomationProcessedFile record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);
        return UpdateAsync(snapshot =>
        {
            var sameVersion = snapshot.Records.FirstOrDefault(existing => SameSource(existing.InputPath, existing.Fingerprint, record.InputPath, record.Fingerprint));
            if (sameVersion is not null && SameRecord(sameVersion, record) &&
                !snapshot.Suppressions.Any(existing => SameSource(existing.InputPath, existing.Fingerprint, record.InputPath, record.Fingerprint)) &&
                !snapshot.RetryProgress.Any(existing => SameSource(existing.InputPath, existing.Fingerprint, record.InputPath, record.Fingerprint))) return snapshot;
            return snapshot with
            {
                // Keep historical versions: reverting a source to an already converted version must not duplicate it.
                Records = snapshot.Records.Where(existing =>
                        !existing.InputPath.Equals(record.InputPath, StringComparison.OrdinalIgnoreCase) ||
                        existing.Fingerprint != record.Fingerprint)
                    .Append(record).ToArray(),
                Suppressions = snapshot.Suppressions.Where(existing =>
                        !existing.InputPath.Equals(record.InputPath, StringComparison.OrdinalIgnoreCase) ||
                        existing.Fingerprint != record.Fingerprint).ToArray(),
                RetryProgress = snapshot.RetryProgress.Where(existing =>
                        !existing.InputPath.Equals(record.InputPath, StringComparison.OrdinalIgnoreCase) ||
                        existing.Fingerprint != record.Fingerprint).ToArray()
            };
        }, cancellationToken);
    }

    public Task RecordSuppressionAsync(AutomationSuppressedFile suppression, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        ValidateSuppression(suppression);
        return UpdateAsync(snapshot =>
        {
            var existingSuppression = snapshot.Suppressions.FirstOrDefault(existing => SameSource(existing.InputPath, existing.Fingerprint, suppression.InputPath, suppression.Fingerprint));
            if (existingSuppression == suppression &&
                !snapshot.RetryProgress.Any(existing => SameSource(existing.InputPath, existing.Fingerprint, suppression.InputPath, suppression.Fingerprint))) return snapshot;
            return snapshot with
            {
                // An explicit cancellation dominates a failure checkpoint for the same source version.
                Suppressions = snapshot.Suppressions.Where(existing =>
                        !existing.InputPath.Equals(suppression.InputPath, StringComparison.OrdinalIgnoreCase) ||
                        existing.Fingerprint != suppression.Fingerprint)
                    .Append(suppression with
                    {
                        Cancelled = suppression.Cancelled || snapshot.Suppressions.Any(existing => existing.Cancelled &&
                            existing.InputPath.Equals(suppression.InputPath, StringComparison.OrdinalIgnoreCase) &&
                            existing.Fingerprint == suppression.Fingerprint)
                    }).ToArray(),
                RetryProgress = snapshot.RetryProgress.Where(existing =>
                        !existing.InputPath.Equals(suppression.InputPath, StringComparison.OrdinalIgnoreCase) ||
                        existing.Fingerprint != suppression.Fingerprint).ToArray()
            };
        }, cancellationToken);
    }

    public Task RecordRetryProgressAsync(AutomationRetryProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ValidateRetryProgress(progress);
        return UpdateAsync(snapshot => snapshot.RetryProgress.Any(existing => existing == progress) ? snapshot : snapshot with
        {
            RetryProgress = snapshot.RetryProgress.Where(existing =>
                    !existing.InputPath.Equals(progress.InputPath, StringComparison.OrdinalIgnoreCase) ||
                    existing.Fingerprint != progress.Fingerprint).Append(progress).ToArray()
        }, cancellationToken);
    }

    public Task ResetRetryProgressAsync(string inputPath, AutomationFileFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return UpdateAsync(snapshot => !snapshot.RetryProgress.Any(existing => SameSource(existing.InputPath, existing.Fingerprint, inputPath, fingerprint)) ? snapshot : snapshot with
        {
            RetryProgress = snapshot.RetryProgress.Where(existing =>
                    !existing.InputPath.Equals(inputPath, StringComparison.OrdinalIgnoreCase) ||
                    existing.Fingerprint != fingerprint).ToArray()
        }, cancellationToken);
    }

    public Task MarkScheduledRunAsync(Guid ruleId, string localDate, CancellationToken cancellationToken = default)
    {
        if (ruleId == Guid.Empty || !DateOnly.TryParseExact(localDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new ArgumentException("A scheduled scan requires a rule id and local calendar date.");
        return UpdateAsync(snapshot =>
        {
            if (snapshot.ScheduleRuns.GetValueOrDefault(ruleId) == localDate) return snapshot;
            return snapshot with { ScheduleRuns = new Dictionary<Guid, string>(snapshot.ScheduleRuns) { [ruleId] = localDate } };
        }, cancellationToken);
    }

    private async Task UpdateAsync(Func<AutomationSnapshot, AutomationSnapshot> update, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var transaction = await StoreTransaction.OpenAsync(_path, token, direct: true, hash: true).ConfigureAwait(false);
            // Never replace unreadable or corrupt data with a fresh snapshot.
            var current = await LoadCoreAsync(token).ConfigureAwait(false);
            var snapshot = update(current);
            if (ReferenceEquals(current, snapshot) && !_cachedNeedsUpgrade) return;
            _beforeCommit?.Invoke(Copy(snapshot));
            _cachedHash = await transaction.PublishAsync(snapshot, JsonOptions, token).ConfigureAwait(false);
            _cachedSnapshot = Copy(snapshot);
            _cachedNeedsUpgrade = false;
            Interlocked.Increment(ref _writeCount);
        }
        finally { _gate.Release(); }
    }

    private async Task<AutomationSnapshot> LoadCoreAsync(CancellationToken token)
    {
        FileStream stream;
        try { stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { ClearCache(); return new AutomationSnapshot(); }
        catch (DirectoryNotFoundException) { ClearCache(); return new AutomationSnapshot(); }
        await using (stream)
        {
            // Content identity, rather than timestamps alone, detects same-size external
            // edits and corruption before an update can overwrite them. Unchanged content
            // avoids rebuilding and revalidating the large object graph.
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            if (_cachedHash is not null && hash.AsSpan().SequenceEqual(_cachedHash) && _cachedSnapshot is not null)
                return Copy(_cachedSnapshot);
            stream.Position = 0;
            AutomationSnapshot snapshot;
            try
            {
                snapshot = await JsonSerializer.DeserializeAsync<AutomationSnapshot>(stream, JsonOptions, token)
                    .ConfigureAwait(false) ?? throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
            }
            catch (JsonException exception)
            { throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"), exception); }
            if (snapshot.Version is not (1 or 2) || snapshot.Rules is null || snapshot.Records is null || snapshot.ScheduleRuns is null ||
                snapshot.Suppressions is null || snapshot.RetryProgress is null)
                throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
            var requiresUpgrade = snapshot.Version != 2;
            ValidateRules(snapshot.Rules);
            if (snapshot.Version == 2 && snapshot.Rules.Any(rule => rule.Outputs.Count == 0))
                throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
            foreach (var rule in snapshot.Rules) rule.MigrateOutputs();
            snapshot.Version = 2;
            foreach (var record in snapshot.Records) ValidateRecord(record);
            foreach (var suppression in snapshot.Suppressions) ValidateSuppression(suppression);
            foreach (var progress in snapshot.RetryProgress) ValidateRetryProgress(progress);
            if (snapshot.ScheduleRuns.Any(pair => pair.Key == Guid.Empty ||
                    !DateOnly.TryParseExact(pair.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                throw new InvalidDataException(LocalizationService.T("AutomationValidationSchedule"));
            Interlocked.Increment(ref _deserializationCount);
            _cachedHash = hash;
            _cachedSnapshot = snapshot;
            _cachedNeedsUpgrade = requiresUpgrade;
            return Copy(snapshot);
        }
    }

    private static void ValidateRules(IEnumerable<AutomationRule> rules)
    {
        var ids = new HashSet<Guid>();
        foreach (var rule in rules)
        {
            if (rule is null || !ids.Add(rule.Id)) throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
            rule.Validate();
        }
    }

    private static void ValidateRecord(AutomationProcessedFile? record)
    {
        if (record is null || record.RuleId == Guid.Empty || string.IsNullOrWhiteSpace(record.InputPath) ||
            !Path.IsPathFullyQualified(record.InputPath) || record.Fingerprint is null || record.Fingerprint.Length < 0 ||
            record.Fingerprint.LastWriteTimeUtcTicks <= 0 || record.Fingerprint.CreationTimeUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(record.OutputPath) || !Path.IsPathFullyQualified(record.OutputPath) ||
            (record.ArchivedPath is not null && !Path.IsPathFullyQualified(record.ArchivedPath)) ||
            record.ArchiveReceipt is { } receipt &&
                (string.IsNullOrWhiteSpace(receipt.DestinationPath) || !Path.IsPathFullyQualified(receipt.DestinationPath) ||
                 Path.GetFullPath(receipt.DestinationPath).Equals(Path.GetFullPath(record.InputPath), StringComparison.OrdinalIgnoreCase) ||
                 receipt.Sha256 is not { Length: 64 } || !receipt.Sha256.All(char.IsAsciiHexDigit) ||
                 receipt.Fingerprint is { } archivedFingerprint && (archivedFingerprint.Length != record.Fingerprint.Length ||
                     archivedFingerprint.LastWriteTimeUtcTicks <= 0 || archivedFingerprint.CreationTimeUtcTicks <= 0) ||
                 record.ArchivedPath is { } archivedPath && !Path.GetFullPath(archivedPath).Equals(
                     Path.GetFullPath(receipt.DestinationPath), StringComparison.OrdinalIgnoreCase)) ||
            record.Outputs is { } outputs && (outputs.Count == 0 || outputs.Any(output =>
                output is null || string.IsNullOrWhiteSpace(output.OutputPath) || !Path.IsPathFullyQualified(output.OutputPath))))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
    }

    private static void ValidateSuppression(AutomationSuppressedFile? suppression)
    {
        if (suppression is null || suppression.RuleId == Guid.Empty || string.IsNullOrWhiteSpace(suppression.InputPath) ||
            !Path.IsPathFullyQualified(suppression.InputPath) || suppression.Fingerprint is null ||
            suppression.Fingerprint.Length < 0 || suppression.Fingerprint.LastWriteTimeUtcTicks <= 0 ||
            suppression.Fingerprint.CreationTimeUtcTicks <= 0 || suppression.SuppressedAtUtc == default)
            throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
    }

    private static void ValidateRetryProgress(AutomationRetryProgress? progress)
    {
        if (progress is null || progress.RuleId == Guid.Empty || string.IsNullOrWhiteSpace(progress.InputPath) ||
            !Path.IsPathFullyQualified(progress.InputPath) || progress.Fingerprint is null ||
            progress.Fingerprint.Length < 0 || progress.Fingerprint.LastWriteTimeUtcTicks <= 0 ||
            progress.Fingerprint.CreationTimeUtcTicks <= 0 || progress.FailedAttempts is < 1 or > 6 ||
            progress.RetryAfterUtc == default(DateTimeOffset))
            throw new InvalidDataException(LocalizationService.T("AutomationValidationOptions"));
    }

    private static AutomationSnapshot Copy(AutomationSnapshot snapshot) => snapshot with
    {
        Rules = snapshot.Rules.Select(rule => rule.Snapshot()).ToArray(),
        Records = snapshot.Records.Select(record => record with { Outputs = record.Outputs?.ToArray() }).ToArray(),
        Suppressions = snapshot.Suppressions.ToArray(),
        RetryProgress = snapshot.RetryProgress.ToArray(),
        ScheduleRuns = new Dictionary<Guid, string>(snapshot.ScheduleRuns)
    };

    private static bool SameSource(string first, AutomationFileFingerprint firstVersion, string second, AutomationFileFingerprint secondVersion) =>
        first.Equals(second, StringComparison.OrdinalIgnoreCase) && firstVersion == secondVersion;

    private static bool SameRecord(AutomationProcessedFile first, AutomationProcessedFile second) =>
        (first with { Outputs = null }) == (second with { Outputs = null }) &&
        (first.Outputs is null ? second.Outputs is null : second.Outputs is not null && first.Outputs.SequenceEqual(second.Outputs));

    private void ClearCache()
    {
        _cachedHash = null;
        _cachedSnapshot = null;
        _cachedNeedsUpgrade = false;
    }

}
