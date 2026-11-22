using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace FormatFactoryWinUI;

internal sealed record NativePreviewSource(string Label, string Path, string? Password);

/// <summary>Bounded, cancellable system preview. Conversion support need not imply playback support.</summary>
internal sealed class NativeMediaPreviewWindow : Window
{
    private readonly ComboBox _sourcePicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = nameof(NativePreviewSource.Label) };
    private readonly Grid _surface = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _scroll;
    private readonly MediaPlayerElement _media = new() { AreTransportControlsEnabled = true, AutoPlay = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = new() { Content = LocalizationService.T("DesktopPreviousPage"), IsEnabled = false };
    private readonly Button _next = new() { Content = LocalizationService.T("DesktopNextPage"), IsEnabled = false };
    private CancellationTokenSource? _load;
    private MediaPlayer? _player;
    private MediaSource? _mediaSource;
    private PdfDocument? _pdf;
    private StorageFile? _imageFile;
    private uint _page;
    private uint _pageCount;
    private bool _closed;

    public NativeMediaPreviewWindow(IReadOnlyList<NativePreviewSource> sources, ElementTheme theme)
    {
        Title = LocalizationService.T("DesktopPreviewTitle");
        SystemBackdrop = new MicaBackdrop();
        _scroll = new ScrollViewer
        {
            Content = _image, ZoomMode = ZoomMode.Enabled, MinZoomFactor = .25f, MaxZoomFactor = 5,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled
        };
        _surface.Children.Add(_scroll);
        _surface.Children.Add(_media);
        var open = new Button { Content = LocalizationService.T("DesktopOpenDefault") };
        open.Click += async (_, _) =>
        {
            if (_sourcePicker.SelectedItem is not NativePreviewSource source) return;
            try
            {
                if (!await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(source.Path)))
                    _status.Text = LocalizationService.T("DesktopOpenFailed");
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.Children.Add(_sourcePicker);
        Grid.SetColumn(open, 1);
        toolbar.Children.Add(open);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pages.Children.Add(_previous);
        pages.Children.Add(_pageLabel);
        pages.Children.Add(_next);
        var root = new Grid { Padding = new Thickness(16), RowSpacing = 12, RequestedTheme = theme };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(toolbar);
        Grid.SetRow(_surface, 1); root.Children.Add(_surface);
        Grid.SetRow(pages, 2); root.Children.Add(pages);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        Content = root;
        AutomationProperties.SetName(_sourcePicker, LocalizationService.T("DesktopPreviewSource"));
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _sourcePicker.ItemsSource = sources;
        _sourcePicker.SelectionChanged += async (_, _) => await LoadSourceAsync();
        _previous.Click += async (_, _) => { if (_page > 0) { _page--; await RenderPageAsync(); } };
        _next.Click += async (_, _) => { if (_page + 1 < _pageCount) { _page++; await RenderPageAsync(); } };
        root.Loaded += (_, _) => { if (_sourcePicker.SelectedIndex < 0) _sourcePicker.SelectedIndex = 0; };
        Closed += (_, _) =>
        {
            _closed = true;
            _load?.Cancel();
            _load?.Dispose();
            ReleaseMedia();
            _image.Source = null;
            _pdf = null;
            // Drop references to the in-memory PDF password when the preview closes.
            _sourcePicker.ItemsSource = null;
        };
        var appWindow = AppWindow;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        appWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1000, area.Width), Math.Min(760, area.Height)));
    }

    private async Task LoadSourceAsync()
    {
        if (_closed || _sourcePicker.SelectedItem is not NativePreviewSource source) return;
        _load?.Cancel();
        _load?.Dispose();
        var cancellation = _load = new CancellationTokenSource();
        var token = cancellation.Token;
        ReleaseMedia();
        _pdf = null; _imageFile = null; _image.Source = null;
        _page = 0; _pageCount = 0;
        _media.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Collapsed;
        UpdatePager();
        _status.Text = LocalizationService.T("DesktopPreviewLoading");
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(source.Path).AsTask(token);
            var kind = FormatCatalog.FindByExtension(source.Path)?.ContentKind;
            if (kind is ContentKind.Video or ContentKind.Audio)
            {
                token.ThrowIfCancellationRequested();
                var mediaSource = _mediaSource = MediaSource.CreateFromStorageFile(file);
                var player = _player = new MediaPlayer { AutoPlay = false, Source = mediaSource };
                player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && ReferenceEquals(player, _player))
                        _status.Text = LocalizationService.T("DesktopPreviewUnsupported");
                });
                _media.SetMediaPlayer(player);
                _media.Visibility = Visibility.Visible;
                _status.Text = string.Empty;
                return;
            }
            if (string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var pdf = string.IsNullOrEmpty(source.Password)
                    ? await PdfDocument.LoadFromFileAsync(file).AsTask(token)
                    : await PdfDocument.LoadFromFileAsync(file, source.Password).AsTask(token);
                token.ThrowIfCancellationRequested();
                _pdf = pdf;
                _pageCount = pdf.PageCount;
            }
            else if (kind == ContentKind.Image)
            {
                using var stream = await file.OpenReadAsync().AsTask(token);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                token.ThrowIfCancellationRequested();
                _imageFile = file;
                _pageCount = decoder.FrameCount;
            }
            else
            {
                _status.Text = LocalizationService.T("DesktopPreviewUnsupported");
                return;
            }
            _scroll.Visibility = Visibility.Visible;
            _scroll.ChangeView(0, 0, 1);
            await RenderPageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_closed)
                _status.Text = LocalizationService.T("DesktopPreviewFailed", ex.Message);
        }
    }

    private async Task RenderPageAsync()
    {
        if (_closed || _load is null || _pageCount == 0) return;
        var token = _load.Token;
        var requestedPage = _page;
        _previous.IsEnabled = _next.IsEnabled = false;
        _status.Text = LocalizationService.T("DesktopPreviewLoading");
        try
        {
            ImageSource source;
            if (_pdf is not null)
            {
                using var page = _pdf.GetPage(requestedPage);
                using var stream = new InMemoryRandomAccessStream();
                var scale = 2048d / Math.Max(page.Size.Width, page.Size.Height);
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                {
                    DestinationWidth = Math.Max(1, (uint)(page.Size.Width * scale)),
                    DestinationHeight = Math.Max(1, (uint)(page.Size.Height * scale))
                }).AsTask(token);
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream).AsTask(token);
                source = bitmap;
            }
            else if (_imageFile is not null)
            {
                using var stream = await _imageFile.OpenReadAsync().AsTask(token);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                var frame = await decoder.GetFrameAsync(requestedPage).AsTask(token);
                var scale = Math.Min(1d, 2048d / Math.Max(frame.PixelWidth, frame.PixelHeight));
                using var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(frame.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(frame.PixelHeight * scale)) },
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
                var bitmapSource = new SoftwareBitmapSource();
                await bitmapSource.SetBitmapAsync(bitmap).AsTask(token);
                source = bitmapSource;
            }
            else return;
            token.ThrowIfCancellationRequested();
            if (_closed || requestedPage != _page) return;
            _image.Source = source;
            _status.Text = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_closed)
                _status.Text = LocalizationService.T("DesktopPreviewFailed", ex.Message);
        }
        finally { if (!token.IsCancellationRequested && !_closed) UpdatePager(); }
    }

    private void UpdatePager()
    {
        _previous.IsEnabled = _page > 0;
        _next.IsEnabled = _page + 1 < _pageCount;
        _pageLabel.Text = _pageCount > 0 ? LocalizationService.T("DesktopPreviewPage", _page + 1, _pageCount) : string.Empty;
    }

    private void ReleaseMedia()
    {
        var player = _player;
        var source = _mediaSource;
        _player = null;
        _mediaSource = null;
        // Detach the transport controls while their player is still alive. Closing
        // it first makes MediaPlayerElement query an aborted playback session.
        try { _media.SetMediaPlayer(null); }
        finally
        {
            try { player?.Dispose(); }
            finally { source?.Dispose(); }
        }
    }
}
