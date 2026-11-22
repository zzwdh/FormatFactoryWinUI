using System.Globalization;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI.Controls;

/// <summary>Edits isolated output drafts; applying never changes a running task.</summary>
public static class OutputConfigurationEditorDialog
{
    public static async Task<IReadOnlyList<OutputConfiguration>?> EditAsync(XamlRoot root,
        IReadOnlyList<string> availableFormats, IReadOnlyList<OutputConfiguration> initialOutputs,
        MediaTask? sampleTask = null, IReadOnlyList<AutomationConversionPreset>? presets = null,
        bool allowMultiple = true, Func<ContentDialog, Task<ContentDialogResult>>? showDialog = null)
    {
        var editor = new Editor(availableFormats, initialOutputs, sampleTask, presets, allowMultiple);
        var visuals = new ResourceDictionary { Source = new Uri("ms-appx:///Views/PageResources.xaml") };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = LocalizationService.T(allowMultiple ? "MultiOutputSettings" : "MediaCustomSettings"),
            Style = (Style)visuals["FFContentDialogStyle"],
            Language = LocalizationService.CurrentUICulture.Name,
            FlowDirection = LocalizationService.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Content = new ScrollViewer { Content = editor.Content, MaxHeight = 590, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = LocalizationService.T("AutomationSave"), CloseButtonText = LocalizationService.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources[typeof(ComboBox)] = visuals["FFSelectorStyle"];
        dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0, 0, 0, 16);
        IReadOnlyList<OutputConfiguration>? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { result = editor.Read(); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or OverflowException)
            {
                args.Cancel = true;
                editor.ShowError(exception.Message);
            }
        };
        var selected = showDialog is null ? await dialog.ShowAsync() : await showDialog(dialog);
        return selected == ContentDialogResult.Primary ? result : null;
    }

    private static string DisplayName(string format) => FormatCatalog.FindById(format)?.DisplayName ?? format;

    private sealed class Editor
    {
        private readonly List<OutputConfiguration> _outputs;
        private readonly MediaTask? _sample;
        private readonly bool _multiple;
        private readonly ComboBox _output = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _format = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _label = new() { MaxLength = 64, CornerRadius = new CornerRadius(8), MinHeight = 36 };
        private readonly NumberBox _size = Number("MediaMaximumSize", 0.001, 1048576);
        private readonly NumberBox _width = Number("MediaMaxWidth", 1, 16384);
        private readonly NumberBox _height = Number("MediaMaxHeight", 1, 16384);
        private readonly NumberBox _fps = Number("MediaFrameRate", 1, 240);
        private readonly NumberBox _videoRate = Number("MediaVideoBitrate", 1, 200000);
        private readonly NumberBox _audioRate = Number("MediaAudioBitrate", 8, 1536);
        private readonly NumberBox _sampleRate = Number("MediaSampleRate", 8000, 192000);
        private readonly NumberBox _channels = Number("MediaChannels", 1, 2);
        private readonly NumberBox _quality = Number("MediaImageQuality", 1, 100);
        private readonly StackPanel _media = new() { Spacing = 12 };
        private readonly StackPanel _dimensions = new() { Spacing = 12 };
        private readonly StackPanel _video = new() { Spacing = 12 };
        private readonly StackPanel _audio = new() { Spacing = 12 };
        private readonly StackPanel _image = new() { Spacing = 12 };
        private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private int _index;
        private bool _loading;

        internal Editor(IReadOnlyList<string> formats, IReadOnlyList<OutputConfiguration> initial,
            MediaTask? sample, IReadOnlyList<AutomationConversionPreset>? presets, bool multiple)
        {
            _multiple = multiple;
            _sample = sample;
            _outputs = initial.Select(output => output.Snapshot()).ToList();
            if (_outputs.Count == 0) throw new ArgumentException(nameof(initial));
            if (!multiple && _outputs.Count != 1) throw new ArgumentException(nameof(initial));
            Content = new StackPanel { Spacing = 16, MinWidth = 280, MaxWidth = 600 };
            if (multiple)
            {
                if (presets is { Count: > 0 })
                {
                    var library = new ComboBox
                    {
                        Header = LocalizationService.T("AutomationConversionPreset"), ItemsSource = presets,
                        DisplayMemberPath = nameof(AutomationConversionPreset.Name), HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    library.SelectionChanged += (_, _) =>
                    {
                        if (library.SelectedItem is not AutomationConversionPreset preset) return;
                        var copied = preset.GetOutputs().Select(value => value.Snapshot()).ToList();
                        if (copied.Any(value => !formats.Contains(DisplayName(value.TargetFormatId), StringComparer.OrdinalIgnoreCase)))
                        { ShowError(LocalizationService.T("MultiOutputUnsupported")); return; }
                        _outputs.Clear();
                        _outputs.AddRange(copied);
                        _index = 0;
                        RefreshOutputSelector();
                        Load();
                    };
                    Content.Children.Add(library);
                }
                _output.Header = LocalizationService.T("MultiOutputSettings");
                _output.SelectionChanged += (_, _) =>
                {
                    if (_loading || _output.SelectedIndex < 0 || _index == _output.SelectedIndex) return;
                    var next = _output.SelectedIndex;
                    try { SaveCurrent(); _index = next; Load(); }
                    catch (Exception exception)
                    {
                        ShowError(exception.Message);
                        _loading = true;
                        _output.SelectedIndex = _index;
                        _loading = false;
                    }
                };
                Content.Children.Add(_output);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var add = new Button { Content = LocalizationService.T("MultiOutputAdd") };
                add.Click += (_, _) =>
                {
                    try
                    {
                        SaveCurrent();
                        var copy = _outputs[_index].Snapshot() with { Id = Guid.NewGuid(), Label = string.Empty };
                        _outputs.Add(copy);
                        _index = _outputs.Count - 1;
                        RefreshOutputSelector();
                        Load();
                    }
                    catch (Exception exception) { ShowError(exception.Message); }
                };
                var remove = new Button { Content = LocalizationService.T("MultiOutputRemove") };
                remove.Click += (_, _) =>
                {
                    if (_outputs.Count <= 1) return;
                    _outputs.RemoveAt(_index);
                    _index = Math.Min(_index, _outputs.Count - 1);
                    RefreshOutputSelector();
                    Load();
                };
                actions.Children.Add(add);
                actions.Children.Add(remove);
                Content.Children.Add(actions);
                _label.Header = LocalizationService.T("MultiOutputLabel");
                Content.Children.Add(_label);
            }
            _format.Header = LocalizationService.T("FormatHeader");
            _format.ItemsSource = formats;
            _format.SelectionChanged += (_, _) => UpdateSections();
            _mode.Header = LocalizationService.T("PresetHeader");
            _mode.ItemsSource = FormatCatalog.ConversionModes.Select(value => new LocalizedOption(value, LocalizationService.ModeLabel(value))).ToArray();
            _mode.DisplayMemberPath = nameof(LocalizedOption.DisplayName);
            Content.Children.Add(_format);
            Content.Children.Add(_mode);
            _media.Children.Add(new TextBlock { Text = LocalizationService.T("MediaSettingsHint"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            _media.Children.Add(_size);
            _dimensions.Children.Add(_width);
            _dimensions.Children.Add(_height);
            _media.Children.Add(_dimensions);
            _video.Children.Add(_fps);
            _video.Children.Add(_videoRate);
            _audio.Children.Add(_audioRate);
            _audio.Children.Add(_sampleRate);
            _audio.Children.Add(_channels);
            _image.Children.Add(_quality);
            _media.Children.Add(_video);
            _media.Children.Add(_audio);
            _media.Children.Add(_image);
            Content.Children.Add(_media);
            Content.Children.Add(_error);
            RefreshOutputSelector();
            Load();
        }

        internal StackPanel Content { get; }
        internal void ShowError(string text) { _error.Text = text; _error.Visibility = Visibility.Visible; }
        internal IReadOnlyList<OutputConfiguration> Read()
        {
            SaveCurrent();
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return _outputs.Select(output =>
            {
                var label = output.Label.Trim();
                if (_multiple && string.IsNullOrEmpty(label))
                {
                    var prefix = DisplayName(output.TargetFormatId);
                    label = prefix;
                    for (var count = 2; labels.Contains(label); count++) label = $"{prefix}_{count}";
                }
                if (!string.IsNullOrEmpty(label) && !labels.Add(label)) throw new InvalidDataException(LocalizationService.T("MultiOutputDuplicateLabel"));
                var copy = output.Snapshot() with { Label = label };
                copy.Validate();
                return copy;
            }).ToArray();
        }

        private void SaveCurrent()
        {
            var current = _outputs[_index];
            var target = FormatCatalog.FindById(_format.SelectedItem as string ?? string.Empty)
                ?? throw new InvalidDataException(LocalizationService.T("AutomationValidationTarget"));
            var options = new Dictionary<string, string>(current.Options, StringComparer.OrdinalIgnoreCase);
            var media = new MediaConversionOptions
            {
                TargetSizeBytes = _media.Visibility == Visibility.Visible && _size.IsEnabled ? LongValue(_size, 1048576) : null,
                MaxWidth = _dimensions.Visibility == Visibility.Visible ? IntValue(_width) : null,
                MaxHeight = _dimensions.Visibility == Visibility.Visible ? IntValue(_height) : null,
                FrameRate = _video.Visibility == Visibility.Visible ? DoubleValue(_fps) : null,
                VideoBitrateKbps = _video.Visibility == Visibility.Visible ? IntValue(_videoRate) : null,
                AudioBitrateKbps = _audio.Visibility == Visibility.Visible ? IntValue(_audioRate) : null,
                SampleRate = _audio.Visibility == Visibility.Visible ? IntValue(_sampleRate) : null,
                Channels = _audio.Visibility == Visibility.Visible ? IntValue(_channels) : null,
                ImageQuality = _image.Visibility == Visibility.Visible && _quality.IsEnabled ? IntValue(_quality) : null
            };
            media.ApplyTo(options);
            var mode = (_mode.SelectedItem as LocalizedOption)?.Key ?? FormatCatalog.DefaultConversionMode;
            var copy = current.Snapshot() with { Label = _multiple ? _label.Text.Trim() : current.Label,
                TargetFormatId = target.Id, ConversionMode = mode, Options = options };
            copy.Validate();
            if (_sample is not null)
            {
                var probeTask = new MediaTask { InputPath = _sample.InputPath, SourceFormatId = _sample.SourceFormatId,
                    ContentProbe = _sample.ContentProbe, TargetFormatId = target.Id, ConversionMode = mode,
                    SelectedAudioTrack = current.SelectedAudioTrack };
                foreach (var input in _sample.Inputs) probeTask.Inputs.Add(input);
                media.Validate(probeTask);
            }
            _outputs[_index] = copy;
        }

        private void Load()
        {
            _loading = true;
            var output = _outputs[_index];
            var options = MediaConversionOptions.Parse(output.Options);
            _label.Text = output.Label;
            _format.SelectedItem = DisplayName(output.TargetFormatId);
            _mode.SelectedItem = ((IEnumerable<LocalizedOption>)_mode.ItemsSource).FirstOrDefault(value => value.Key == output.ConversionMode);
            _size.Value = options.TargetSizeBytes / 1048576d ?? double.NaN;
            _width.Value = (double?)options.MaxWidth ?? double.NaN;
            _height.Value = (double?)options.MaxHeight ?? double.NaN;
            _fps.Value = options.FrameRate ?? double.NaN;
            _videoRate.Value = (double?)options.VideoBitrateKbps ?? double.NaN;
            _audioRate.Value = (double?)options.AudioBitrateKbps ?? double.NaN;
            _sampleRate.Value = (double?)options.SampleRate ?? double.NaN;
            _channels.Value = (double?)options.Channels ?? double.NaN;
            _quality.Value = (double?)options.ImageQuality ?? double.NaN;
            _error.Visibility = Visibility.Collapsed;
            UpdateSections();
            _loading = false;
        }

        private void UpdateSections()
        {
            var kind = FormatCatalog.FindById(_format.SelectedItem as string ?? string.Empty)?.ContentKind;
            var format = FormatCatalog.NormalizeFormatId(_format.SelectedItem as string);
            var staticSource = _sample is null || _sample.Kind != MediaKind.Image ||
                (!_sample.Probe.IsAnimated && _sample.Probe.PageCount <= 1);
            _size.IsEnabled = MediaConversionOptions.SupportsTargetSize(format) && staticSource;
            ToolTipService.SetToolTip(_size, _size.IsEnabled ? null : LocalizationService.T("MediaOptionsTargetUnsupported"));
            _quality.IsEnabled = format is "jpg" or "webp" or "avif" && staticSource;
            _media.Visibility = kind is ContentKind.Video or ContentKind.Audio or ContentKind.Image ? Visibility.Visible : Visibility.Collapsed;
            _dimensions.Visibility = kind is ContentKind.Video or ContentKind.Image ? Visibility.Visible : Visibility.Collapsed;
            _video.Visibility = kind == ContentKind.Video ? Visibility.Visible : Visibility.Collapsed;
            _audio.Visibility = kind is ContentKind.Video or ContentKind.Audio ? Visibility.Visible : Visibility.Collapsed;
            _image.Visibility = kind == ContentKind.Image ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshOutputSelector()
        {
            _loading = true;
            _output.ItemsSource = _outputs.Select((value, index) => $"{index + 1}. {value.Label} · {DisplayName(value.TargetFormatId)}").ToArray();
            _output.SelectedIndex = _index;
            _loading = false;
        }

        private static NumberBox Number(string key, double minimum, double maximum)
        {
            var box = new NumberBox { Header = LocalizationService.T(key), Value = double.NaN, Minimum = minimum,
                Maximum = maximum, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                CornerRadius = new CornerRadius(8), MinHeight = 36,
                PlaceholderText = LocalizationService.T("MediaKeepDefault"), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(box, LocalizationService.T(key));
            return box;
        }
        private static double? DoubleValue(NumberBox box) => double.IsNaN(box.Value) ? null : box.Value;
        private static int? IntValue(NumberBox box)
        {
            if (double.IsNaN(box.Value)) return null;
            if (box.Value != Math.Truncate(box.Value)) throw new InvalidDataException(LocalizationService.T("MediaOptionsInvalidValue", box.Header));
            return checked((int)box.Value);
        }
        private static long? LongValue(NumberBox box, long factor) => double.IsNaN(box.Value) ? null : checked((long)Math.Floor(box.Value * factor));
    }
}
