using System.Numerics;
using System.Reflection;
using FormatFactoryWinUI.Controls;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FormatFactoryWinUI;

public partial class App
{
    private static readonly (string Tag, string Key)[] NavigationLabels =
    [
        ("all", "NavAll"), ("video", "NavVideo"), ("audio", "NavAudio"),
        ("image", "NavImage"), ("document", "NavDocument"), ("ebook", "NavEbook"),
        ("disc", "NavDisc"), ("archive", "ArchiveTitle"), ("automation", "AutomationTitle"),
        ("history", "NavHistory"), ("settings", "NavSettings")
    ];

    private async Task CheckJumpListNavigationAsync()
    {
        var navigation = (NavigationView)Field("NavView")!;
        var previous = ((NavigationViewItem?)navigation.SelectedItem)?.Tag as string ?? "all";
        var taskIds = _window!.Tasks.Select(task => task.Id).ToArray();
        // The harness detaches production startup and supplies a ready, isolated UI.
        ((TaskCompletionSource<bool>)Field("_activationReady")!).TrySetResult(true);
        try
        {
            Check("Taskbar shortcuts cover every destination and ship their icons", () =>
            {
                Require(JumpListService.Destinations.Select(item => item.Target).Order()
                    .SequenceEqual(NavigationLabels.Select(item => item.Tag).Order()),
                    "Taskbar navigation does not match the application destinations.");
                foreach (var (target, label, group) in JumpListService.Destinations)
                {
                    Require(LocalizationService.T(label) != label && LocalizationService.T(group) != group,
                        $"The {target} shortcut has an untranslated label or group.");
                    Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", $"JumpList-{target}.png")),
                        $"The {target} taskbar icon was not copied into the application.");
                }
            });
            foreach (var (tag, _) in NavigationLabels)
            {
                await CheckAsync($"Taskbar activation reaches {tag} on the running window", async () =>
                {
                    var request = ActivationService.FromLaunchArguments(
                        ActivationArgumentParser.BuildJumpListNavigationArgument(tag));
                    await _window.DispatchActivationAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
                    Require(navigation.SelectedItem is NavigationViewItem selected && Equals(selected.Tag, tag),
                        $"The {tag} shortcut selected a different navigation item.");
                    RequireNavigation(tag);
                    if (tag is not ("settings" or "history" or "archive" or "automation"))
                        Require(Equals(Field("_filter"), tag), $"The {tag} shortcut did not apply its queue filter.");
                    Require(taskIds.SequenceEqual(_window.Tasks.Select(task => task.Id)),
                        "A navigation shortcut changed or imported conversion tasks.");
                });
            }
            await CheckAsync("Queued taskbar activations retain arrival order through page transitions", async () =>
            {
                await using var router = new ActivationRouter();
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var handled = new List<string>();
                foreach (var tag in new[] { "video", "archive", "history", "all" })
                    Require(router.Enqueue(ActivationService.FromLaunchArguments($"--navigate:{tag}")),
                        "Navigation was dropped before the activation handler attached.");
                router.Attach(async request =>
                {
                    await _window.DispatchActivationAsync(request);
                    handled.Add(request.NavigationTarget!);
                    if (handled.Count == 4) completed.TrySetResult(true);
                });
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(handled.SequenceEqual(new[] { "video", "archive", "history", "all" }),
                    "Queued taskbar navigation changed arrival order.");
                RequireNavigation("all");
                Require(navigation.SelectedItem is NavigationViewItem selected && Equals(selected.Tag, "all"),
                    "The last queued shortcut did not remain selected.");
            });
        }
        finally
        {
            SelectNavigation(previous);
            await AwaitNavigationAsync();
        }
    }

    private async Task CheckNavigationMotionAsync()
    {
        var root = (Grid)Field("RootGrid")!;
        var navigation = (NavigationView)Field("NavView")!;
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        var previousExpanded = (bool)Field("_navigationExpanded")!;
        try
        {
            Check("The caption uses platform-supported styling through activation preference changes", () =>
            {
                var supportsCustomization = AppWindowTitleBar.IsCustomizationSupported();
                Require(_window!.ExtendsContentIntoTitleBar == supportsCustomization,
                    "The custom title bar did not follow the platform capability.");
                if (!supportsCustomization)
                {
                    var titleDragRegion = (Grid)Field("TitleDragRegion")!;
                    Require(titleDragRegion.Visibility == Visibility.Collapsed, "The app title duplicated the system caption.");
                    return;
                }
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    root.UpdateLayout();
                    Invoke("UpdateNavigationTitleBar");
                    var header = (Grid)Field("AppTitleBar")!;
                    var bar = _window.AppWindow.TitleBar;
                    var scale = root.XamlRoot?.RasterizationScale ?? 1;
                    Require(header.Margin.Left == bar.LeftInset / scale && header.Margin.Right == bar.RightInset / scale,
                        "The app header did not reserve space for the Windows caption buttons.");
                    return;
                }

                var previousTheme = root.RequestedTheme;
                var highContrast = typeof(FluentMotion).GetField("_highContrast", BindingFlags.Static | BindingFlags.NonPublic)!;
                var windowActive = typeof(FluentMotion).GetField("_windowActive", BindingFlags.Static | BindingFlags.NonPublic)!;
                var previousHighContrast = (bool)highContrast.GetValue(null)!;
                var previousWindowActive = (bool)windowActive.GetValue(null)!;
                try
                {
                    highContrast.SetValue(null, false);
                    foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    {
                        root.RequestedTheme = theme;
                        root.UpdateLayout();
                        FluentMotion.SetWindowActive(false);
                        FluentMotion.SetWindowActive(true);
                        Invoke("UpdateNavigationTitleBar");
                        var bar = _window!.AppWindow.TitleBar;
                        Require(bar.ButtonBackgroundColor?.A == 0 && bar.ButtonInactiveBackgroundColor?.A == 0,
                            "Caption backgrounds lost their transparent styling.");
                        var expected = theme == ElementTheme.Dark ? (byte)255 : (byte)0;
                        Require(bar.ButtonForegroundColor is { A: 255 } foreground &&
                            foreground.R == expected && foreground.G == expected && foreground.B == expected,
                            $"Caption foreground did not follow {theme}.");
                    }
                    highContrast.SetValue(null, true);
                    Invoke("UpdateNavigationTitleBar");
                    Require(_window!.AppWindow.TitleBar.ButtonForegroundColor is null,
                        "High contrast did not restore the system caption foreground.");
                }
                finally
                {
                    highContrast.SetValue(null, previousHighContrast);
                    root.RequestedTheme = previousTheme;
                    FluentMotion.SetWindowActive(previousWindowActive);
                    Invoke("UpdateNavigationTitleBar");
                }
            });

            await CheckAsync("Compact and expanded navigation retain localized accessible names in every language", async () =>
            {
                ResizeForCapture(1280, 900);
                foreach (var language in LocalizationService.LanguageOptions.Where(option => option.Key != "auto").Select(option => option.Key))
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    foreach (var expanded in new[] { false, true })
                    {
                        Invoke("SetNavigationExpanded", expanded, false);
                        root.UpdateLayout();
                        foreach (var (tag, key) in NavigationLabels)
                        {
                            var item = FindNavigationItem(tag);
                            var expected = LocalizationService.T(key);
                            Require(AutomationProperties.GetName(item) == expected,
                                $"{language}, expanded={expanded}: {tag} lost its full accessible name.");
                            Require(item.Content?.ToString() == expected,
                                $"{language}, expanded={expanded}: {tag} lost its complete navigation content label.");
                        }
                        Require(root.FlowDirection == (LocalizationService.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight),
                            $"{language}: navigation did not inherit the expected reading direction.");
                    }
                }
                await Task.Delay(300);
            });

            LocalizationService.SetLanguage("zh-Hans");
            Invoke("ApplyLocalization");
            await CheckAsync("The visible navigation toggle expands and collapses through its real button action", async () =>
            {
                ResizeForCapture(1280, 900);
                await Task.Delay(300);
                Invoke("SetNavigationExpanded", false, false);
                var toggle = (ToggleButton)Field("NavigationToggleButton")!;
                Require(IsVisibleWithin(toggle, root), "The navigation toggle is outside the visible window.");
                Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)), "The navigation toggle has no accessible action name.");
                var provider = (IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle);
                provider.Toggle();
                await Task.Delay(300);
                Require(Field("_navigationExpanded") is true && navigation.OpenPaneLength is >= 216 and <= 240,
                    "Invoking the visible toggle did not open the full navigation labels.");
                provider.Toggle();
                await Task.Delay(300);
                Require(Field("_navigationExpanded") is false && navigation.OpenPaneLength is >= 72 and <= 88,
                    "Invoking the visible toggle again did not restore the compact rail.");
                Invoke("SetNavigationExpanded", true, false);
                ResizeForCapture(720, 900);
                await Task.Delay(300);
                Require(Field("_navigationExpanded") is false && navigation.OpenPaneLength is >= 72 and <= 88,
                    "Shrinking the window retained a wide rail over the task content.");
            });
            await CheckAsync("Compact navigation reaches every conversion category and feature page", async () =>
            {
                Invoke("SetNavigationExpanded", false, false);
                ResizeForCapture(720, 900);
                await Task.Delay(300);
                Require(navigation.OpenPaneLength is >= 72 and <= 88,
                    "The compact rail no longer uses its intended narrow width.");
                foreach (var (tag, _) in NavigationLabels)
                {
                    var item = FindNavigationItem(tag);
                    Require(item.Visibility == Visibility.Visible, $"Compact navigation removed {tag}.");
                    Require(IsVisibleWithin(item, root), $"Compact navigation clipped the {tag} entry.");
                    RequireNavigationLabelBounds(item, compactLabels: true);
                    SelectNavigation(tag);
                    await AwaitNavigationAsync();
                    RequireNavigation(tag);
                    Require(ReferenceEquals(navigation.SelectedItem, item), $"{tag} lost its selected entry.");
                }
                SelectNavigation("all");
                await AwaitNavigationAsync();
            });

            await CheckAsync("Disc and archive entries stay directly reachable and selected through pane toggles", async () =>
            {
                ResizeForCapture(1280, 900);
                await Task.Delay(300);
                foreach (var tag in new[] { "disc", "archive" })
                {
                    var item = FindNavigationItem(tag);
                    Require(navigation.MenuItems.Contains(item) && item.MenuItems.Count == 0,
                        $"{tag} requires a submenu instead of direct main navigation.");
                    foreach (var expanded in new[] { false, true, false })
                    {
                        Invoke("SetNavigationExpanded", expanded, false);
                        root.UpdateLayout();
                        Require(IsVisibleWithin(item, root), $"{tag} was clipped after toggling the pane.");
                        RequireNavigationLabelBounds(item, compactLabels: !expanded);
                        if (!ReferenceEquals(navigation.SelectedItem, item)) SelectNavigation(tag);
                        await AwaitNavigationAsync();
                        RequireNavigation(tag);
                        Require(ReferenceEquals(navigation.SelectedItem, item), $"Toggling the pane lost the selected {tag} route.");
                        Require((bool)Field("_navigationExpanded")! == expanded,
                            $"Selecting {tag} unexpectedly changed the pane width.");
                    }
                }
                SelectNavigation("all");
                await AwaitNavigationAsync();
            });

            await CheckAsync("Navigation glyphs and label line boxes fit their actual presenters in normal, short, expanded and RTL layouts", async () =>
            {
                foreach (var language in new[] { "zh-Hans", "ar-SA" })
                {
                    LocalizationService.SetLanguage(language);
                    Invoke("ApplyLocalization");
                    foreach (var (width, height, expanded) in new[] { (720, 900, false), (960, 560, false), (1280, 900, true) })
                    {
                        ResizeForCapture(width, height);
                        await Task.Delay(100);
                        Invoke("SetNavigationExpanded", expanded, false);
                        root.UpdateLayout();
                        await Task.Delay(100);
                        foreach (var (tag, _) in NavigationLabels)
                            RequireNavigationLabelBounds(FindNavigationItem(tag), compactLabels: !expanded && height >= 640);
                    }
                }
                LocalizationService.SetLanguage("zh-Hans");
                Invoke("ApplyLocalization");
                Invoke("SetNavigationExpanded", false, false);
            });

            await CheckAsync("Rapid category selections during pane toggles settle on the final request", async () =>
            {
                ResizeForCapture(1280, 900);
                await Task.Delay(300);
                var transitions = new List<Task>();
                foreach (var (tag, expanded) in new[] { ("video", true), ("disc", false), ("archive", true), ("image", true), ("settings", false), ("history", true), ("video", false), ("all", false) })
                {
                    Invoke("SetNavigationExpanded", expanded, true);
                    SelectNavigation(tag);
                    transitions.Add((Task)Field("_navigationTransition")!);
                }
                await Task.WhenAll(transitions).WaitAsync(TimeSpan.FromSeconds(3));
                await AwaitNavigationAsync();
                RequireNavigation("all");
                Require(Field("_navigationExpanded") is false, "An earlier expansion request won after the final collapse.");
                Require(ReferenceEquals(navigation.SelectedItem, FindNavigationItem("all")), "Rapid selection left a stale selected entry.");
                var host = (FrameworkElement)Field("TaskContentHost")!;
                Require(ElementCompositionPreview.GetElementVisual(host).Opacity == 1,
                    "Rapid navigation retained an invisible content surface.");
            });

            await CheckAsync("Short windows keep disc, archive, history and settings reachable without obscuring the content", async () =>
            {
                foreach (var height in new[] { 560, 640, 680, 720 })
                {
                    ResizeForCapture(960, height);
                    await Task.Delay(100);
                    foreach (var (tag, _) in NavigationLabels)
                        Require(IsVisibleWithin(FindNavigationItem(tag), root), $"The {height}px viewport clipped the {tag} entry.");
                    foreach (var tag in new[] { "disc", "archive", "history", "settings" })
                    {
                        var item = FindNavigationItem(tag);
                        SelectNavigation(tag);
                        await AwaitNavigationAsync();
                        RequireNavigation(tag);
                    }
                }
                SelectNavigation("all");
                await AwaitNavigationAsync();
            });

            ResizeForCapture(1280, 900);
            await Task.Delay(300);
            await CheckAdaptiveRepositionAsync(root);
        }
        finally
        {
            LocalizationService.SetLanguage(previousLanguage);
            Invoke("ApplyLocalization");
            ResizeForCapture(1280, 900);
            root.UpdateLayout();
            Invoke("SetNavigationExpanded", previousExpanded, false);
            SelectNavigation("all");
            await AwaitNavigationAsync();
        }
    }

    private NavigationViewItem FindNavigationItem(string tag)
    {
        var navigation = (NavigationView)Field("NavView")!;
        return navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>()
            .SelectMany(FlattenNavigationItem).Single(item => Equals(item.Tag, tag));
    }

    private static IEnumerable<NavigationViewItem> FlattenNavigationItem(NavigationViewItem item)
    {
        yield return item;
        foreach (var nested in item.MenuItems.OfType<NavigationViewItem>().SelectMany(FlattenNavigationItem))
            yield return nested;
    }

    private static void RequireNavigationLabelBounds(NavigationViewItem item, bool compactLabels = false)
    {
        var label = FindDescendants<FrameworkElement>(item).FirstOrDefault(element => element.Name == "NavigationLabelSurface");
        Require(label is { IsLoaded: true }, $"{item.Tag}: the visible navigation label was not realized.");
        var presenter = VisualTreeHelper.GetParent(label!) as DependencyObject;
        while (presenter is not null && presenter is not ContentPresenter)
            presenter = VisualTreeHelper.GetParent(presenter);
        Require(presenter is ContentPresenter, $"{item.Tag}: the navigation label has no native content presenter.");
        var contentPresenter = (ContentPresenter)presenter!;
        var itemBounds = new Rect(0, 0, item.ActualWidth, item.ActualHeight);
        var presenterBounds = contentPresenter.TransformToVisual(item).TransformBounds(
            new Rect(0, 0, contentPresenter.ActualWidth, contentPresenter.ActualHeight));
        // ActualWidth can exceed the allocated slot when fixed-size content overflows.
        // Include margins so the deliberate presenter compensation gets its real drawable area.
        var slot = LayoutInformation.GetLayoutSlot(contentPresenter);
        var margin = contentPresenter.Margin;
        var drawableSlot = new Rect(slot.X + margin.Left, slot.Y + margin.Top,
            Math.Max(0, slot.Width - margin.Left - margin.Right), Math.Max(0, slot.Height - margin.Top - margin.Bottom));
        var slotBounds = VisualTreeHelper.GetParent(contentPresenter) is UIElement parent
            ? parent.TransformToVisual(item).TransformBounds(drawableSlot) : presenterBounds;
        foreach (var element in FindDescendants<FrameworkElement>(label!).Where(element => element is FontIcon or TextBlock))
        {
            var bounds = element.TransformToVisual(item).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            RequireNavigationRectInside(bounds, itemBounds, $"{item.Tag}: {element.GetType().Name} overflows its navigation item");
            RequireNavigationRectInside(bounds, presenterBounds, $"{item.Tag}: {element.GetType().Name} overflows its presenter");
            RequireNavigationRectInside(bounds, slotBounds, $"{item.Tag}: {element.GetType().Name} is clipped by its presenter layout slot");
            for (DependencyObject? current = element; current is UIElement visual && !ReferenceEquals(current, item); current = VisualTreeHelper.GetParent(current))
                if (visual.Clip is RectangleGeometry clip)
                {
                    var clipBounds = visual.TransformToVisual(item).TransformBounds(clip.Rect);
                    RequireNavigationRectInside(bounds, clipBounds, $"{item.Tag}: {element.GetType().Name} is clipped by an ancestor");
                }
            if (element is TextBlock text)
            {
                Require(text.ActualHeight + .5 >= text.DesiredSize.Height && text.ActualHeight + .5 >= text.FontSize,
                    $"{item.Tag}: the text line box is shorter than its measured content.");
                if (compactLabels)
                    Require(text.ActualHeight >= 16 && text.TextWrapping == TextWrapping.NoWrap,
                        $"{item.Tag}: the compact label no longer has a full 16-pixel line box.");
            }
        }
    }

    private static void RequireNavigationRectInside(Rect content, Rect viewport, string message)
    {
        const double tolerance = .75;
        Require(content.Width > 0 && content.Height > 0 && content.Left >= viewport.Left - tolerance &&
            content.Top >= viewport.Top - tolerance && content.Right <= viewport.Right + tolerance && content.Bottom <= viewport.Bottom + tolerance,
            $"{message}: content={content}, drawable={viewport}.");
    }

    private async Task CaptureNavigationStatesAsync()
    {
        var navigation = (NavigationView)Field("NavView")!;
        var previousExpanded = (bool)Field("_navigationExpanded")!;
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            LocalizationService.SetLanguage("zh-Hans");
            Invoke("ApplyLocalization");
            ResizeForCapture(1280, 900);
            await Task.Delay(60);
            Invoke("SetNavigationExpanded", true, false);
            await CaptureAsync("nav-expanded-light", ElementTheme.Light, 1280, 900, "all");
            Invoke("SetNavigationExpanded", false, false);
            await CaptureAsync("nav-compact-dark", ElementTheme.Dark, 720, 900, "all");
            await CaptureAsync("nav-short-dark", ElementTheme.Dark, 960, 560, "all");
            ResizeForCapture(720, 900);
            Invoke("SetNavigationExpanded", false, false);
            await CaptureAsync("nav-disc-compact-dark", ElementTheme.Dark, 720, 900, "disc");
            await CaptureAsync("nav-archive-compact-dark", ElementTheme.Dark, 720, 900, "archive");
            ResizeForCapture(1280, 900);
            await Task.Delay(60);
            Invoke("SetNavigationExpanded", true, false);
            await CaptureAsync("nav-archive-expanded-light", ElementTheme.Light, 1280, 900, "archive");
            LocalizationService.SetLanguage("ar-SA");
            Invoke("ApplyLocalization");
            ResizeForCapture(1280, 900);
            await Task.Delay(60);
            Invoke("SetNavigationExpanded", true, false);
            await CaptureAsync("nav-expanded-ar-SA-dark", ElementTheme.Dark, 1280, 900, "all");
        }
        finally
        {
            LocalizationService.SetLanguage(previousLanguage);
            Invoke("ApplyLocalization");
            Invoke("SetNavigationExpanded", previousExpanded, false);
        }
    }

    private async Task CheckAdaptiveRepositionAsync(Grid root)
    {
        var fixture = new AdaptiveGrid
        {
            Width = 1000, Height = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CompactThreshold = 320, WideThreshold = 640,
            MediumColumns = "*", CompactColumns = "*", AutoStackCompact = true
        };
        fixture.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fixture.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fixture.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fixture.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var children = Enumerable.Range(0, 3).Select(index => new Border
        {
            Height = 48, Child = new TextBlock { Text = "Isolated resize fixture " + index }
        }).ToArray();
        Grid.SetColumn(children[1], 1);
        Grid.SetRow(children[2], 1);
        Grid.SetColumnSpan(children[2], 2);
        for (var index = 0; index < children.Length; index++)
        {
            AdaptiveGrid.SetMediumCell(children[index], $"{index},0");
            fixture.Children.Add(children[index]);
        }
        root.Children.Add(fixture);
        try
        {
            root.UpdateLayout();
            await Task.Delay(60);
            await CheckAsync("Loaded responsive grids animate a breakpoint and settle on exact layout cells", async () =>
            {
                Require(fixture.IsLoaded && fixture.ActualWidth == 1000, "The resize fixture was not truly loaded at its initial width.");
                var before = RepositionStartCount();
                fixture.Width = 500;
                root.UpdateLayout();
                await Task.Delay(30);
                Require(RepositionStartCount() > before && ActiveRepositionCount() > 0,
                    $"The real wide-to-medium layout change did not retain active Composition reflow. Starts={before}->{RepositionStartCount()}, active={ActiveRepositionCount()}, enabled={FluentMotion.IsEnabled}, width={fixture.ActualWidth}.");
                await Task.Delay(230);
                RequireStackedCells(fixture, children, "Medium");
                fixture.Width = 260;
                root.UpdateLayout();
                await Task.Delay(230);
                RequireStackedCells(fixture, children, "Compact");
                fixture.Width = 1000;
                root.UpdateLayout();
                await Task.Delay(230);
                Require(Grid.GetColumn(children[1]) == 1 && Grid.GetRow(children[2]) == 1 && Grid.GetColumnSpan(children[2]) == 2,
                    "Returning to wide did not restore the original cells and span.");
                RequireSettledReflow(children);
            });

            await CheckAsync("Continuous resizing within one layout mode starts no extra reflow animations", async () =>
            {
                var before = RepositionStartCount();
                foreach (var width in new[] { 980d, 920, 860, 800, 760 })
                {
                    fixture.Width = width;
                    root.UpdateLayout();
                    await Task.Delay(20);
                }
                Require(RepositionStartCount() == before,
                    "Ordinary continuous resizing replayed breakpoint animation within the same mode.");
                RequireSettledReflow(children);
            });

            await CheckAsync("Interrupted breakpoint changes settle without translation drift", async () =>
            {
                foreach (var width in new[] { 500d, 1000, 260, 1000, 500, 260, 1000 })
                {
                    fixture.Width = width;
                    root.UpdateLayout();
                    await Task.Delay(20);
                }
                await Task.Delay(240);
                Require(Grid.GetColumn(children[1]) == 1 && Grid.GetColumnSpan(children[2]) == 2,
                    "A stale breakpoint won after rapid resizing.");
                RequireSettledReflow(children);
            });

            await CheckAsync("Disabling motion mid-reflow clears active animation and pending layout state", async () =>
            {
                fixture.Width = 1000;
                root.UpdateLayout();
                await Task.Delay(300);
                var preference = typeof(FluentMotion).GetField("_animationsEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;
                var previous = (bool)preference.GetValue(null)!;
                try
                {
                    fixture.Width = 500;
                    root.UpdateLayout();
                    await Task.Delay(30);
                    Require(ActiveRepositionCount() > 0, $"No reflow was active before disabling motion. Starts={RepositionStartCount()}, enabled={FluentMotion.IsEnabled}, width={fixture.ActualWidth}.");
                    preference.SetValue(null, false); // Isolated process state; never writes Windows preferences.
                    FluentMotion.SetWindowActive(false);
                    FluentMotion.SetWindowActive(true);
                    Require(ActiveRepositionCount() == 0, "Reduced motion retained active reflow.");
                    RequireSettledReflow(children);
                    var before = RepositionStartCount();
                    fixture.Width = 1000;
                    root.UpdateLayout();
                    await Task.Delay(30);
                    Require(RepositionStartCount() == before, "Reduced motion still started breakpoint animation.");
                    Require(Grid.GetColumn(children[1]) == 1, "Reduced motion did not apply the final wide layout.");
                    RequirePendingReflowEmpty(fixture);
                    RequireSettledReflow(children);
                }
                finally
                {
                    preference.SetValue(null, previous);
                    FluentMotion.SetWindowActive(false);
                    FluentMotion.SetWindowActive(true);
                }
            });

            await CheckAsync("Unloading during reflow releases targets and pending layout work", async () =>
            {
                fixture.Width = 1000;
                root.UpdateLayout();
                await Task.Delay(300);
                fixture.Width = 500;
                root.UpdateLayout();
                await Task.Delay(30);
                Require(ActiveRepositionCount() > 0, "No loaded reflow was active before removing its target.");
                root.Children.Remove(fixture);
                root.UpdateLayout();
                await Task.Delay(40);
                Require(!fixture.IsLoaded, "The detached test grid remained loaded.");
                Require(ActiveRepositionCount() == 0, "Removing the grid retained running reflow targets.");
                RequirePendingReflowEmpty(fixture);
                RequireSettledReflow(children);
            });
        }
        finally
        {
            root.Children.Remove(fixture);
            foreach (var child in children) FluentMotion.Reset(child);
            root.UpdateLayout();
        }
    }

    private static long RepositionStartCount() => Convert.ToInt64(typeof(FluentMotion)
        .GetProperty("RepositionStartCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null));

    private static int ActiveRepositionCount() => Convert.ToInt32(typeof(FluentMotion)
        .GetProperty("ActiveRepositionCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null));

    private static void RequireStackedCells(AdaptiveGrid grid, Border[] children, string mode)
    {
        Require(grid.ColumnDefinitions.Count == 1, $"{mode} retained wide column definitions.");
        for (var index = 0; index < children.Length; index++)
            Require(Grid.GetRow(children[index]) == index && Grid.GetColumn(children[index]) == 0 && Grid.GetColumnSpan(children[index]) == 1,
                $"{mode}: child {index} did not reach its final cell.");
        RequireSettledReflow(children);
        RequirePendingReflowEmpty(grid);
    }

    private static void RequireSettledReflow(IEnumerable<FrameworkElement> children)
    {
        foreach (var child in children)
        {
            var visual = ElementCompositionPreview.GetElementVisual(child);
            visual.Properties.TryGetVector3("Translation", out var translation);
            Require(Vector3.Distance(translation, Vector3.Zero) < .001,
                "A settled reflow retained translation drift.");
            Require(visual.Opacity == 1 && Vector3.Distance(visual.Scale, Vector3.One) < .001,
                "A layout transition changed content opacity or scale.");
        }
    }

    private static void RequirePendingReflowEmpty(AdaptiveGrid grid)
    {
        var pending = typeof(AdaptiveGrid).GetField("_pendingRepositionOrigins", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(grid);
        Require(pending is null || Convert.ToInt32(pending.GetType().GetProperty("Count")!.GetValue(pending)) == 0,
            "The settled or unloaded grid retained pending reflow origins.");
    }
}
