using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FormatFactoryWinUI.Services;

/// <summary>Persists review reminders independently of editable application settings.</summary>
public sealed class ReviewReminderService
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan FileLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly long[] Milestones = [10, 50, 100];
    private readonly string _statePath;
    private readonly string _lockPath;

    public ReviewReminderService()
        : this(Path.Combine(LogService.AppDataRoot, "review-reminder.json"))
    {
    }

    internal ReviewReminderService(string statePath)
    {
        _statePath = Path.GetFullPath(statePath);
        _lockPath = _statePath + ".lock";
    }

    /// <summary>Call once for an eligible user-initiated process launch.</summary>
    public Task<bool> RegisterLaunchAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        WithStateAsync(state =>
        {
            if (!state.RemindersStopped)
            {
                state.LaunchCount = checked(state.LaunchCount + 1);
            }
            return IsDue(state, now);
        }, save: true, cancellationToken);

    public Task<bool> IsPromptDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        WithStateAsync(state => IsDue(state, now), save: false, cancellationToken);

    /// <summary>Record only after the reminder dialog has actually been shown.</summary>
    public Task RecordPromptShownAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        WithStateAsync(state =>
        {
            if (!state.RemindersStopped && IsDue(state, now))
            {
                // One prompt satisfies every milestone reached while prompting was deferred.
                state.LastPromptLaunchCount = state.LaunchCount;
                state.LastPromptAt = now;
            }
            return true;
        }, save: true, cancellationToken);

    /// <summary>Call after confirmed review submission or an explicit permanent opt-out.</summary>
    public Task StopRemindersAsync(CancellationToken cancellationToken = default) =>
        WithStateAsync(state =>
        {
            state.RemindersStopped = true;
            return true;
        }, save: true, cancellationToken);

    private static bool IsDue(ReviewReminderState state, DateTimeOffset now)
    {
        if (state.RemindersStopped)
        {
            return false;
        }
        if (Milestones.Any(milestone =>
            state.LaunchCount >= milestone && state.LastPromptLaunchCount < milestone))
        {
            return true;
        }

        // Monthly reminders begin only after the 100-launch reminder was displayed.
        return state.LastPromptLaunchCount >= 100 && state.LastPromptAt is { } lastPromptAt &&
            now >= lastPromptAt.AddMonths(1);
    }

    private async Task<T> WithStateAsync<T>(
        Func<ReviewReminderState, T> action,
        bool save,
        CancellationToken cancellationToken)
    {
        await ProcessGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var state = await ReadStateAsync(cancellationToken);
            var result = action(state);
            if (save)
            {
                await WriteStateAsync(state, cancellationToken);
            }
            return result;
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    private async Task<ReviewReminderState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new ReviewReminderState();
        }

        await using var stream = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync<ReviewReminderState>(stream, Options, cancellationToken);
        if (state is null || state.Version != 1 || state.LaunchCount < 0 ||
            state.LastPromptLaunchCount < 0 || state.LastPromptLaunchCount > state.LaunchCount ||
            (state.LastPromptLaunchCount == 0) != (state.LastPromptAt is null))
        {
            // Resetting damaged state could re-enable reminders that the user had stopped.
            throw new JsonException("The review reminder state is invalid.");
        }
        return state;
    }

    private async Task WriteStateAsync(ReviewReminderState state, CancellationToken cancellationToken)
    {
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        var temporaryPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(snapshot, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Cleanup must not hide the original persistence failure.
            }
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var stopwatch = Stopwatch.StartNew();
        IOException? lastError = null;
        while (stopwatch.Elapsed < FileLockTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return PersistenceFileLock.Open(_lockPath, "review");
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new TimeoutException($"Timed out acquiring the review reminder state lock: {_lockPath}", lastError);
    }

    private sealed class ReviewReminderState
    {
        [JsonRequired]
        public int Version { get; set; } = 1;

        [JsonRequired]
        public long LaunchCount { get; set; }

        [JsonRequired]
        public long LastPromptLaunchCount { get; set; }

        [JsonRequired]
        public DateTimeOffset? LastPromptAt { get; set; }

        [JsonRequired]
        public bool RemindersStopped { get; set; }
    }
}
