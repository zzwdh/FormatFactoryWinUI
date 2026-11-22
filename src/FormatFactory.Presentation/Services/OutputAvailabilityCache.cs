namespace FormatFactoryWinUI.Services;

internal sealed record OutputAvailabilityTarget(Guid TaskId, string Path);
internal sealed record OutputAvailabilityCheck(Guid TaskId, string Path, long Generation);
internal sealed record OutputAvailabilityResult(OutputAvailabilityCheck Check, bool Exists);

/// <summary>
/// Host-context cache. Background probes receive immutable path snapshots only;
/// publishing a result never reads a bound task or collection off the host context.
/// </summary>
internal sealed class OutputAvailabilityCache
{
    private sealed record Entry(string Path, long Generation, bool Exists, long CheckedAt, bool Pending);
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Func<long> _clock;
    private long _generation;

    internal OutputAvailabilityCache(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    internal OutputAvailabilityCheck[] CaptureChecks(IEnumerable<OutputAvailabilityTarget> outputs)
    {
        var targets = outputs.Where(output => !string.IsNullOrWhiteSpace(output.Path)).ToDictionary(output => output.TaskId);
        foreach (var id in _entries.Keys.Where(id => !targets.ContainsKey(id)).ToArray()) _entries.Remove(id);
        var checks = new List<OutputAvailabilityCheck>();
        var now = _clock();
        foreach (var target in targets.Values)
        {
            var samePath = _entries.TryGetValue(target.TaskId, out var entry) &&
                string.Equals(entry.Path, target.Path, StringComparison.OrdinalIgnoreCase);
            if (samePath && (entry!.Pending || now - entry.CheckedAt < 30_000)) continue;
            var generation = ++_generation;
            _entries[target.TaskId] = new(target.Path, generation, samePath && entry!.Exists, now, Pending: true);
            checks.Add(new(target.TaskId, target.Path, generation));
        }
        return checks.ToArray();
    }

    internal bool IsAvailable(Guid taskId, string path) => _entries.TryGetValue(taskId, out var entry) &&
        entry.Exists && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase);

    internal void Invalidate(Guid taskId) => _entries.Remove(taskId);
    internal void Clear() => _entries.Clear();

    internal void Apply(IEnumerable<OutputAvailabilityResult> results)
    {
        foreach (var result in results)
        {
            var check = result.Check;
            if (!_entries.TryGetValue(check.TaskId, out var entry) || entry.Generation != check.Generation ||
                !string.Equals(entry.Path, check.Path, StringComparison.OrdinalIgnoreCase)) continue;
            _entries[check.TaskId] = entry with { Exists = result.Exists, Pending = false, CheckedAt = _clock() };
        }
    }

    internal static Task<OutputAvailabilityResult[]> ProbeAsync(OutputAvailabilityCheck[] checks,
        Func<string, bool> pathExists, CancellationToken cancellationToken)
    {
        // WaitAsync lets window shutdown stop waiting even if Windows is still
        // resolving an offline share. The remaining worker has no window references.
        return Task.Run(() =>
        {
            var results = new List<OutputAvailabilityResult>();
            foreach (var check in checks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exists = pathExists(check.Path);
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new(check, exists));
            }
            return results.ToArray();
        }, cancellationToken).WaitAsync(cancellationToken);
    }
}
