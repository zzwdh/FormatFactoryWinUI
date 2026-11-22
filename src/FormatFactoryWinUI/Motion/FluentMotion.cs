using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace FormatFactoryWinUI.Motion;

/// <summary>
/// Shared, interruptible motion for the main window. Call on its UI thread, initialize
/// after creating the window, and call Shutdown when that window closes.
/// </summary>
public static class FluentMotion
{
    public const int FastDurationMs = 83;
    public const int ContentDurationMs = 167;
    public const int PageDurationMs = 250;

    /// <summary>Top-level destinations stay anchored while their content fades in.</summary>
    public static void FadeIn(FrameworkElement element, int durationMs = ContentDurationMs) =>
        Entrance(element, durationMs, distance: 0);

    private static readonly Dictionary<FrameworkElement, RunningMotion> Running = [];
    private static DispatcherQueue? _dispatcher;
    private static UISettings? _uiSettings;
    private static AccessibilitySettings? _accessibilitySettings;
    private static bool _initialized;
    private static bool _shutdown;
    private static bool _animationsEnabled;
    private static bool _highContrast;
    private static bool _windowActive = true;
    private static int _initializationVersion;

    /// <summary>Raised on the window's UI thread, including changes to window activity.</summary>
    public static event EventHandler? PreferencesChanged;
    internal static event EventHandler? ShuttingDown;

    public static bool AnimationsEnabled => _animationsEnabled;
    public static bool HighContrast => _highContrast;
    public static bool IsEnabled => _initialized && !_shutdown && _animationsEnabled && !_highContrast && _windowActive;

    // UI-thread diagnostics also let the smoke host distinguish a breakpoint
    // transition from an ordinary resize without waiting for a screenshot.
    internal static int ActiveRepositionCount => Running.Values.Count(motion => motion.Kind == MotionKind.Reposition);
    internal static long RepositionStartCount { get; private set; }

    public static void Initialize(DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_initialized) return;
        _dispatcher = dispatcher;
        _shutdown = false;
        _initialized = true;
        _initializationVersion++;
        try
        {
            _uiSettings = new UISettings();
            _accessibilitySettings = new AccessibilitySettings();
            _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
            ReadPreferences();
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            // An unavailable settings source should never enable decorative motion.
            _animationsEnabled = false;
            Debug.WriteLine($"Motion preferences unavailable: {exception.Message}");
        }
    }

    public static void Entrance(
        FrameworkElement element,
        int durationMs = PageDurationMs,
        float distance = 8,
        int delayMs = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureInitialized(element);
        var continuing = Running.TryGetValue(element, out var previous) && previous.Kind == MotionKind.Presentation;
        Stop(element, preserveVisualState: continuing);
        if (!TryGetVisual(element, out var visual)) return;
        if (!IsEnabled || durationMs <= 0)
        {
            SetFinalState(visual, 1);
            return;
        }

        Start(element, visual, 1, durationMs, delayMs, (compositor, easing) =>
        {
            if (!continuing)
            {
                visual.Opacity = 0;
                visual.Properties.InsertVector3("Translation", new Vector3(0, distance, 0));
            }
            else if (visual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded)
            {
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            using var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertExpressionKeyFrame(0, "this.StartingValue");
            opacity.InsertKeyFrame(1, 1, easing);
            Configure(opacity, durationMs, delayMs);
            visual.StartAnimation("Opacity", opacity);

            using var translation = compositor.CreateVector3KeyFrameAnimation();
            translation.InsertExpressionKeyFrame(0, "this.StartingValue");
            translation.InsertKeyFrame(1, Vector3.Zero, easing);
            Configure(translation, durationMs, delayMs);
            visual.StartAnimation("Translation", translation);
        });
    }

    /// <summary>
    /// Completes when the fade finishes or is interrupted. Callers changing pages should
    /// check their navigation version after awaiting, so an older request cannot win.
    /// </summary>
    public static Task FadeOutAsync(FrameworkElement element, int durationMs = FastDurationMs)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureInitialized(element);
        // A rapid reverse must continue from the presented value rather than first
        // settling an interrupted entrance at full opacity.
        Stop(element, preserveVisualState: true);
        if (!TryGetVisual(element, out var visual)) return Task.CompletedTask;
        if (!IsEnabled || durationMs <= 0)
        {
            SetFinalState(visual, 0);
            return Task.CompletedTask;
        }

        return Start(element, visual, 0, durationMs, 0, (compositor, easing) =>
        {
            using var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertExpressionKeyFrame(0, "this.StartingValue");
            opacity.InsertKeyFrame(1, 0, easing);
            Configure(opacity, durationMs, 0);
            visual.StartAnimation("Opacity", opacity);
        }, easingKind: EasingKind.Exit);
    }

    public static void Reset(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        Stop(element);
        if (TryGetVisual(element, out var visual)) SetFinalState(visual, 1);
    }

    /// <summary>A single quiet emphasis for an actual status change; never loops.</summary>
    public static void Pulse(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureInitialized(element);
        Stop(element);
        if (!TryGetVisual(element, out var visual)) return;
        SetFinalState(visual, 1);
        if (!IsEnabled) return;

        Start(element, visual, 1, ContentDurationMs, 0, (compositor, easing) =>
        {
            using var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0.65f);
            opacity.InsertKeyFrame(1, 1, easing);
            Configure(opacity, ContentDurationMs, 0);
            visual.StartAnimation("Opacity", opacity);
        });
    }

    /// <summary>
    /// Bridges an already arranged layout change using translation only. The supplied
    /// delta is the old layout position minus the new position in the same parent.
    /// A replacement starts from the compositor's current value, so rapid breakpoint
    /// changes do not first snap an in-flight transition to its destination.
    /// </summary>
    public static bool Reposition(FrameworkElement element, Vector3 delta, int durationMs = ContentDurationMs)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureInitialized(element);
        if (!IsEnabled || durationMs <= 0 || !IsFinite(delta) || !CanReposition(element) ||
            !IsInVisibleViewport(element))
        {
            StopReposition(element);
            return false;
        }

        if (!TryGetVisual(element, out var visual)) return false;
        var continuing = Running.TryGetValue(element, out var previous) && previous.Kind == MotionKind.Reposition;
        if (!continuing && delta.LengthSquared() < 0.25f) return false;
        if (continuing) Finish(previous!, preserveVisualState: true);

        _ = Start(element, visual, 1, durationMs, 0, (compositor, easing) =>
        {
            // New responsive targets may never have had an entrance animation.
            // StartingValue needs a typed property; retain an existing animation's
            // value when replacing it instead of resetting its current translation.
            if (visual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded)
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            using var translation = compositor.CreateVector3KeyFrameAnimation();
            translation.SetVector3Parameter("layoutDelta", delta);
            translation.InsertExpressionKeyFrame(0, "this.StartingValue + layoutDelta");
            translation.InsertKeyFrame(1, Vector3.Zero, easing);
            Configure(translation, durationMs, 0);
            visual.StartAnimation("Translation", translation);
            RepositionStartCount++;
        }, MotionKind.Reposition, EasingKind.Standard);
        return Running.TryGetValue(element, out var motion) && motion.Kind == MotionKind.Reposition;
    }

    internal static bool TryCaptureRepositionOrigin(FrameworkElement element, out Vector3 origin)
    {
        origin = Vector3.Zero;
        if (!IsEnabled || !CanReposition(element) || !IsInVisibleViewport(element) ||
            !TryGetVisual(element, out _)) return false;
        // ActualOffset is computed by XAML arrange. The handout visual can have
        // zero Offset even when the element occupies a nonzero layout cell.
        // Translation remains independent and is
        // sampled by this.StartingValue when the replacement animation begins.
        try
        {
            origin = element.ActualOffset;
            return IsFinite(origin);
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            return false;
        }
    }

    internal static bool TryGetLayoutPosition(FrameworkElement element, out Vector3 position)
    {
        position = Vector3.Zero;
        try
        {
            position = element.ActualOffset;
            return IsFinite(position);
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            return false;
        }
    }

    internal static void StopReposition(FrameworkElement element)
    {
        if (Running.TryGetValue(element, out var motion) && motion.Kind == MotionKind.Reposition) Finish(motion);
    }

    /// <summary>Settles running animations and releases their elements and awaiters.</summary>
    public static void StopAll()
    {
        foreach (var motion in Running.Values.ToArray()) Finish(motion);
    }

    public static void SetWindowActive(bool active)
    {
        if (_windowActive == active) return;
        _windowActive = active;
        if (!active) StopAll();
        PreferencesChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Shutdown()
    {
        _shutdown = true;
        _initializationVersion++;
        StopAll();
        // Pending layout captures have not entered Running yet. Release them even
        // if closing this window never produces another arrange or Unloaded event.
        ShuttingDown?.Invoke(null, EventArgs.Empty);
        ShuttingDown = null;
        if (_uiSettings is not null)
        {
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        }
        if (_accessibilitySettings is not null)
        {
            _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
        }
        _uiSettings = null;
        _accessibilitySettings = null;
        _dispatcher = null;
        _initialized = false;
        _animationsEnabled = false;
        _windowActive = true;
        PreferencesChanged = null;
    }

    private static void EnsureInitialized(FrameworkElement element)
    {
        if (!_initialized && !_shutdown) Initialize(element.DispatcherQueue);
    }

    private static void OnAnimationsEnabledChanged(UISettings sender, object args) => QueuePreferencesRefresh();
    private static void OnHighContrastChanged(AccessibilitySettings sender, object args) => QueuePreferencesRefresh();

    private static void QueuePreferencesRefresh()
    {
        var version = _initializationVersion;
        _dispatcher?.TryEnqueue(() =>
        {
            // Settings notifications can already be queued when the window closes.
            if (_initialized && !_shutdown && version == _initializationVersion) ReadPreferences();
        });
    }

    private static void ReadPreferences()
    {
        var animationsEnabled = false;
        var highContrast = _highContrast;
        try
        {
            animationsEnabled = _uiSettings?.AnimationsEnabled == true;
            highContrast = _accessibilitySettings?.HighContrast == true;
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            Debug.WriteLine($"Motion preferences unavailable: {exception.Message}");
        }
        var changed = _animationsEnabled != animationsEnabled || _highContrast != highContrast;
        _animationsEnabled = animationsEnabled;
        _highContrast = highContrast;
        if (!IsEnabled) StopAll();
        if (changed) PreferencesChanged?.Invoke(null, EventArgs.Empty);
    }

    private static Task Start(
        FrameworkElement element,
        Visual visual,
        float finalOpacity,
        int durationMs,
        int delayMs,
        Action<Compositor, CubicBezierEasingFunction> animate,
        MotionKind kind = MotionKind.Presentation,
        EasingKind easingKind = EasingKind.Entrance)
    {
        // Detached and collapsed visuals may never produce a composition batch
        // completion. Keep Reset usable before Loaded, but do not animate those targets.
        if (!IsEnabled || !IsVisibleInTree(element))
        {
            SetFinalState(visual, finalOpacity, resetOpacity: kind != MotionKind.Reposition);
            return Task.CompletedTask;
        }

        RunningMotion? motion = null;
        try
        {
            var compositor = visual.Compositor;
            var timeout = element.DispatcherQueue.CreateTimer();
            timeout.IsRepeating = false;
            timeout.Interval = TimeSpan.FromMilliseconds((double)Math.Max(1, durationMs) + Math.Max(0, delayMs) + 100);
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            motion = new RunningMotion(element, visual, batch, timeout, finalOpacity, kind);
            Running[element] = motion;
            try
            {
                element.Unloaded += motion.OnUnloaded;
                batch.Completed += motion.OnCompleted;
                timeout.Tick += motion.OnTimeout;
                timeout.Start();
                using var easing = CreateEasing(compositor, easingKind);
                animate(compositor, easing);
            }
            finally
            {
                // Even a visual invalidated while starting must close its scoped batch.
                batch.End();
            }
            return motion.Completion.Task;
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            Debug.WriteLine($"Motion target unavailable: {exception.Message}");
            if (motion is not null) Finish(motion);
            else SetFinalState(visual, finalOpacity, resetOpacity: kind != MotionKind.Reposition);
            return Task.CompletedTask;
        }
    }

    private static void Configure(KeyFrameAnimation animation, int durationMs, int delayMs)
    {
        animation.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
        animation.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMs));
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
    }

    private static CubicBezierEasingFunction CreateEasing(Compositor compositor, EasingKind kind) => kind switch
    {
        EasingKind.Exit => compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0), new Vector2(1, 0.5f)),
        EasingKind.Standard => compositor.CreateCubicBezierEasingFunction(new Vector2(0.8f, 0), new Vector2(0.2f, 1)),
        _ => compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1))
    };

    private static void Stop(FrameworkElement element, bool preserveVisualState = false)
    {
        if (Running.TryGetValue(element, out var motion)) Finish(motion, preserveVisualState);
    }

    private static void Finish(RunningMotion motion, bool preserveVisualState = false)
    {
        if (!Running.TryGetValue(motion.Element, out var current) || !ReferenceEquals(current, motion)) return;
        Running.Remove(motion.Element);
        motion.Element.Unloaded -= motion.OnUnloaded;
        motion.Timeout.Stop();
        motion.Timeout.Tick -= motion.OnTimeout;
        try
        {
            motion.Batch.Completed -= motion.OnCompleted;
            if (!preserveVisualState)
            {
                SetFinalState(motion.Visual, motion.FinalOpacity, resetOpacity: motion.Kind != MotionKind.Reposition);
            }
            motion.Batch.Dispose();
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            Debug.WriteLine($"Motion cleanup: {exception.Message}");
        }
        finally
        {
            motion.Completion.TrySetResult(true);
        }
    }

    private static bool IsVisibleInTree(FrameworkElement element)
    {
        try
        {
            if (!element.IsLoaded || element.XamlRoot is null) return false;
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
            }
            return true;
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            Debug.WriteLine($"Motion tree unavailable: {exception.Message}");
            return false;
        }
    }

    private static bool CanReposition(FrameworkElement element)
    {
        try
        {
            // A page entrance/fade owns its presentation until it settles. Do not
            // compete with it, even when the adaptive grid is nested inside the page.
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement owner && Running.TryGetValue(owner, out var motion) &&
                    motion.Kind != MotionKind.Reposition) return false;
            }
            return true;
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            return false;
        }
    }

    private static bool IsInVisibleViewport(FrameworkElement element)
    {
        if (!IsVisibleInTree(element) || element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
            element.XamlRoot?.Content is not UIElement root) return false;
        try
        {
            var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(
                0, 0, element.ActualWidth, element.ActualHeight));
            var rootSize = element.XamlRoot.Size;
            if (!Intersects(bounds, new Windows.Foundation.Rect(0, 0, rootSize.Width, rootSize.Height))) return false;
            for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is Microsoft.UI.Xaml.Controls.ScrollViewer viewport)
                {
                    var clip = viewport.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(
                        0, 0, viewport.ActualWidth, viewport.ActualHeight));
                    if (!Intersects(bounds, clip)) return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            return false;
        }
    }

    private static bool Intersects(Windows.Foundation.Rect left, Windows.Foundation.Rect right) =>
        left.Width > 0 && left.Height > 0 && right.Width > 0 && right.Height > 0 &&
        left.Left < right.Right && left.Right > right.Left && left.Top < right.Bottom && left.Bottom > right.Top;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool TryGetVisual(FrameworkElement element, out Visual visual)
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            visual = ElementCompositionPreview.GetElementVisual(element);
            return true;
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            Debug.WriteLine($"Motion visual unavailable: {exception.Message}");
            visual = null!;
            return false;
        }
    }

    private static void SetFinalState(Visual visual, float opacity, bool resetOpacity = true)
    {
        try
        {
            if (resetOpacity) visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            if (resetOpacity) visual.Opacity = opacity;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch (Exception exception) when (IsVisualUnavailable(exception))
        {
            Debug.WriteLine($"Motion final state unavailable: {exception.Message}");
        }
    }

    private static bool IsVisualUnavailable(Exception exception) =>
        exception is COMException or InvalidOperationException;

    private enum MotionKind { Presentation, Reposition }
    private enum EasingKind { Entrance, Exit, Standard }

    private sealed class RunningMotion(
        FrameworkElement element,
        Visual visual,
        CompositionScopedBatch batch,
        DispatcherQueueTimer timeout,
        float finalOpacity,
        MotionKind kind)
    {
        public FrameworkElement Element { get; } = element;
        public Visual Visual { get; } = visual;
        public CompositionScopedBatch Batch { get; } = batch;
        public DispatcherQueueTimer Timeout { get; } = timeout;
        public float FinalOpacity { get; } = finalOpacity;
        public MotionKind Kind { get; } = kind;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnCompleted(object sender, CompositionBatchCompletedEventArgs args) => Finish(this);
        public void OnUnloaded(object sender, RoutedEventArgs args) => Finish(this);
        public void OnTimeout(DispatcherQueueTimer sender, object args) => Finish(this);
    }
}




