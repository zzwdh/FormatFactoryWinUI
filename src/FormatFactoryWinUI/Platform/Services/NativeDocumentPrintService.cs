using FormatFactoryWinUI.Documents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace FormatFactoryWinUI.Services;

/// <summary>Owns one window's native print registration. All PrintDocument callbacks complete synchronously.</summary>
internal sealed class NativeDocumentPrintService : IDisposable
{
    internal const int PreviewCacheLimit = 4;
    private readonly Window _window;
    private readonly nint _windowHandle;
    private readonly PrintManager _manager;
    private readonly PrintDocument _document;
    private readonly IPrintDocumentSource _source;
    private readonly NativePrintPreviewCache _previewCache = new(PreviewCacheLimit);

    private readonly List<Grid> _printingPages = [];
    private readonly CancellationTokenSource _lifetime = new();
    private PreparedPrintDocument? _prepared;
    private PrintTaskOptions? _options;
    private TaskCompletionSource<PrintTaskCompletion>? _completion;
    private string _title = string.Empty;
    private bool _disposed;
    private PlatformDiagnosticOperation? _diagnostic;

    public NativeDocumentPrintService(Window window)
    {
        _window = window;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _manager = PrintManagerInterop.GetForWindow(_windowHandle);
        _document = new PrintDocument();
        _source = _document.DocumentSource;
        _manager.PrintTaskRequested += OnPrintTaskRequested;
        _document.Paginate += OnPaginate;
        _document.GetPreviewPage += OnGetPreviewPage;
        _document.AddPages += OnAddPages;
        _window.Closed += OnWindowClosed;
    }

    public async Task<PrintTaskCompletion> ShowAsync(string path, Func<Task>? onPrepared = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completion is not null) throw new InvalidOperationException(LocalizationService.T("ScanPrintAlreadyPrinting"));
        if (!PrintManager.IsSupported()) throw new NotSupportedException(LocalizationService.T("ScanPrintUnavailable"));
        var completion = new TaskCompletionSource<PrintTaskCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.Print)
        { Stage = RuntimeFailureStage.Print, Phase = PlatformDiagnosticPhase.PrintPrepare, HandlePresent = _windowHandle != 0, WindowOpen = !_disposed };
        _diagnostic = diagnostic;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            _title = Path.GetFileName(path);
            // No async XAML print event handlers: page decoding and file I/O preparation
            // finish first. Only a bounded synchronous pixel read is needed in callbacks.
            _prepared = await PreparedPrintDocument.CreateAsync(path, cancellation.Token, progress);
            diagnostic.ResultCount = _prepared.Pages.Count;
            cancellation.Token.ThrowIfCancellationRequested();
            if (onPrepared is not null) await onPrepared();
            if (_disposed) return PrintTaskCompletion.Canceled;
            diagnostic.Phase = PlatformDiagnosticPhase.PrintShow;
            if (!await PrintManagerInterop.ShowPrintUIForWindowAsync(_windowHandle)) return PrintTaskCompletion.Canceled;
            var result = await completion.Task;
            diagnostic.Complete(cancelled: result == PrintTaskCompletion.Canceled);
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return PrintTaskCompletion.Canceled; }
        catch (Exception exception) { diagnostic.Fail(exception); throw; }
        finally
        {
            ClearPreviewPages();
            foreach (var page in _printingPages) NativePrintPreviewCache.ReleasePageImage(page);
            _printingPages.Clear();
            _prepared?.Dispose();
            _prepared = null;
            _options = null;
            _completion = null;
            _diagnostic = null;
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        if (_diagnostic is { } diagnostic) diagnostic.Phase = PlatformDiagnosticPhase.PrintRequest;
        try
        {
            if (_completion is null || _disposed || _prepared is null) return;
            var task = args.Request.CreatePrintTask(_title, requested => requested.SetSource(_source));
            task.Options.PageRangeOptions.AllowAllPages = true;
            task.Options.PageRangeOptions.AllowCurrentPage = false;
            task.Options.PageRangeOptions.AllowCustomSetOfPages = true;
            task.Options.DisplayedOptions.Clear();
            foreach (var option in new[] { StandardPrintTaskOptions.Copies, StandardPrintTaskOptions.CustomPageRanges,
                         StandardPrintTaskOptions.Orientation, StandardPrintTaskOptions.MediaSize,
                         StandardPrintTaskOptions.Duplex, StandardPrintTaskOptions.ColorMode })
                task.Options.DisplayedOptions.Add(option);
            var completion = _completion;
            task.Completed += (_, completed) => completion.TrySetResult(completed.Completion);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void OnPaginate(object sender, PaginateEventArgs args)
    {
        if (_diagnostic is { } diagnostic) diagnostic.Phase = PlatformDiagnosticPhase.PrintPaginate;
        if (_disposed || _prepared is null) return;
        try
        {
            _options = args.PrintTaskOptions;
            ClearPreviewPages();
            _document.SetPreviewPageCount(_prepared.Pages.Count, PreviewPageCountType.Final);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs args)
    {
        if (_diagnostic is { } diagnostic) { diagnostic.Phase = PlatformDiagnosticPhase.PrintPreview; diagnostic.Page = args.PageNumber; }
        if (_disposed || _prepared is null || _options is null) return;
        try
        {
            var page = _previewCache.GetOrCreate(args.PageNumber, () =>
                CreatePage(args.PageNumber, _options.GetPageDescription((uint)(args.PageNumber - 1))));
            _document.SetPreviewPage(args.PageNumber, page);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void OnAddPages(object sender, AddPagesEventArgs args)
    {
        if (_diagnostic is { } diagnostic) diagnostic.Phase = PlatformDiagnosticPhase.PrintAddPages;
        if (_disposed || _prepared is null) return;
        try
        {
            ClearPreviewPages();
            var options = args.PrintTaskOptions;
            var selected = WindowsPrintService.SelectPages(_prepared.Pages.Count,
                options.CustomPageRanges.Select(range => (range.FirstPageNumber, range.LastPageNumber)));
            foreach (var number in selected)
            {
                // Final pages have a separate lifetime: preview eviction must never blank
                // a page being consumed by the print spooler.
                var page = CreatePage(number, options.GetPageDescription((uint)(number - 1)));
                _printingPages.Add(page);
                _document.AddPage(page);
            }
            _document.AddPagesComplete();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private Grid CreatePage(int number, PrintPageDescription description)
    {
        if (_prepared is null || number < 1 || number > _prepared.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(number));
        var source = _prepared.Pages[number - 1].LoadBitmap();
        var page = new Grid
        {
            Width = description.PageSize.Width,
            Height = description.PageSize.Height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            FlowDirection = FlowDirection.LeftToRight
        };
        page.Children.Add(new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            Width = Math.Max(1, description.ImageableRect.Width),
            Height = Math.Max(1, description.ImageableRect.Height),
            Margin = new Thickness(description.ImageableRect.X, description.ImageableRect.Y, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        page.Measure(description.PageSize);
        page.Arrange(new Windows.Foundation.Rect(0, 0, page.Width, page.Height));
        page.UpdateLayout();
        return page;
    }

    private void ClearPreviewPages() => _previewCache.Clear();
    private void Fail(Exception exception)
    {
        _diagnostic?.Fail(exception);
        LogService.WriteAppError(exception);
        _completion?.TrySetException(exception);
    }
    private void OnWindowClosed(object sender, WindowEventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _completion?.TrySetResult(PrintTaskCompletion.Canceled);
        _manager.PrintTaskRequested -= OnPrintTaskRequested;
        _document.Paginate -= OnPaginate;
        _document.GetPreviewPage -= OnGetPreviewPage;
        _document.AddPages -= OnAddPages;
        _window.Closed -= OnWindowClosed;
    }
}


