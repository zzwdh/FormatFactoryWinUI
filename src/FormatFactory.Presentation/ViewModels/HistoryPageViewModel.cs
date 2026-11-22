using System.Collections.ObjectModel;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ViewModels;

/// <summary>Latest-query-wins history state. Results are published only through the owning UI dispatcher.</summary>
public sealed class HistoryPageViewModel : IDisposable
{
    private CancellationTokenSource? _searchCancellation;
    private bool _disposed;
    public ObservableCollection<ConversionHistoryItem> Items { get; } = [];

    public static string DescribeParameters(ConversionHistoryItem item) => ArchiveHistoryReplayService.IsArchive(item)
        ? ArchiveHistoryReplayService.DescribeParameters(item)
        : item.SnapshotVersion == 0
        ? LocalizationService.T("HistoryLegacyParameters")
        : LocalizationService.T("HistoryParametersSummary",
            Models.FormatCatalog.FindById(item.TargetFormatId)?.DisplayName ?? item.TargetFormatId,
            LocalizationService.T(item.ConversionMode switch
            {
                "fast" => "ModeFast", "quality" => "ModeQuality", "lossless" => "ModeLossless", _ => "ModeBalanced"
            }));

    public static string DescribeCompletedAt(ConversionHistoryItem item) =>
        item.CompletedAtUtc.ToLocalTime().ToString("g", LocalizationService.CurrentUICulture);

    public static string DescribeResult(ConversionHistoryItem item) =>
        ConversionResultSummaryFormatter.Format(item.ResultStatistics);

    public async Task RefreshAsync(string? query, bool debounce,
        Func<string?, CancellationToken, Task<IReadOnlyList<ConversionHistoryItem>>> search,
        Func<Func<Task>, Task> dispatch)
    {
        if (_disposed) return;
        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try
        {
            if (debounce) await Task.Delay(250, cancellation.Token);
            var items = await Task.Run(() => search(query, cancellation.Token), cancellation.Token);
            await dispatch(() =>
            {
                if (!_disposed && !cancellation.IsCancellationRequested && ReferenceEquals(_searchCancellation, cancellation))
                    CollectionSynchronizer.Update(Items, items, item => item.TaskId);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _searchCancellation?.Cancel();
    }
}
