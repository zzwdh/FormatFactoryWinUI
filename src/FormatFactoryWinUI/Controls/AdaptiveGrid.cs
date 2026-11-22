using System.Globalization;
using System.Numerics;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI.Controls;

/// <summary>
/// Repositions this grid's direct children by available effective width.
/// Original Grid coordinates define the wide layout; attached cells override it.
/// </summary>
public sealed class AdaptiveGrid : Grid
{
    private readonly Dictionary<UIElement, ResponsiveCell> _wideCells = [];
    private GridLength[]? _wideColumns;
    private ResponsiveLayoutMode? _currentMode;
    private readonly Dictionary<FrameworkElement, Vector3> _pendingRepositionOrigins = [];

    public AdaptiveGrid()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width != args.PreviousSize.Width)
            {
                ApplyLayout();
            }
        };
    }

    public string MediumColumns { get; set; } = "";
    public string CompactColumns { get; set; } = "*";
    public double CompactThreshold { get; set; } = 720;
    public double WideThreshold { get; set; } = 1040;
    public bool AutoStackCompact { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        FluentMotion.PreferencesChanged += OnMotionPreferencesChanged;
        FluentMotion.ShuttingDown += OnMotionPreferencesChanged;
        ApplyLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        FluentMotion.PreferencesChanged -= OnMotionPreferencesChanged;
        FluentMotion.ShuttingDown -= OnMotionPreferencesChanged;
        CancelPendingReposition();
        foreach (var child in Children.OfType<FrameworkElement>()) FluentMotion.StopReposition(child);
    }

    private void OnMotionPreferencesChanged(object? sender, EventArgs args)
    {
        if (!FluentMotion.IsEnabled) CancelPendingReposition();
    }

    public static readonly DependencyProperty CompactCellProperty = DependencyProperty.RegisterAttached(
        "CompactCell", typeof(string), typeof(AdaptiveGrid), new PropertyMetadata(""));
    public static readonly DependencyProperty MediumCellProperty = DependencyProperty.RegisterAttached(
        "MediumCell", typeof(string), typeof(AdaptiveGrid), new PropertyMetadata(""));

    public static string GetCompactCell(DependencyObject element) => (string)element.GetValue(CompactCellProperty);
    public static void SetCompactCell(DependencyObject element, string value) => element.SetValue(CompactCellProperty, value);
    public static string GetMediumCell(DependencyObject element) => (string)element.GetValue(MediumCellProperty);
    public static void SetMediumCell(DependencyObject element, string value) => element.SetValue(MediumCellProperty, value);

    private void ApplyLayout()
    {
        if (!IsLoaded || ActualWidth <= 0)
        {
            return;
        }

        var mode = ResponsiveLayoutPolicy.SelectMode(ActualWidth, CompactThreshold, WideThreshold);
        if (_currentMode == mode)
        {
            return;
        }

        // Continuous resizing follows XAML layout immediately. Only an actual
        // breakpoint change bridges the old/new positions, after the next arrange.
        // Initial presentation belongs to the existing page entrance animation.
        var animateReposition = _currentMode is not null && FluentMotion.IsEnabled;
        if (animateReposition)
        {
            foreach (var child in Children.OfType<FrameworkElement>())
            {
                if (!_pendingRepositionOrigins.ContainsKey(child) &&
                    FluentMotion.TryCaptureRepositionOrigin(child, out var origin))
                {
                    _pendingRepositionOrigins[child] = origin;
                }
            }
        }

        _wideColumns ??= ColumnDefinitions.Select(column => column.Width).ToArray();
        foreach (var child in Children.OfType<FrameworkElement>())
        {
            _wideCells.TryAdd(child, new(GetRow(child), GetColumn(child), GetRowSpan(child), GetColumnSpan(child)));
        }

        // Set the mode before changing definitions: measure/arrange can raise SizeChanged again.
        _currentMode = mode;
        var columns = mode switch
        {
            ResponsiveLayoutMode.Compact => ParseColumns(CompactColumns),
            ResponsiveLayoutMode.Medium when !string.IsNullOrWhiteSpace(MediumColumns) => ParseColumns(MediumColumns),
            _ => _wideColumns
        };

        ColumnDefinitions.Clear();
        foreach (var column in columns)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = column });
        }

        var rowCount = 1;
        var compactRow = 0;
        foreach (var child in Children.OfType<FrameworkElement>())
        {
            var cellText = mode switch
            {
                ResponsiveLayoutMode.Compact => GetCompactCell(child),
                ResponsiveLayoutMode.Medium => GetMediumCell(child),
                _ => ""
            };
            var cell = mode == ResponsiveLayoutMode.Compact && AutoStackCompact
                ? new ResponsiveCell(compactRow++, 0)
                : string.IsNullOrWhiteSpace(cellText) ? _wideCells[child] : ResponsiveLayoutPolicy.ParseCell(cellText);
            SetRow(child, cell.Row);
            SetColumn(child, cell.Column);
            SetRowSpan(child, cell.RowSpan);
            SetColumnSpan(child, cell.ColumnSpan);
            rowCount = Math.Max(rowCount, cell.Row + cell.RowSpan);
        }

        RowDefinitions.Clear();
        for (var row = 0; row < rowCount; row++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        if (_pendingRepositionOrigins.Count > 0)
        {
            LayoutUpdated -= OnRepositionLayoutUpdated;
            LayoutUpdated += OnRepositionLayoutUpdated;
        }
    }

    private void OnRepositionLayoutUpdated(object? sender, object args)
    {
        LayoutUpdated -= OnRepositionLayoutUpdated;
        var origins = _pendingRepositionOrigins.ToArray();
        _pendingRepositionOrigins.Clear();
        if (!IsLoaded || !FluentMotion.IsEnabled) return;
        foreach (var (child, origin) in origins)
        {
            if (Children.Contains(child) && FluentMotion.TryGetLayoutPosition(child, out var position))
            {
                FluentMotion.Reposition(child, origin - position);
            }
        }
    }

    private void CancelPendingReposition()
    {
        LayoutUpdated -= OnRepositionLayoutUpdated;
        _pendingRepositionOrigins.Clear();
    }

    private static GridLength[] ParseColumns(string value) => value.Split(',').Select(part =>
    {
        part = part.Trim();
        if (part.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return GridLength.Auto;
        }

        if (part.EndsWith('*'))
        {
            var weight = part.Length == 1 ? 1 : double.Parse(part[..^1], CultureInfo.InvariantCulture);
            return new GridLength(weight, GridUnitType.Star);
        }

        return new GridLength(double.Parse(part, CultureInfo.InvariantCulture));
    }).ToArray();
}
