using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>Incremental, synchronized lookups shared by polling passes and queue completions.</summary>
internal sealed class AutomationHistoryIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<AutomationFileFingerprint, AutomationProcessedFile>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<AutomationFileFingerprint, bool>> _suppressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<AutomationFileFingerprint, int>> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _unversionedOutputs = new(StringComparer.OrdinalIgnoreCase);

    public AutomationHistoryIndex(IEnumerable<AutomationProcessedFile> records,
        IEnumerable<AutomationSuppressedFile>? suppressions = null)
    {
        foreach (var record in records) Remember(record);
        foreach (var suppression in suppressions ?? []) Suppress(suppression);
    }

    public bool IsKnownSource(string path, AutomationFileFingerprint fingerprint, bool allowFailedRetry)
    {
        path = Normalize(path);
        lock (_gate)
            return _sources.TryGetValue(path, out var versions) && versions.ContainsKey(fingerprint) ||
                _suppressions.TryGetValue(path, out var suppressed) && suppressed.TryGetValue(fingerprint, out var cancelled) &&
                (cancelled || !allowFailedRetry);
    }

    public bool IsExcluded(string path, AutomationFileFingerprint? fingerprint = null)
    {
        path = Normalize(path);
        lock (_gate)
        {
            // An old unversioned output may be a directory. Walk ancestors instead of
            // comparing every historical output with every enumerated input.
            for (string? parent = path; parent is not null; parent = Parent(parent))
                if (_unversionedOutputs.ContainsKey(parent)) return true;
            return _outputs.TryGetValue(path, out var versions) &&
                (fingerprint ?? FileFingerprintService.Read(path)) is { } current && versions.ContainsKey(current);
        }
    }

    public void Remember(AutomationProcessedFile record)
    {
        var path = Normalize(record.InputPath);
        lock (_gate)
        {
            if (!_sources.TryGetValue(path, out var versions)) _sources[path] = versions = [];
            if (versions.TryGetValue(record.Fingerprint, out var previous)) UpdateOutputs(previous, -1);
            versions[record.Fingerprint] = record;
            UpdateOutputs(record, 1);
            if (_suppressions.TryGetValue(path, out var suppressed)) suppressed.Remove(record.Fingerprint);
        }
    }

    public void Suppress(AutomationSuppressedFile suppression)
    {
        var path = Normalize(suppression.InputPath);
        lock (_gate)
        {
            if (_sources.TryGetValue(path, out var completed) && completed.ContainsKey(suppression.Fingerprint)) return;
            if (!_suppressions.TryGetValue(path, out var versions)) _suppressions[path] = versions = [];
            versions[suppression.Fingerprint] = suppression.Cancelled || versions.GetValueOrDefault(suppression.Fingerprint);
        }
    }

    private void UpdateOutputs(AutomationProcessedFile record, int delta)
    {
        if (record.OutputPath is { } output) UpdateOutput(output, record.OutputFingerprint, delta);
        foreach (var receipt in record.Outputs ?? []) UpdateOutput(receipt.OutputPath, receipt.Fingerprint, delta);
        if (record.ArchivedPath is { } archived)
            UpdateOutput(archived, record.ArchiveReceipt?.Fingerprint ?? record.Fingerprint, delta);
    }

    private void UpdateOutput(string path, AutomationFileFingerprint? fingerprint, int delta)
    {
        path = Normalize(path);
        if (fingerprint is null)
        {
            var count = _unversionedOutputs.GetValueOrDefault(path) + delta;
            if (count == 0) _unversionedOutputs.Remove(path);
            else _unversionedOutputs[path] = count;
        }
        else
        {
            if (!_outputs.TryGetValue(path, out var versions)) _outputs[path] = versions = [];
            var count = versions.GetValueOrDefault(fingerprint) + delta;
            if (count == 0) versions.Remove(fingerprint);
            else versions[fingerprint] = count;
            if (versions.Count == 0) _outputs.Remove(path);
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string? Parent(string path) => Path.GetDirectoryName(path) is { } parent &&
        !parent.Equals(path, StringComparison.OrdinalIgnoreCase) ? parent : null;
}
