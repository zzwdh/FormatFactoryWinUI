namespace FormatFactoryWinUI.Documents;

/// <summary>Reads bounded search text without running a third-party filter on the caller's thread.</summary>
internal sealed class WindowsSearchTextReader
{
    internal const int MaximumIndexedCharacters = 200_000;
    private readonly Func<string, CancellationToken, Task<WindowsFilterExtractionResult>> _extractFilter;

    public WindowsSearchTextReader()
        : this(new WindowsFilterTextExtractor().ExtractAsync) { }

    internal WindowsSearchTextReader(
        Func<string, CancellationToken, Task<WindowsFilterExtractionResult>> extractFilter) =>
        _extractFilter = extractFilter ?? throw new ArgumentNullException(nameof(extractFilter));

    public async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaximumIndexedCharacters];
                var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                return new string(buffer, 0, count);
            }

            // Process.Start and registration checks precede the filter client's first await.
            // Keep even that synchronous startup work off the indexing caller's thread.
            var extraction = await Task.Run(() => _extractFilter(path, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return extraction.IsAvailable && extraction.Text.Length > 0
                ? extraction.Text[..Math.Min(extraction.Text.Length, MaximumIndexedCharacters)]
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
