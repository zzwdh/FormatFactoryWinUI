using System.Globalization;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Conservative source replacement planning; it never searches by partial names or changes automation ownership.</summary>
public static class SourceRelocationService
{
    private const string SizePrefix = "sourceIdentity.size.";

    public static bool IsManualTask(MediaTask task) =>
        !task.Options.Keys.Any(key => key.StartsWith("automation.", StringComparison.OrdinalIgnoreCase));

    public static bool HasMissingInputs(MediaTask task) =>
        task.ContentKind != ContentKind.Disc && task.InputPaths.Any(path => !File.Exists(path));

    public static void CaptureIdentity(MediaTask task)
    {
        if (!IsManualTask(task)) return;
        for (var index = 0; index < task.InputPaths.Count; index++)
        {
            var size = task.Inputs.ElementAtOrDefault(index)?.Probe?.FileSize;
            if (size is not > 0 && task.InputPaths.Count == 1) size = task.FileSize;
            if (size is > 0) task.Options[SizePrefix + index] = size.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static long? KnownSize(MediaTask task, int index)
    {
        if (task.Options.TryGetValue(SizePrefix + index, out var text) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var saved) && saved > 0) return saved;
        var size = task.Inputs.ElementAtOrDefault(index)?.Probe?.FileSize;
        return size is > 0 ? size : task.InputPaths.Count == 1 && task.FileSize > 0 ? task.FileSize : null;
    }

    public static IReadOnlyDictionary<string, string> MapFolder(MediaTask task, string oldDirectory, string newDirectory)
    {
        var oldRoot = Path.GetFullPath(oldDirectory);
        var newRoot = Path.GetFullPath(newDirectory);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in task.InputPaths)
        {
            if (File.Exists(path)) continue;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), oldRoot, StringComparison.OrdinalIgnoreCase))
                throw SelectionError("SourceRelocationOtherFolder");
            var candidate = Path.Combine(newRoot, Path.GetFileName(path));
            if (!File.Exists(candidate)) throw SelectionError("SourceRelocationCandidateMissing", Path.GetFileName(path));
            result[path] = candidate;
        }
        return result;
    }

    public static PersistedQueueItem CreateRequest(MediaTask task, IReadOnlyDictionary<string, string> replacements,
        bool requireKnownSize = true)
    {
        var operation = Guid.NewGuid();
        try
        {
        if (!IsManualTask(task)) throw SelectionError("SourceRelocationAutomation");
        var request = QueueStore.ToPersistedItem(task);
        var changed = false;
        for (var index = 0; index < request.Inputs.Count; index++)
        {
            var input = request.Inputs[index];
            if (!replacements.TryGetValue(input.Path, out var replacement)) continue;
            if (File.Exists(input.Path)) throw SelectionError("SourceRelocationSourceAvailable");
            var fullPath = Path.GetFullPath(replacement);
            if (!File.Exists(fullPath)) throw SelectionError("SourceRelocationCandidateMissing", Path.GetFileName(fullPath));
            var expectedFormat = FormatCatalog.FindById(input.FormatId) ?? FormatCatalog.FindByExtension(input.Path);
            var actualFormat = FormatCatalog.FindByExtension(fullPath);
            if (expectedFormat is null || actualFormat?.Id != expectedFormat.Id || !actualFormat.CanImport)
                throw SelectionError("SourceRelocationWrongType");
            var knownSize = KnownSize(task, index);
            if (requireKnownSize && knownSize is null) throw SelectionError("SourceRelocationUnknownIdentity");
            if (knownSize is { } expectedSize && new FileInfo(fullPath).Length != expectedSize)
                throw SelectionError("SourceRelocationWrongSize", Path.GetFileName(fullPath));
            input.Path = fullPath;
            // A token for the old file could resolve back to that old file, ignoring this explicit choice.
            input.AccessToken = null;
            changed = true;
        }
        if (!changed) throw SelectionError("SourceRelocationNothingMissing");
        if (request.Inputs.Any(input => !File.Exists(input.Path))) throw SelectionError("SourceRelocationCandidateMissing", task.FileName);
        if (request.Inputs.Select(input => input.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Inputs.Count)
            throw SelectionError("SourceRelocationDuplicateInput");
        request.InputPath = request.Inputs[0].Path;
        request.InputAccessToken = request.Inputs[0].AccessToken;
        // Keep folder-relative output naming only when the original root was exactly the relocated folder.
        request.SourceRoot = task.SourceRoot is not null &&
            string.Equals(Path.GetFullPath(task.SourceRoot), Path.GetDirectoryName(Path.GetFullPath(task.InputPath)), StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(request.InputPath) : null;
        request.SourceRootAccessToken = null;
        return request;
        }
        catch (Exception error) when (error is not SourceRelocationValidationException)
        {
            StorageFailureDiagnostics.Record(error, new(StorageFailureStage.SourceRequest, operation, task?.Id,
                StoragePathKind.Unknown, null, Source: new(StorageSourceCheck.Request, task?.InputPaths.Count ?? 0, replacements?.Count ?? 0)));
            throw;
        }
    }

    public static void ValidateImported(MediaTask original, PersistedQueueItem request, MediaTask imported)
    {
        var operation = Guid.NewGuid();
        var check = StorageSourceCheck.Identity;
        long? expectedBytes = null;
        long? actualBytes = null;
        try
        {
        if (original.Id != imported.Id || original.ContentKind != imported.ContentKind || original.SourceFormatId != imported.SourceFormatId ||
            original.TargetFormatId != imported.TargetFormatId || original.SelectedAudioTrack != imported.SelectedAudioTrack ||
            !request.Inputs.Select(input => input.Path).SequenceEqual(imported.InputPaths, StringComparer.OrdinalIgnoreCase))
            throw Error("SourceRelocationWrongType");
        check = StorageSourceCheck.Size;
        for (var index = 0; index < imported.InputPaths.Count; index++)
            if (KnownSize(original, index) is { } size)
            {
                expectedBytes = size;
                actualBytes = new FileInfo(imported.InputPaths[index]).Length;
                if (actualBytes != size) throw Error("SourceRelocationWrongSize", Path.GetFileName(imported.InputPaths[index]));
            }
        check = StorageSourceCheck.Options;
        MediaConversionOptions.FromTask(original).Validate(imported);
        }
        catch (Exception error)
        {
            StorageFailureDiagnostics.Record(error, new(StorageFailureStage.SourceImportValidation, operation, original?.Id,
                StoragePathKind.Unknown, null, Source: new(check, original?.InputPaths.Count ?? 0, imported?.InputPaths.Count ?? 0,
                    expectedBytes, actualBytes)));
            throw;
        }
    }

    private static SourceRelocationValidationException SelectionError(string key, params object[] args) => new(LocalizationService.T(key, args));

    private static InvalidOperationException Error(string key, params object[] args) => new(LocalizationService.T(key, args));
}
