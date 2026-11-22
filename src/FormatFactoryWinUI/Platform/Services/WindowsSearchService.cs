using FormatFactoryWinUI.Documents;
using Windows.Storage.Search;

namespace FormatFactoryWinUI.Services;

internal static class WindowsSearchService
{
    private const string IndexName = "FormatFactoryConversions";
    private static readonly WindowsSearchTextReader TextReader = new();

    public static async Task IndexCompletedAsync(ConversionHistoryItem historyItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(historyItem);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(historyItem.OutputPath) ||
            (!File.Exists(historyItem.OutputPath) && !Directory.Exists(historyItem.OutputPath)))
        {
            return;
        }

        try
        {
            var indexer = ContentIndexer.GetIndexer(IndexName);
            var item = new IndexableContent { Id = historyItem.OutputPath };
            item.Properties["System.ItemNameDisplay"] = historyItem.DisplayName;
            item.Properties["System.ItemPathDisplay"] = historyItem.OutputPath;
            item.Properties["FormatFactory.State"] = "Completed";
            item.Properties["FormatFactory.TargetFormat"] = historyItem.TargetFormatId;
            item.Properties["FormatFactory.Engine"] = historyItem.EngineId;
            item.Properties["FormatFactory.CompletedAt"] = historyItem.CompletedAtUtc;
            var text = File.Exists(historyItem.OutputPath)
                ? await TextReader.ReadAsync(historyItem.OutputPath, cancellationToken).ConfigureAwait(false)
                : null;
            if (text is { Length: > 0 })
            {
                item.Properties["System.Search.Contents"] = text;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await indexer.AddAsync(item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogWindowsSearchIndexWriteFailed", ex.Message));
        }
    }

}
