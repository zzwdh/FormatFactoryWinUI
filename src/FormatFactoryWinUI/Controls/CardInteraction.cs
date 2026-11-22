using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FormatFactoryWinUI.Controls;

/// <summary>
/// Subtle pointer and keyboard-within feedback, drawn over a card without changing
/// its layout or replacing its theme brushes. Native child focus visuals remain.
/// </summary>
public static class CardInteraction
{
    private static readonly ConditionalWeakTable<Border, Interaction> Interactions = new();

    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(CardInteraction), new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);

    private static void OnEnableChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Border border) return;
        if ((bool)args.NewValue)
        {
            Interactions.GetValue(border, element => new Interaction(element)).Enable();
        }
        else if (Interactions.TryGetValue(border, out var interaction))
        {
            interaction.Disable();
            Interactions.Remove(border);
        }
    }

    private sealed class Interaction(Border border)
    {
        private readonly Border _border = border;
        private bool _enabled;
        private bool _attached;
        private bool _hovered;
        private bool _keyboardFocus;
        private ShapeVisual? _visual;
        private CompositionRoundedRectangleGeometry? _geometry;
        private CompositionSpriteShape? _shape;
        private CompositionColorBrush? _fill;
        private CompositionColorBrush? _stroke;
        private float? _targetOpacity;
        private Color? _targetFill;
        private Color? _targetStroke;

        public void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            _border.Loaded += OnLoaded;
            _border.Unloaded += OnUnloaded;
            if (_border.IsLoaded) Attach();
        }

        public void Disable()
        {
            _enabled = false;
            _border.Loaded -= OnLoaded;
            _border.Unloaded -= OnUnloaded;
            Detach();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();

        private void Attach()
        {
            if (_attached) return;
            _attached = true;
            _border.PointerEntered += OnPointerEntered;
            _border.PointerExited += OnPointerExited;
            _border.PointerCanceled += OnPointerCanceled;
            _border.GotFocus += OnFocusChanged;
            _border.LostFocus += OnFocusChanged;
            _border.SizeChanged += OnSizeChanged;
            _border.ActualThemeChanged += OnThemeChanged;
            FluentMotion.PreferencesChanged += OnPreferencesChanged;
            UpdateKeyboardFocus();
            UpdateVisual(false);
        }

        private void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _hovered = false;
            _keyboardFocus = false;
            _border.PointerEntered -= OnPointerEntered;
            _border.PointerExited -= OnPointerExited;
            _border.PointerCanceled -= OnPointerCanceled;
            _border.GotFocus -= OnFocusChanged;
            _border.LostFocus -= OnFocusChanged;
            _border.SizeChanged -= OnSizeChanged;
            _border.ActualThemeChanged -= OnThemeChanged;
            FluentMotion.PreferencesChanged -= OnPreferencesChanged;
            try
            {
                _visual?.StopAnimation("Opacity");
                _fill?.StopAnimation("Color");
                _stroke?.StopAnimation("Color");
                if (_visual is not null && ReferenceEquals(ElementCompositionPreview.GetElementChildVisual(_border), _visual))
                {
                    ElementCompositionPreview.SetElementChildVisual(_border, null);
                }
            }
            catch (Exception exception) when (IsVisualUnavailable(exception)) { }
            DisposeSafely(_visual);
            DisposeSafely(_shape);
            DisposeSafely(_geometry);
            DisposeSafely(_fill);
            DisposeSafely(_stroke);
            _visual = null;
            _shape = null;
            _geometry = null;
            _fill = null;
            _stroke = null;
            _targetOpacity = null;
            _targetFill = null;
            _targetStroke = null;
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
        {
            _hovered = true;
            UpdateVisual(true);
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs args)
        {
            _hovered = false;
            UpdateVisual(true);
        }

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
        {
            _hovered = false;
            UpdateVisual(false);
        }

        private void OnFocusChanged(object sender, RoutedEventArgs args)
        {
            // LostFocus can precede another child gaining focus within the same card.
            _border.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_attached) return;
                UpdateKeyboardFocus();
                UpdateVisual(true);
            });
        }

        private void UpdateKeyboardFocus()
        {
            _keyboardFocus = false;
            if (_border.XamlRoot is null) return;
            var focused = FocusManager.GetFocusedElement(_border.XamlRoot);
            if (focused is not Control { FocusState: FocusState.Keyboard } control) return;
            for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, _border))
                {
                    _keyboardFocus = true;
                    return;
                }
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateSize();
        private void OnThemeChanged(FrameworkElement sender, object args) => UpdateVisual(false);
        private void OnPreferencesChanged(object? sender, EventArgs args) => UpdateVisual(false);

        private bool EnsureVisual()
        {
            if (_visual is not null) return true;
            // Do not take over another behavior's composition child.
            if (ElementCompositionPreview.GetElementChildVisual(_border) is not null) return false;
            var compositor = ElementCompositionPreview.GetElementVisual(_border).Compositor;
            _geometry = compositor.CreateRoundedRectangleGeometry();
            _geometry.Offset = new Vector2(0.5f);
            _fill = compositor.CreateColorBrush();
            _stroke = compositor.CreateColorBrush();
            _shape = compositor.CreateSpriteShape(_geometry);
            _shape.FillBrush = _fill;
            _shape.StrokeBrush = _stroke;
            _shape.StrokeThickness = 1;
            _visual = compositor.CreateShapeVisual();
            _visual.Opacity = 0;
            _visual.Shapes.Add(_shape);
            ElementCompositionPreview.SetElementChildVisual(_border, _visual);
            UpdateSize();
            return true;
        }

        private void UpdateSize()
        {
            if (_visual is null || _geometry is null) return;
            _visual.Size = new Vector2((float)_border.ActualWidth, (float)_border.ActualHeight);
            _geometry.Size = Vector2.Max(Vector2.Zero, _visual.Size - Vector2.One);
            _geometry.CornerRadius = new Vector2((float)Math.Max(0, _border.CornerRadius.TopLeft - 0.5));
        }

        private void UpdateVisual(bool animate)
        {
            if (!_attached || !_border.IsLoaded) return;
            try
            {
                if (!EnsureVisual() || _visual is null || _fill is null || _stroke is null) return;
                var dark = _border.ActualTheme == ElementTheme.Dark;
                var channel = dark ? (byte)255 : (byte)0;
                var fill = Color.FromArgb(_hovered ? (dark ? (byte)7 : (byte)3) : (byte)0, channel, channel, channel);
                var stroke = Color.FromArgb(_keyboardFocus ? (byte)120 : (dark ? (byte)42 : (byte)28), channel, channel, channel);
                // System contrast brushes and native focus rectangles provide the
                // complete contrast-mode experience; decorative overlays stay hidden.
                var opacity = FluentMotion.HighContrast || (!_hovered && !_keyboardFocus) ? 0f : 1f;
                if (animate && FluentMotion.IsEnabled)
                {
                    var entering = opacity > 0;
                    var duration = entering ? FluentMotion.FastDurationMs : FluentMotion.ContentDurationMs;
                    using var easing = _visual.Compositor.CreateCubicBezierEasingFunction(
                        entering ? new Vector2(0.1f, 0.9f) : new Vector2(0.7f, 0),
                        entering ? new Vector2(0.2f, 1) : new Vector2(1, 0.5f));
                    if (_targetOpacity != opacity)
                    {
                        using var animation = _visual.Compositor.CreateScalarKeyFrameAnimation();
                        // Replace a running animation without stopping it first: the
                        // compositor samples its current value for a smooth reversal.
                        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
                        animation.InsertKeyFrame(1, opacity, easing);
                        animation.Duration = TimeSpan.FromMilliseconds(duration);
                        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                        _visual.StartAnimation("Opacity", animation);
                        _visual.Opacity = opacity;
                    }
                    AnimateColor(_fill, fill, _targetFill, duration, easing);
                    AnimateColor(_stroke, stroke, _targetStroke, duration, easing);
                }
                else
                {
                    // Theme, contrast, animation-policy and window-activity changes
                    // settle every property immediately, including brush animations.
                    _visual.StopAnimation("Opacity");
                    _fill.StopAnimation("Color");
                    _stroke.StopAnimation("Color");
                    _visual.Opacity = opacity;
                    _fill.Color = fill;
                    _stroke.Color = stroke;
                }
                _targetOpacity = opacity;
                _targetFill = fill;
                _targetStroke = stroke;
            }
            catch (Exception exception) when (IsVisualUnavailable(exception)) { }
        }

        private static void AnimateColor(CompositionColorBrush brush, Color color, Color? previousTarget,
            int durationMs, CompositionEasingFunction easing)
        {
            if (previousTarget == color) return;
            using var animation = brush.Compositor.CreateColorKeyFrameAnimation();
            animation.InsertExpressionKeyFrame(0, "this.StartingValue");
            animation.InsertKeyFrame(1, color, easing);
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            brush.StartAnimation("Color", animation);
            brush.Color = color;
        }

        private static void DisposeSafely(IDisposable? resource)
        {
            try { resource?.Dispose(); }
            catch (Exception exception) when (IsVisualUnavailable(exception)) { }
        }

        private static bool IsVisualUnavailable(Exception exception) =>
            exception is COMException or ObjectDisposedException or InvalidOperationException;
    }
}
