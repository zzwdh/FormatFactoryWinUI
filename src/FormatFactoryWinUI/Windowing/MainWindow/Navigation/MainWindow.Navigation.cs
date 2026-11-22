using FormatFactoryWinUI.Controls;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private bool _navigationReady;
    private bool _navigationExpanded;
    private bool _shortNavigation;
    private XamlRoot? _navigationXamlRoot;
    private readonly Dictionary<NavigationViewItem, (string Key, string Glyph, bool Primary)> _navigationLabels = [];
    private readonly Dictionary<(string Glyph, bool Expanded, bool IconOnly, bool Rtl), DataTemplate> _navigationTemplates = [];

    private void InitializeNavigation()
    {
        _navigationLabels.Add(NavAllItem, ("NavAll", "\uE8A9", true));
        _navigationLabels.Add(NavVideoItem, ("NavVideo", "\uE714", true));
        _navigationLabels.Add(NavAudioItem, ("NavAudio", "\uE8D6", true));
        _navigationLabels.Add(NavImageItem, ("NavImage", "\uE91B", true));
        _navigationLabels.Add(NavDocumentItem, ("NavDocument", "\uE8A5", true));
        _navigationLabels.Add(NavEbookItem, ("NavEbook", "\uE736", true));
        _navigationLabels.Add(NavDiscItem, ("NavDisc", "\uE958", true));
        var archiveGlyph = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "\uF012" : "\uE8B7";
        _navigationLabels.Add(NavArchiveItem, ("ArchiveTitle", archiveGlyph, true));
        _navigationLabels.Add(NavAutomationItem, ("AutomationTitle", "\uE895", false));
        _navigationLabels.Add(NavHistoryItem, ("NavHistory", "\uE81C", false));
        _navigationLabels.Add(NavSettingsItem, ("NavSettings", "\uE713", false));
        _navigationReady = true;
        RootGrid.SizeChanged += NavigationRoot_SizeChanged;
        RootGrid.Loaded += NavigationLoaded;
        RootGrid.ActualThemeChanged += NavigationThemeChanged;
        FluentMotion.PreferencesChanged += NavigationPreferencesChanged;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleDragRegion);
        }
        else
        {
            // The navigation toggle remains in the app header below the system caption.
            TitleDragRegion.Visibility = Visibility.Collapsed;
        }
        RefreshNavigationLabels();
        UpdateNavigationTitleBar();
    }

    private void NavigationToggleButton_Click(object sender, RoutedEventArgs args) =>
        SetNavigationExpanded(!_navigationExpanded);

    private void SetNavigationExpanded(bool expanded, bool animate = true)
    {
        if (!_navigationReady || _windowClosed) return;
        var changed = _navigationExpanded != expanded;
        _navigationExpanded = expanded;
        NavigationToggleButton.IsChecked = expanded;
        NavView.OpenPaneLength = expanded ? 232 : 80;
        NavView.IsPaneOpen = true;
        RefreshNavigationLabels();
        // ContentTemplate visuals are replaced during layout. Wait for the new
        // surfaces instead of starting an animation on the outgoing template.
        if (changed && animate && FluentMotion.IsEnabled)
            RootGrid.LayoutUpdated += NavigationLabelsLayoutUpdated;
    }

    private void RefreshNavigationLabels()
    {
        if (!_navigationReady || _windowClosed) return;
        CancelPendingNavigationEntrance();
        foreach (var (item, descriptor) in _navigationLabels)
        {
            var fullLabel = LocalizationService.T(descriptor.Key);
            // Native NavigationView uses string content for search and accessibility.
            // Templates own the visuals instead of placing UIElements in Content.
            item.Content = fullLabel;
            item.Icon = null;
            var key = (Glyph: descriptor.Glyph, Expanded: _navigationExpanded, IconOnly: !descriptor.Primary || _shortNavigation, Rtl: LocalizationService.IsRightToLeft);
            if (!_navigationTemplates.TryGetValue(key, out var template))
            {
                template = NavigationRailLabel.CreateTemplate(key.Glyph, key.Expanded, key.IconOnly, key.Rtl);
                _navigationTemplates.Add(key, template);
            }
            item.ContentTemplate = template;
            item.MinHeight = _navigationExpanded ? 40 : descriptor.Primary && !_shortNavigation ? 52 : 36;
            AutomationProperties.SetName(item, fullLabel);
            ToolTipService.SetToolTip(item, fullLabel);
        }
        var toggleLabel = LocalizationService.T(_navigationExpanded ? "DesktopNavigationCollapse" : "DesktopNavigationExpand");
        AutomationProperties.SetName(NavigationToggleButton, toggleLabel);
        ToolTipService.SetToolTip(NavigationToggleButton, toggleLabel);
        AppTitleText.Text = LocalizationService.T("AppTitle");
        UpdateNavigationPageHeading();
    }

    private void NavigationLabelsLayoutUpdated(object? sender, object args)
    {
        CancelPendingNavigationEntrance();
        if (!_navigationReady || _windowClosed || !FluentMotion.IsEnabled) return;
        foreach (var item in _navigationLabels.Keys)
            if (FindNavigationLabelSurface(item) is { IsLoaded: true } label)
                FluentMotion.FadeIn(label);
    }

    private void CancelPendingNavigationEntrance() =>
        RootGrid.LayoutUpdated -= NavigationLabelsLayoutUpdated;

    private static FrameworkElement? FindNavigationLabelSurface(DependencyObject owner)
    {
        if (owner is FrameworkElement { Name: "NavigationLabelSurface" } element) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
            if (FindNavigationLabelSurface(VisualTreeHelper.GetChild(owner, index)) is { } found) return found;
        return null;
    }

    private void UpdateNavigationPageHeading()
    {
        if (!_navigationReady) return;
        ConverterView.BrandTitleText.Text = NavView.SelectedItem is NavigationViewItem selected &&
            !ReferenceEquals(selected, NavAllItem) && _navigationLabels.TryGetValue(selected, out var descriptor) &&
            descriptor.Primary
                ? LocalizationService.T(descriptor.Key) : LocalizationService.T("AppTitle");
    }

    private void NavigationRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        CaptureDiagnosticUiSnapshot();
        var shortNavigation = args.NewSize.Height < 640;
        if (_navigationExpanded && args.NewSize.Width < 960 && args.NewSize.Width != args.PreviousSize.Width)
            SetNavigationExpanded(false, false);
        if (_shortNavigation != shortNavigation)
        {
            _shortNavigation = shortNavigation;
            RefreshNavigationLabels();
        }
        UpdateNavigationTitleBar();
    }

    private void NavigationThemeChanged(FrameworkElement sender, object args) => UpdateNavigationTitleBar();
    private void NavigationPreferencesChanged(object? sender, EventArgs args)
    {
        if (!FluentMotion.IsEnabled) CancelPendingNavigationEntrance();
        UpdateNavigationTitleBar();
    }
    private void NavigationLoaded(object sender, RoutedEventArgs args)
    {
        CaptureDiagnosticUiSnapshot();
        if (_navigationXamlRoot is not null) _navigationXamlRoot.Changed -= NavigationXamlRootChanged;
        _navigationXamlRoot = RootGrid.XamlRoot;
        if (_navigationXamlRoot is not null) _navigationXamlRoot.Changed += NavigationXamlRootChanged;
        UpdateNavigationTitleBar();
    }

    private void NavigationXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        CaptureDiagnosticUiSnapshot();
        UpdateNavigationTitleBar();
    }

    private void UpdateNavigationTitleBar()
    {
        if (!_navigationReady || _windowClosed || !ExtendsContentIntoTitleBar) return;
        TitleBarAppearanceUpdate.Apply(() =>
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
            var bar = AppWindow.TitleBar;
            // Reserve caption-button space for the entire header, including the toggle
            // when right-to-left layout places that control on the other side.
            AppTitleBar.Margin = new Thickness(bar.LeftInset / scale, 0, bar.RightInset / scale, 0);
            // Windows 10 supplies the caption colors even with an extended title bar.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            bar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            bar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            bar.ButtonForegroundColor = FluentMotion.HighContrast ? null : RootGrid.ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);
        });
    }

    private void DisposeNavigation()
    {
        CancelPendingNavigationEntrance();
        RootGrid.SizeChanged -= NavigationRoot_SizeChanged;
        RootGrid.Loaded -= NavigationLoaded;
        RootGrid.ActualThemeChanged -= NavigationThemeChanged;
        FluentMotion.PreferencesChanged -= NavigationPreferencesChanged;
        if (_navigationXamlRoot is not null) _navigationXamlRoot.Changed -= NavigationXamlRootChanged;
        _navigationXamlRoot = null;
        foreach (var item in _navigationLabels.Keys)
            if (FindNavigationLabelSurface(item) is { } label) FluentMotion.Reset(label);
        _navigationLabels.Clear();
        _navigationTemplates.Clear();
        _navigationReady = false;
    }
}
