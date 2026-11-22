using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;

namespace FormatFactoryWinUI.Controls;

/// <summary>A themed, accessible task state. Recycled cards never replay a completion.</summary>
public sealed class TaskStatusBadge : UserControl
{
    // The small style dictionary is parsed once on the UI thread and shared. Each
    // realized task only creates one icon; theme references still resolve per icon.
    private static readonly Lazy<ResourceDictionary> IconStyles = new(() => (ResourceDictionary)XamlReader.Load("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Style x:Key="Waiting" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xE823;"/><Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/></Style>
            <Style x:Key="Probing" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xE721;"/><Setter Property="Foreground" Value="{ThemeResource AccentTextFillColorPrimaryBrush}"/></Style>
            <Style x:Key="Running" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xE768;"/><Setter Property="Foreground" Value="{ThemeResource AccentTextFillColorPrimaryBrush}"/></Style>
            <Style x:Key="Paused" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xE769;"/><Setter Property="Foreground" Value="{ThemeResource SystemFillColorCautionBrush}"/></Style>
            <Style x:Key="Completed" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xE73E;"/><Setter Property="Foreground" Value="{ThemeResource SystemFillColorSuccessBrush}"/></Style>
            <Style x:Key="Failed" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xEA39;"/><Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}"/></Style>
            <Style x:Key="Cancelled" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xE711;"/><Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/></Style>
            <Style x:Key="Blocked" TargetType="FontIcon"><Setter Property="Glyph" Value="&#xE72E;"/><Setter Property="Foreground" Value="{ThemeResource SystemFillColorCautionBrush}"/></Style>
        </ResourceDictionary>
        """));

    private readonly FontIcon _icon;
    private readonly TextBlock _label;
    private bool _ready;
    private object? _presentedContext;
    private int _contextVersion;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ConversionState), typeof(TaskStatusBadge),
        new PropertyMetadata(ConversionState.Waiting, OnStateChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TaskStatusBadge),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public TaskStatusBadge()
    {
        _icon = new FontIcon { Name = "StatusIcon", FontSize = 12 };
        _label = new TextBlock { Name = "StatusLabel", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var layout = new Grid { ColumnSpacing = 6 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_label, 1);
        layout.Children.Add(_icon);
        layout.Children.Add(_label);
        AutomationProperties.SetAccessibilityView(_icon, AccessibilityView.Raw);
        _label.SetBinding(TextBlock.FontWeightProperty, new Binding { Source = this, Path = new PropertyPath(nameof(FontWeight)) });
        _label.SetBinding(TextBlock.FontSizeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(FontSize)) });
        _label.SetBinding(TextBlock.ForegroundProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        AutomationProperties.SetLiveSetting(_label, AutomationLiveSetting.Polite);
        Content = layout;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        UpdateState(false);
    }

    public ConversionState State
    {
        get => (ConversionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var badge = (TaskStatusBadge)sender;
        badge.UpdateState(badge.IsLoaded && badge._ready && ReferenceEquals(badge._presentedContext, badge.DataContext));
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var badge = (TaskStatusBadge)sender;
        badge._label.Text = (string?)args.NewValue ?? string.Empty;
        if (badge.IsLoaded && badge._ready)
        {
            FrameworkElementAutomationPeer.FromElement(badge._label)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void UpdateState(bool animate)
    {
        if (_icon.IsLoaded) FluentMotion.Reset(_icon);
        var key = Enum.IsDefined(State) ? State.ToString() : nameof(ConversionState.Waiting);
        _icon.Style = (Style)IconStyles.Value[key];
        if (animate && FluentMotion.IsEnabled) FluentMotion.Pulse(_icon);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _presentedContext = DataContext;
        _ready = true;
        FluentMotion.PreferencesChanged += OnPreferencesChanged;
        UpdateState(false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _ready = false;
        _contextVersion++;
        FluentMotion.PreferencesChanged -= OnPreferencesChanged;
        FluentMotion.Reset(_icon);
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        _ready = false;
        _presentedContext = args.NewValue;
        var version = ++_contextVersion;
        UpdateState(false);
        // Arm only after the current recycled-container binding batch has settled.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _contextVersion && IsLoaded) _ready = true;
        });
    }

    private void OnPreferencesChanged(object? sender, EventArgs args)
    {
        if (!FluentMotion.IsEnabled) FluentMotion.Reset(_icon);
    }
}
