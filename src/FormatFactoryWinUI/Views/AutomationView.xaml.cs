using System.Globalization;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Controls;
using FormatFactoryWinUI.Motion;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI.Views;

public sealed partial class AutomationView : UserControl
{
    private readonly List<AutomationRule> _rules = [];
    private readonly Dictionary<Guid, AutomationRule> _drafts = [];
    private readonly List<AutomationConversionPreset> _presets = [];
    private static readonly string[] PresetOptionKeys =
    [
        "layoutMode", "layout", "ocrLanguage", "language", "repairDocx", "imageColorPolicy",
        "audioTrackPolicy", "selectedAudioTrack", "outputFileNameTemplate", "preserveFolderStructure"
    ];
    private Func<Task<(string Path, string? AccessToken)?>>? _browseFolder;
    private AutomationRule? _editing;
    private bool _updating;
    private bool _initialized;
    private long _statusMotionVersion;
    private (string Key, object?[] Arguments)? _localizedStatus;

    public AutomationView()
    {
        InitializeComponent();
        ApplyLocalization();
        SetEditorEnabled(false);
    }

    internal event Action<IReadOnlyList<AutomationRule>>? RulesChanged;
    internal event Action<Guid>? RunRequested;
    internal event Action<AutomationRule>? PreviewRequested;
    internal event Action<AutomationConversionPreset>? SavePresetRequested;
    internal event Action<Guid>? DeletePresetRequested;
    internal IReadOnlyList<AutomationRule> Rules => _rules.Select(rule => rule.Snapshot()).ToArray();

    internal void SetPresets(IReadOnlyList<AutomationConversionPreset> presets)
    {
        var selected = (PresetBox.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        _presets.Clear();
        _presets.AddRange(presets.Select(preset => preset.Snapshot()));
        RefreshPresets(selected);
    }

    internal void Initialize(IReadOnlyList<AutomationRule> rules,
        Func<Task<(string Path, string? AccessToken)?>> browseFolder)
    {
        _browseFolder = browseFolder;
        if (_initialized) return;
        _initialized = true;
        _rules.AddRange(rules.Select(rule => rule.Snapshot()));
        RefreshRules(_rules.FirstOrDefault()?.Id);
        if (_rules.Count > 0) EditRule(_rules[0]);
    }

    internal void RestoreRules(IReadOnlyList<AutomationRule> rules)
    {
        CaptureDraft();
        _rules.Clear();
        _rules.AddRange(rules.Select(rule => rule.Snapshot()));
        RefreshRules(_editing?.Id);
        if (_editing is null && _rules.FirstOrDefault() is { } first) EditRule(first);
    }

    internal void ShowStatus(string message, bool isError = false)
    {
        _localizedStatus = null;
        DisplayStatus(message, isError);
    }

    internal void ShowLocalizedStatus(string key, bool isError = false, params object?[] arguments)
    {
        _localizedStatus = (key, arguments);
        DisplayStatus(T(key, arguments), isError);
    }

    private void DisplayStatus(string message, bool isError)
    {
        var wasOpen = StatusBar.IsOpen;
        StatusBar.Message = message;
        StatusBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        StatusBar.IsOpen = !string.IsNullOrWhiteSpace(message);
        if (!StatusBar.IsOpen)
        {
            ++_statusMotionVersion;
            FluentMotion.Reset(StatusBar);
            return;
        }
        if (wasOpen) return;
        var version = ++_statusMotionVersion;
        if (!IsLoaded || Visibility != Visibility.Visible || !FluentMotion.IsEnabled) return;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version == _statusMotionVersion && IsLoaded && Visibility == Visibility.Visible && StatusBar.IsOpen)
                FluentMotion.Entrance(StatusBar, FluentMotion.ContentDurationMs, 4);
        });
    }

    internal void ApplyLocalization()
    {
        CaptureDraft();
        if (StatusBar.IsOpen && _localizedStatus is { } status)
            StatusBar.Message = T(status.Key, status.Arguments);
        TitleText.Text = T("AutomationTitle");
        DescriptionText.Text = T("AutomationDescription");
        PriorityHint.Text = T("AutomationPriorityHint");
        EmptyText.Text = T("AutomationEmpty");
        NewButton.Content = T("AutomationNew");
        ToolTipService.SetToolTip(UpButton, T("AutomationMoveUp"));
        ToolTipService.SetToolTip(DownButton, T("AutomationMoveDown"));
        AutomationProperties.SetName(UpButton, T("AutomationMoveUp"));
        AutomationProperties.SetName(DownButton, T("AutomationMoveDown"));
        NameBox.Header = T("AutomationName");
        EnabledBox.Content = T("AutomationEnabled");
        TriggerTitle.Text = T("AutomationTrigger");
        FolderBox.Header = T("AutomationInputFolder");
        FolderBrowseButton.Content = T("Browse");
        OutputBrowseButton.Content = T("Browse");
        ArchiveBrowseButton.Content = T("Browse");
        SubfoldersBox.Content = T("AutomationSubfolders");
        WatchBox.Content = T("AutomationWatch");
        CatchUpBox.Content = T("AutomationCatchUpOnStartup");
        CatchUpHint.Text = T("AutomationCatchUpHint");
        RetryCountBox.Header = T("AutomationRetryCount");
        RetryDelayBox.Header = T("AutomationRetryDelay");
        RetryHint.Text = T("AutomationRetryHint");
        ScheduleBox.Content = T("AutomationSchedule");
        SchedulePicker.Header = T("AutomationScheduleTime");
        ManualHint.Text = T("AutomationManualHint");
        FiltersTitle.Text = T("AutomationFilters");
        KindBox.Header = T("AutomationInputKind");
        ExtensionsBox.Header = T("AutomationExtensions");
        ExtensionsHint.Text = T("AutomationExtensionsHint");
        PatternBox.Header = T("AutomationPattern");
        PatternHint.Text = T("AutomationPatternHint");
        ConversionTitle.Text = T("AutomationConversion");
        TargetBox.Header = T("FormatHeader");
        ModeBox.Header = T("PresetHeader");
        OutputsButton.Content = T("MultiOutputSettings");
        PresetBox.Header = T("AutomationConversionPreset");
        PresetNameBox.Header = T("AutomationPresetName");
        PresetSaveExpander.Header = T("AutomationSavePreset");
        SavePresetButton.Content = T("AutomationSavePreset");
        DeletePresetButton.Content = T("AutomationRemovePreset");
        AdvancedExpander.Header = T("AutomationAdvancedOptions");
        AdvancedHint.Text = T("AutomationAdvancedHint");
        OcrLanguageBox.Header = T("OcrLanguage");
        OcrLanguageBox.PlaceholderText = T("OcrLanguagePlaceholder");
        LayoutBox.Header = T("EbookLayoutMode");
        ImageColorBox.Header = T("AutomationImageColor");
        RepairDocxBox.Content = T("MediaCompatibilityRepairDocx");
        CompatibleAudioBox.Content = T("AutomationCompatibleAudio");
        OutputTemplateBox.Header = T("DesktopUpgradeNamingTemplate");
        OutputTemplateBox.PlaceholderText = T("AutomationUseAppSettings");
        OutputTemplateHint.Text = T("DesktopUpgradeNamingHelp");
        PreserveFoldersBox.Content = T("DesktopUpgradePreserveFolders");
        OutputBox.Header = T("AutomationOutputFolder");
        SourceTitle.Text = T("AutomationSource");
        ArchiveBox.Content = T("AutomationArchive");
        ArchiveFolderBox.Header = T("AutomationArchiveFolder");
        ArchiveHint.Text = T("AutomationArchiveHint");
        SaveButton.Content = T("AutomationSave");
        RunButton.Content = T("AutomationRun");
        PreviewButton.Content = T("AutomationPreview");
        ToolTipService.SetToolTip(RunButton, T("AutomationRunHint"));
        DeleteButton.Content = T("AutomationDelete");
        _updating = true;
        KindBox.Items.Clear();
        KindBox.Items.Add(new ComboBoxItem { Content = T("AutomationAnyKind"), Tag = null });
        foreach (var kind in Enum.GetValues<ContentKind>())
            KindBox.Items.Add(new ComboBoxItem { Content = T("Kind" + kind), Tag = kind });
        ModeBox.Items.Clear();
        foreach (var mode in FormatCatalog.ConversionModes)
            ModeBox.Items.Add(new ComboBoxItem { Content = LocalizationService.ModeLabel(mode), Tag = mode });
        LayoutBox.Items.Clear();
        LayoutBox.Items.Add(new ComboBoxItem { Content = T("AutomationUseAppSettings"), Tag = null });
        LayoutBox.Items.Add(new ComboBoxItem { Content = T("EbookReflow"), Tag = "reflow" });
        LayoutBox.Items.Add(new ComboBoxItem { Content = T("EbookFixed"), Tag = "fixed" });
        ImageColorBox.Items.Clear();
        ImageColorBox.Items.Add(new ComboBoxItem { Content = T("AutomationUseAppSettings"), Tag = null });
        ImageColorBox.Items.Add(new ComboBoxItem { Content = T("AutomationImageColorSrgb"), Tag = "srgb" });
        ImageColorBox.Items.Add(new ComboBoxItem { Content = T("AutomationImageColorPreserve"), Tag = "preserve" });
        _updating = false;
        RefreshPresets((PresetBox.SelectedItem as ComboBoxItem)?.Tag as Guid?);
        RefreshRules(_editing?.Id);
        if (_editing is not null) LoadEditor(_editing);
    }

    private static string T(string key, params object?[] args) => LocalizationService.T(key, args);

    private void RefreshRules(Guid? selectedId)
    {
        _updating = true;
        RulesList.Items.Clear();
        for (var index = 0; index < _rules.Count; index++)
        {
            var rule = _rules[index];
            var panel = new StackPanel { Spacing = 4, Padding = new Thickness(4, 8, 4, 8) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{index + 1}. {rule.Name}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = T("AutomationRuleSummary", rule.InputKind is { } kind ? T("Kind" + kind) : T("AutomationAnyKind"),
                    string.Join(" + ", rule.GetOutputs().Select(output => FormatCatalog.FindById(output.TargetFormatId)?.DisplayName ?? output.TargetFormatId)),
                    LocalizationService.ModeLabel(rule.ConversionMode)),
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
            });
            panel.Children.Add(new TextBlock { Text = rule.FolderPath, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Opacity = 0.7 });
            var toggle = new ToggleSwitch
            {
                IsOn = rule.Enabled, OnContent = T("HardwareOn"), OffContent = T("HardwareOff"), Tag = rule.Id
            };
            AutomationProperties.SetName(toggle, T("AutomationEnabled") + ": " + rule.Name);
            toggle.Toggled += RuleEnabled_Toggled;
            panel.Children.Add(toggle);
            var item = new ListViewItem { Content = panel, Tag = rule.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            RulesList.Items.Add(item);
            if (rule.Id == selectedId) RulesList.SelectedItem = item;
        }
        var hasRules = _rules.Count > 0;
        var listVisibility = hasRules ? Visibility.Visible : Visibility.Collapsed;
        var contentChanged = RulesList.Visibility != listVisibility;
        RulesList.Visibility = listVisibility;
        EmptyText.Visibility = hasRules ? Visibility.Collapsed : Visibility.Visible;
        if (contentChanged)
        {
            FluentMotion.Reset(RulesList);
            FluentMotion.Reset(EmptyText);
            if (IsLoaded && Visibility == Visibility.Visible)
                FluentMotion.Entrance(hasRules ? RulesList : EmptyText, FluentMotion.ContentDurationMs, 6);
        }
        _updating = false;
        UpdateActions();
    }

    private void SetEditorEnabled(bool enabled)
    {
        EditorPane.IsEnabled = enabled;
        UpdateActions();
    }

    private void UpdateActions()
    {
        var index = _editing is null ? -1 : _rules.FindIndex(rule => rule.Id == _editing.Id);
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index < _rules.Count - 1;
        RunButton.IsEnabled = index >= 0 && _rules[index].Enabled;
        PreviewButton.IsEnabled = SavePresetButton.IsEnabled = _editing is not null;
        DeletePresetButton.IsEnabled = _editing is not null && (PresetBox.SelectedItem as ComboBoxItem)?.Tag is Guid;
        DeleteButton.IsEnabled = _editing is not null;
    }

    private void CaptureDraft()
    {
        if (_editing is null || _updating) return;
        var rule = _editing.Snapshot();
        rule.Name = NameBox.Text.Trim();
        rule.Enabled = EnabledBox.IsChecked == true;
        if (!string.Equals(rule.FolderPath, FolderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            rule.FolderAccessToken = null;
        rule.FolderPath = FolderBox.Text.Trim();
        rule.IncludeSubfolders = SubfoldersBox.IsChecked == true;
        rule.WatchEnabled = WatchBox.IsChecked == true;
        rule.CatchUpOnStartup = CatchUpBox.IsChecked == true;
        rule.RetryCount = ReadInteger(RetryCountBox.Value, 0, 5, 0);
        rule.RetryDelaySeconds = ReadInteger(RetryDelayBox.Value, 1, 3600, 30);
        rule.ScheduleEnabled = ScheduleBox.IsChecked == true;
        rule.ScheduleTime = SchedulePicker.Time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        rule.InputKind = (KindBox.SelectedItem as ComboBoxItem)?.Tag is ContentKind kind ? kind : null;
        rule.Extensions = ExtensionsBox.Text.Trim();
        rule.FileNamePattern = PatternBox.Text.Trim();
        rule.TargetFormatId = (TargetBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        rule.ConversionMode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? FormatCatalog.DefaultConversionMode;
        rule.OutputDirectory = OutputBox.Text.Trim();
        rule.ArchiveSource = ArchiveBox.IsChecked == true;
        rule.ArchiveDirectory = ArchiveFolderBox.Text.Trim();
        CaptureAdvancedOptions(rule);
        if (rule.Outputs.Count > 0)
        {
            var first = rule.Outputs[0];
            rule.Outputs[0] = first with
            {
                TargetFormatId = rule.TargetFormatId, ConversionMode = rule.ConversionMode,
                SelectedAudioTrack = rule.Options.TryGetValue("selectedAudioTrack", out var track) && int.TryParse(track, out var index) ? index : first.SelectedAudioTrack,
                Options = new Dictionary<string, string>(rule.Options, StringComparer.OrdinalIgnoreCase)
            };
        }
        _editing = rule;
        _drafts[rule.Id] = rule.Snapshot();
    }

    private void EditRule(AutomationRule rule)
    {
        _editing = _drafts.TryGetValue(rule.Id, out var draft) ? draft.Snapshot() : rule.Snapshot();
        LoadEditor(_editing);
    }

    private void LoadEditor(AutomationRule rule)
    {
        _updating = true;
        NameBox.Text = rule.Name;
        EnabledBox.IsChecked = rule.Enabled;
        FolderBox.Text = rule.FolderPath;
        SubfoldersBox.IsChecked = rule.IncludeSubfolders;
        WatchBox.IsChecked = rule.WatchEnabled;
        CatchUpBox.IsChecked = rule.CatchUpOnStartup;
        RetryCountBox.Value = rule.RetryCount;
        RetryDelayBox.Value = rule.RetryDelaySeconds;
        ScheduleBox.IsChecked = rule.ScheduleEnabled;
        SchedulePicker.Time = TimeSpan.TryParseExact(rule.ScheduleTime, @"hh\:mm", CultureInfo.InvariantCulture, out var time) ? time : TimeSpan.FromHours(9);
        SelectTag(KindBox, rule.InputKind);
        ExtensionsBox.Text = rule.Extensions;
        PatternBox.Text = rule.FileNamePattern;
        RefreshTargets(rule.TargetFormatId);
        SelectTag(ModeBox, rule.ConversionMode);
        OutputBox.Text = rule.OutputDirectory;
        ArchiveBox.IsChecked = rule.ArchiveSource;
        ArchiveFolderBox.Text = rule.ArchiveDirectory;
        LoadAdvancedOptions(rule);
        OutputsSummary.Text = string.Join("\n", rule.GetOutputs().Select(output =>
            $"{(string.IsNullOrWhiteSpace(output.Label) ? FormatCatalog.FindById(output.TargetFormatId)?.DisplayName : output.Label)} · " +
            $"{FormatCatalog.FindById(output.TargetFormatId)?.DisplayName ?? output.TargetFormatId} · {LocalizationService.ModeLabel(output.ConversionMode)}"));
        _updating = false;
        RefreshAdvancedVisibility();
        RetryDelayBox.IsEnabled = rule.RetryCount > 0;
        SchedulePicker.IsEnabled = rule.ScheduleEnabled;
        ArchiveFolderBox.IsEnabled = ArchiveBrowseButton.IsEnabled = rule.ArchiveSource;
        SetEditorEnabled(true);
    }

    private static void SelectTag(ComboBox box, object? tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            item.Tag is string text && tag is string selected
                ? text.Equals(selected, StringComparison.OrdinalIgnoreCase) : Equals(item.Tag, tag)) ?? box.Items.FirstOrDefault();
    }

    private void RefreshTargets(string? selectedId)
    {
        var wasUpdating = _updating;
        _updating = true;
        var selectedKind = (KindBox.SelectedItem as ComboBoxItem)?.Tag as ContentKind?;
        IEnumerable<string> names = selectedKind is { } kind
            ? FormatCatalog.TargetsFor(kind)
            : Enum.GetValues<ContentKind>().SelectMany(value => FormatCatalog.TargetsFor(value));
        TargetBox.Items.Clear();
        foreach (var format in names.Select(FormatCatalog.FindById).OfType<FormatDescriptor>().DistinctBy(format => format.Id))
            TargetBox.Items.Add(new ComboBoxItem { Content = format.DisplayName, Tag = format.Id });
        SelectTag(TargetBox, selectedId);
        _updating = wasUpdating;
    }

    private void RulesList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || RulesList.SelectedItem is not ListViewItem { Tag: Guid id }) return;
        CaptureDraft();
        var rule = _rules.FirstOrDefault(rule => rule.Id == id);
        if (rule is not null) EditRule(rule);
    }

    private void NewButton_Click(object sender, RoutedEventArgs args)
    {
        CaptureDraft();
        var rule = _drafts.Values.FirstOrDefault(draft => _rules.All(saved => saved.Id != draft.Id))?.Snapshot()
            ?? new AutomationRule { Name = T("AutomationNewRule"), InputKind = ContentKind.Video };
        RefreshRules(null);
        EditRule(rule);
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating) return;
        CaptureDraft();
        RefreshTargets((TargetBox.SelectedItem as ComboBoxItem)?.Tag as string);
        RefreshAdvancedVisibility();
    }

    private static int ReadInteger(double value, int minimum, int maximum, int fallback) =>
        double.IsFinite(value) ? Math.Clamp((int)value, minimum, maximum) : fallback;

    private void RefreshPresets(Guid? selectedId)
    {
        var wasUpdating = _updating;
        _updating = true;
        PresetBox.Items.Clear();
        PresetBox.Items.Add(new ComboBoxItem { Content = T("AutomationPresetCustom"), Tag = null });
        foreach (var preset in _presets)
            PresetBox.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset.Id });
        SelectTag(PresetBox, selectedId);
        _updating = wasUpdating;
        UpdateActions();
    }

    private void CaptureAdvancedOptions(AutomationRule rule)
    {
        if (OcrLanguageBox.Visibility == Visibility.Visible)
            SetOption(rule, "ocrLanguage", OcrLanguageBox.Text.Trim(), "language");
        if (LayoutBox.Visibility == Visibility.Visible)
            SetOption(rule, "layoutMode", (LayoutBox.SelectedItem as ComboBoxItem)?.Tag as string, "layout");
        if (ImageColorBox.Visibility == Visibility.Visible)
            SetOption(rule, "imageColorPolicy", (ImageColorBox.SelectedItem as ComboBoxItem)?.Tag as string);
        if (RepairDocxBox.Visibility == Visibility.Visible)
            SetOption(rule, "repairDocx", RepairDocxBox.IsChecked?.ToString().ToLowerInvariant());
        if (CompatibleAudioBox.Visibility == Visibility.Visible)
            SetOption(rule, "audioTrackPolicy", CompatibleAudioBox.IsChecked == true ? "compatible" : null);
        SetOption(rule, "outputFileNameTemplate", OutputTemplateBox.Text.Trim());
        SetOption(rule, "preserveFolderStructure", PreserveFoldersBox.IsChecked?.ToString().ToLowerInvariant());
    }

    private static void SetOption(AutomationRule rule, string key, string? value, string? alias = null)
    {
        if (alias is not null) rule.Options.Remove(alias);
        if (string.IsNullOrWhiteSpace(value)) rule.Options.Remove(key);
        else rule.Options[key] = value;
    }

    private void LoadAdvancedOptions(AutomationRule rule)
    {
        OcrLanguageBox.Text = rule.Options.GetValueOrDefault("ocrLanguage", rule.Options.GetValueOrDefault("language", string.Empty));
        SelectTag(LayoutBox, rule.Options.GetValueOrDefault("layoutMode") ?? rule.Options.GetValueOrDefault("layout"));
        SelectTag(ImageColorBox, rule.Options.GetValueOrDefault("imageColorPolicy"));
        RepairDocxBox.IsChecked = ReadBooleanOption(rule, "repairDocx");
        CompatibleAudioBox.IsChecked = rule.Options.GetValueOrDefault("audioTrackPolicy") == "compatible";
        OutputTemplateBox.Text = rule.Options.GetValueOrDefault("outputFileNameTemplate", string.Empty);
        PreserveFoldersBox.IsChecked = ReadBooleanOption(rule, "preserveFolderStructure");
    }

    private static bool? ReadBooleanOption(AutomationRule rule, string key) =>
        rule.Options.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : null;

    private void RefreshAdvancedVisibility()
    {
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag as ContentKind?;
        var target = FormatCatalog.FindById((TargetBox.SelectedItem as ComboBoxItem)?.Tag as string);
        var mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var documentInput = kind is null or ContentKind.Image or ContentKind.Document or ContentKind.Ebook;
        OcrLanguageBox.Visibility = documentInput && (target?.ContentKind is ContentKind.Document or ContentKind.Ebook)
            ? Visibility.Visible : Visibility.Collapsed;
        LayoutBox.Visibility = documentInput && target?.Id == "epub" ? Visibility.Visible : Visibility.Collapsed;
        ImageColorBox.Visibility = (kind is null or ContentKind.Image) && target?.ContentKind == ContentKind.Image
            ? Visibility.Visible : Visibility.Collapsed;
        RepairDocxBox.Visibility = (kind is null or ContentKind.Document) && (target?.ContentKind is ContentKind.Document or ContentKind.Ebook)
            ? Visibility.Visible : Visibility.Collapsed;
        CompatibleAudioBox.Visibility = (kind is null or ContentKind.Audio or ContentKind.Video)
            && (target?.ContentKind is ContentKind.Audio or ContentKind.Video) && mode != "lossless"
            ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "lossless")
        {
            CompatibleAudioBox.IsChecked = false;
            _editing?.Options.Remove("audioTrackPolicy");
        }
        var preserveColor = ImageColorBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, "preserve"));
        if (preserveColor is not null)
        {
            preserveColor.IsEnabled = (target?.Id is "png" or "jpg" or "tiff") && !(target?.Id == "jpg" && mode == "lossless");
            if (!preserveColor.IsEnabled && ReferenceEquals(ImageColorBox.SelectedItem, preserveColor)
                && ImageColorBox.Visibility == Visibility.Visible)
            {
                SelectTag(ImageColorBox, null);
                _editing?.Options.Remove("imageColorPolicy");
            }
        }
        if (_editing is { } draft) _drafts[draft.Id] = draft.Snapshot();
    }

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating) return;
        CaptureDraft();
        RefreshAdvancedVisibility();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating) return;
        CaptureDraft();
        RefreshAdvancedVisibility();
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || _editing is null) return;
        UpdateActions();
        if ((PresetBox.SelectedItem as ComboBoxItem)?.Tag is not Guid id) return;
        var preset = _presets.FirstOrDefault(value => value.Id == id);
        if (preset is null) return;
        if (preset.GetOutputs().Any(output => !TargetBox.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, output.TargetFormatId))))
        {
            ShowLocalizedStatus("AutomationPresetIncompatible", isError: true);
            RefreshPresets(null);
            return;
        }
        CaptureDraft();
        var draft = _editing!.Snapshot();
        draft.TargetFormatId = preset.TargetFormatId;
        draft.ConversionMode = preset.ConversionMode;
        // Unknown conversion options survive editing; known options are replaced by the preset snapshot.
        foreach (var key in PresetOptionKeys) draft.Options.Remove(key);
        foreach (var key in MediaConversionOptions.OptionNames) draft.Options.Remove(key);
        foreach (var option in preset.Options) draft.Options[option.Key] = option.Value;
        draft.Outputs = preset.GetOutputs().Select(output => output.Snapshot()).ToList();
        _editing = draft;
        _drafts[draft.Id] = draft.Snapshot();
        LoadEditor(draft);
        PresetNameBox.Text = preset.Name;
        ShowLocalizedStatus("AutomationPresetApplied", false, preset.Name);
    }

    private async void OutputsButton_Click(object sender, RoutedEventArgs args)
    {
        CaptureDraft();
        if (_editing is null || XamlRoot is null) return;
        try
        {
            var formats = TargetBox.Items.OfType<ComboBoxItem>().Select(item => FormatCatalog.FindById(item.Tag as string)?.DisplayName)
                .OfType<string>().ToArray();
            var outputs = await OutputConfigurationEditorDialog.EditAsync(XamlRoot, formats, _editing.GetOutputs(),
                presets: _presets, allowMultiple: _editing.InputKind != ContentKind.Disc);
            if (outputs is null) return;
            var draft = _editing.Snapshot();
            draft.Outputs = outputs.Select(output => output.Snapshot()).ToList();
            var first = draft.Outputs[0];
            draft.TargetFormatId = first.TargetFormatId;
            draft.ConversionMode = first.ConversionMode;
            draft.Options = new Dictionary<string, string>(first.Options, StringComparer.OrdinalIgnoreCase);
            if (FormatCatalog.FindById(first.TargetFormatId)?.ContentKind is ContentKind.Audio or ContentKind.Video)
                draft.Options["selectedAudioTrack"] = first.SelectedAudioTrack.ToString(CultureInfo.InvariantCulture);
            _editing = draft;
            _drafts[draft.Id] = draft.Snapshot();
            LoadEditor(draft);
        }
        catch (Exception exception) { ShowStatus(exception.Message, true); }
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs args)
    {
        CaptureDraft();
        if (_editing is null) return;
        var name = PresetNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowLocalizedStatus("AutomationPresetNameRequired", isError: true); return; }
        try
        {
            var preset = AutomationConversionPreset.FromRule(_editing, name);
            if (_presets.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } existing)
                preset = preset with { Id = existing.Id };
            preset.Validate();
            SavePresetRequested?.Invoke(preset.Snapshot());
        }
        catch (InvalidDataException exception) { ShowStatus(exception.Message, true); }
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs args)
    {
        if ((PresetBox.SelectedItem as ComboBoxItem)?.Tag is Guid id) DeletePresetRequested?.Invoke(id);
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs args)
    {
        CaptureDraft();
        if (_editing is null) return;
        try
        {
            _editing.Validate();
            ValidateConversionOptions(_editing);
            PreviewRequested?.Invoke(_editing.Snapshot());
        }
        catch (InvalidDataException exception) { ShowStatus(exception.Message, true); }
    }

    private void RetryCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (RetryDelayBox is not null) RetryDelayBox.IsEnabled = sender.Value > 0;
    }

    private static void ValidateConversionOptions(AutomationRule rule)
    {
        // Validate supported values without dropping unknown rule options or restricting the rule name to preset limits.
        _ = AutomationConversionPreset.FromRule(rule, "Conversion options");
    }

    private void ScheduleBox_Changed(object sender, RoutedEventArgs args)
    {
        if (SchedulePicker is not null) SchedulePicker.IsEnabled = ScheduleBox.IsChecked == true;
    }

    private void ArchiveBox_Changed(object sender, RoutedEventArgs args)
    {
        if (ArchiveFolderBox is null || ArchiveBrowseButton is null) return;
        ArchiveFolderBox.IsEnabled = ArchiveBrowseButton.IsEnabled = ArchiveBox.IsChecked == true;
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_browseFolder is null || sender is not Button button) return;
        try
        {
            var result = await _browseFolder();
            if (result is not { } folder) return;
            switch (button.Tag as string)
            {
                case "input":
                    FolderBox.Text = folder.Path;
                    SetDefaultOutputFolder();
                    if (_editing is not null)
                    {
                        _editing.FolderPath = folder.Path;
                        _editing.FolderAccessToken = folder.AccessToken;
                    }
                    break;
                case "output": OutputBox.Text = folder.Path; break;
                case "archive": ArchiveFolderBox.Text = folder.Path; break;
            }
        }
        catch (Exception exception) { ShowLocalizedStatus("FolderReadFailed", true, exception.Message); }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs args)
    {
        CaptureDraft();
        if (_editing is null) return;
        if (string.IsNullOrWhiteSpace(_editing.Name)) { ShowLocalizedStatus("AutomationValidationName", isError: true); return; }
        if (!Path.IsPathFullyQualified(_editing.FolderPath) || !Path.IsPathFullyQualified(_editing.OutputDirectory) ||
            (_editing.ArchiveSource && !Path.IsPathFullyQualified(_editing.ArchiveDirectory)))
        { ShowLocalizedStatus("AutomationAbsolutePathsRequired", isError: true); return; }
        if (FormatCatalog.FindById(_editing.TargetFormatId) is not { CanExport: true })
        { ShowLocalizedStatus("AutomationValidationTarget", isError: true); return; }
        try { _editing.Validate(); ValidateConversionOptions(_editing); }
        catch (InvalidDataException exception) { ShowStatus(exception.Message, true); return; }
        var index = _rules.FindIndex(rule => rule.Id == _editing.Id);
        if (index < 0) _rules.Add(_editing.Snapshot());
        else _rules[index] = _editing.Snapshot();
        _drafts.Remove(_editing.Id);
        RefreshRules(_editing.Id);
        RulesChanged?.Invoke(Rules);
    }

    private void RuleEnabled_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updating || sender is not ToggleSwitch { Tag: Guid id } toggle) return;
        var rule = _rules.FirstOrDefault(rule => rule.Id == id);
        if (rule is null) return;
        rule.Enabled = toggle.IsOn;
        if (_drafts.TryGetValue(id, out var draft)) draft.Enabled = toggle.IsOn;
        if (_editing?.Id == id) { _editing.Enabled = toggle.IsOn; EnabledBox.IsChecked = toggle.IsOn; }
        UpdateActions();
        RulesChanged?.Invoke(Rules);
    }

    private void RunButton_Click(object sender, RoutedEventArgs args)
    {
        if (_editing is null) return;
        RunRequested?.Invoke(_editing.Id);
    }

    private void FolderBox_LostFocus(object sender, RoutedEventArgs args) => SetDefaultOutputFolder();

    private void SetDefaultOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text) && Path.IsPathFullyQualified(FolderBox.Text.Trim()))
            OutputBox.Text = Path.Combine(FolderBox.Text.Trim(), "Converted");
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs args)
    {
        if (_editing is null) return;
        var id = _editing.Id;
        var index = _rules.FindIndex(rule => rule.Id == id);
        _drafts.Remove(id);
        _editing = null;
        if (index >= 0) _rules.RemoveAt(index);
        var next = _rules.ElementAtOrDefault(Math.Min(Math.Max(index, 0), _rules.Count - 1));
        RefreshRules(next?.Id);
        if (next is not null) EditRule(next);
        else SetEditorEnabled(false);
        if (index >= 0) RulesChanged?.Invoke(Rules);
    }

    private void MoveRule(int offset)
    {
        if (_editing is null) return;
        CaptureDraft();
        var index = _rules.FindIndex(rule => rule.Id == _editing.Id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _rules.Count) return;
        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        RefreshRules(_editing.Id);
        RulesChanged?.Invoke(Rules);
    }

    private void UpButton_Click(object sender, RoutedEventArgs args) => MoveRule(-1);
    private void DownButton_Click(object sender, RoutedEventArgs args) => MoveRule(1);

    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 820;
        ListColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(300);
        EditorColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ListRow.Height = narrow ? new GridLength(220) : new GridLength(1, GridUnitType.Star);
        EditorRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(EditorPane, narrow ? 1 : 0);
        Grid.SetColumn(EditorPane, narrow ? 0 : 1);
    }
}
