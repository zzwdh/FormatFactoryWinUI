using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ApplicationServices;

public sealed record MediaImportProgress(bool IsScanning, int Discovered, int Analyzed, int Failed);

public sealed partial class MediaImportCoordinator
{
    public Task<IReadOnlyList<string>> DiscoverFolderAsync(string folderPath,
        IProgress<MediaImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var files = new List<string>();
            var lastReport = System.Diagnostics.Stopwatch.StartNew();
            foreach (var entry in FolderDiscovery.Enumerate(folderPath, true, true, true, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(entry.Path);
                if (lastReport.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new(true, files.Count, 0, 0));
                    lastReport.Restart();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Global natural order is required for image collections. Scanning is cancellable,
            // and probing/publication is incremental after this ordered discovery phase.
            var ordered = files.OrderBy(path => Path.GetRelativePath(folderPath, path), ImportPolicy.NaturalPathComparer).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(true, ordered.Length, 0, 0));
            return ordered;
        }, cancellationToken);

    /// <summary>
    /// Publishes ordered batches with backpressure. Successfully published batches remain
    /// owned by the caller on cancellation; unpublished reservations are always released.
    /// The existing atomic import API retains its all-or-nothing cancellation contract.
    /// </summary>
    public async Task<MediaImportBatchResult> ImportPathsProgressivelyAsync(string ffprobePath,
        IReadOnlyList<string> paths, Func<MediaImportBatchResult, Task> publish,
        string? sourceRoot = null, string? sourceRootAccessToken = null,
        IProgress<MediaImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(publish);
        var results = new List<MediaImportItemResult>();
        var failed = 0;
        progress?.Report(new(false, paths.Count, 0, 0));
        // Small ordered batches avoid overwhelming the dispatcher and keep cancellation
        // bounded to at most two probes, as in the existing import scheduler.
        foreach (var pathsBatch in paths.Chunk(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await ImportPathsAsync(ffprobePath, pathsBatch, sourceRoot,
                sourceRootAccessToken, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await publish(batch).ConfigureAwait(false);
            }
            catch
            {
                foreach (var task in batch.AddedTasks) Forget(task);
                throw;
            }
            results.AddRange(batch.Items);
            failed += batch.Failures.Count;
            progress?.Report(new(false, paths.Count, results.Count, failed));
        }
        return new(results);
    }
}
