using System.Runtime.InteropServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace FormatFactoryWinUI.Controls;

/// <summary>
/// Preserves the native range/automation value and its independent determinate
/// transition. No timer, predicted progress, or per-frame layout updates are used.
/// </summary>
public sealed class TaskProgressBar : ProgressBar
{
    private VisualStateGroup? _commonStates;
    private Rectangle[] _indicators = [];
    private List<VisualTransition> _nativeTransitions = [];
    private readonly List<(UIElement Element, long Token)> _visibilitySubscriptions = [];
    private bool? _transitionsEnabled;
    private bool _ready;
    private object? _presentedContext;
    private int _contextVersion;
    private bool _colorRefreshQueued;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(TaskProgressBar),
        new PropertyMetadata(0d, OnProgressChanged));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ConversionState), typeof(TaskProgressBar),
        new PropertyMetadata(ConversionState.Waiting, OnStateChanged));

    public TaskProgressBar()
    {
        DefaultStyleKey = typeof(ProgressBar);
        Minimum = 0;
        Maximum = 100;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public ConversionState State
    {
        get => (ConversionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private double ReportedValue => double.IsFinite(Progress) ? Math.Clamp(Progress, Minimum, Maximum) : Minimum;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _commonStates = GetTemplateChild("LayoutRoot") is FrameworkElement root
            ? VisualStateManager.GetVisualStateGroups(root).FirstOrDefault(group => group.Name == "CommonStates")
            : null;
        _nativeTransitions = _commonStates?.Transitions.ToList() ?? [];
        _indicators = new[] { "DeterminateProgressBarIndicator", "IndeterminateProgressBarIndicator", "IndeterminateProgressBarIndicator2" }
            .Select(name => GetTemplateChild(name)).OfType<Rectangle>().ToArray();
        _transitionsEnabled = null;
        ConfigureTransitions(false);
        SetReportedValue(false);
        RestoreIndicatorForeground();
    }

    private static void OnProgressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var bar = (TaskProgressBar)sender;
        bar.SetReportedValue(bar._ready && ReferenceEquals(bar._presentedContext, bar.DataContext) &&
            bar.State == ConversionState.Running && bar.IsVisibleInTree() && FluentMotion.IsEnabled);
    }

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var bar = (TaskProgressBar)sender;
        bar.ConfigureTransitions(false);
        bar.UpdateState();
        bar.SetReportedValue(false);
        bar.RestoreIndicatorForeground();
    }

    private void RestoreIndicatorForeground()
    {
        if (ShowError || ShowPaused) return;
        // Native error/pause animations can leave a cloned Fill with the color
        // sampled before a theme change. Restore the entire brush binding when
        // returning to a normal state; do not rewrite colors on progress frames.
        foreach (var indicator in _indicators)
            indicator.SetBinding(Shape.FillProperty, new Binding
            {
                Source = this,
                Path = new PropertyPath(nameof(Foreground))
            });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ConfigureTransitions(false);
        UpdateState();
        SetReportedValue(false);
        RestoreIndicatorForeground();
        QueueThemeColorRefresh();
    }

    private void QueueThemeColorRefresh()
    {
        if (_colorRefreshQueued) return;
        _colorRefreshQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _colorRefreshQueued = false;
            if (!IsLoaded || _commonStates is null) return;
            // Theme bindings settle after ActualThemeChanged. Leaving the old
            // native color state first releases its animated brush clone, then
            // the current error/pause state can target the new theme's brush.
            VisualStateManager.GoToState(this, "Determinate", false);
            foreach (var indicator in _indicators)
                indicator.SetBinding(Shape.FillProperty, new Binding
                {
                    Source = this,
                    Path = new PropertyPath(nameof(Foreground))
                });
            var state = IsIndeterminate
                ? ShowError ? "IndeterminateError" : ShowPaused ? "IndeterminatePaused" : "Indeterminate"
                : ShowError ? "Error" : ShowPaused ? "Paused" : "Determinate";
            VisualStateManager.GoToState(this, state, false);
            FinishNativeStoryboards();
        });
    }

    private void UpdateState()
    {
        ShowError = State == ConversionState.Failed;
        ShowPaused = State is ConversionState.Paused or ConversionState.Cancelled or ConversionState.Blocked;
        IsIndeterminate = State == ConversionState.Probing && IsVisibleInTree() && FluentMotion.IsEnabled;
    }

    private void SetReportedValue(bool animate)
    {
        var target = ReportedValue;
        // The native Updating -> Determinate RepositionThemeAnimation handles
        // forward movement. Retrying or recycling must never animate backwards.
        ConfigureTransitions(animate && target > Value);
        Value = target;
        if (!animate) FinishNativeStoryboards();
    }

    private void ConfigureTransitions(bool enabled)
    {
        if (_commonStates is null || _transitionsEnabled == enabled) return;
        _transitionsEnabled = enabled;
        if (!enabled) FinishNativeStoryboards();
        _commonStates.Transitions.Clear();
        if (enabled)
        {
            foreach (var transition in _nativeTransitions) _commonStates.Transitions.Add(transition);
        }
    }

    private void FinishNativeStoryboards()
    {
        foreach (var transition in _nativeTransitions) FinishStoryboard(transition.Storyboard);
        // Error and pause use short color storyboards in the native template.
        // Settle these too when a card is unloaded or system motion is disabled.
        if (!IsIndeterminate) FinishStoryboard(_commonStates?.CurrentState?.Storyboard);
    }

    private static void FinishStoryboard(Storyboard? storyboard)
    {
        if (storyboard is null) return;
        try
        {
            if (storyboard.GetCurrentState() == ClockState.Active) storyboard.SkipToFill();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // A native VisualStateManager storyboard may not have begun yet.
        }
    }

    private bool IsVisibleInTree()
    {
        if (!IsLoaded || XamlRoot is null) return false;
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        }
        return true;
    }

    private void SubscribeToVisibility()
    {
        UnsubscribeFromVisibility();
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element)
            {
                _visibilitySubscriptions.Add((element, element.RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged)));
            }
        }
    }

    private void UnsubscribeFromVisibility()
    {
        foreach (var (element, token) in _visibilitySubscriptions)
        {
            element.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        }
        _visibilitySubscriptions.Clear();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        ConfigureTransitions(false);
        UpdateState();
        SetReportedValue(false);
        RestoreIndicatorForeground();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _ready = true;
        _presentedContext = DataContext;
        FluentMotion.PreferencesChanged += OnPreferencesChanged;
        SubscribeToVisibility();
        UpdateState();
        SetReportedValue(false);
        RestoreIndicatorForeground();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _ready = false;
        _contextVersion++;
        FluentMotion.PreferencesChanged -= OnPreferencesChanged;
        UnsubscribeFromVisibility();
        ConfigureTransitions(false);
        IsIndeterminate = false;
        SetReportedValue(false);
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        _ready = false;
        _presentedContext = args.NewValue;
        var version = ++_contextVersion;
        SetReportedValue(false);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _contextVersion || !IsLoaded) return;
            SetReportedValue(false);
            RestoreIndicatorForeground();
            _ready = true;
        });
    }

    private void OnPreferencesChanged(object? sender, EventArgs args)
    {
        ConfigureTransitions(false);
        UpdateState();
        SetReportedValue(false);
        RestoreIndicatorForeground();
        QueueThemeColorRefresh();
    }
}
