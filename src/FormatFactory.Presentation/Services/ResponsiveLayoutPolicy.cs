using System.Globalization;

namespace FormatFactoryWinUI.Services;

internal enum ResponsiveLayoutMode
{
    Compact,
    Medium,
    Wide
}

internal readonly record struct ResponsiveCell(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

internal static class ResponsiveLayoutPolicy
{
    internal static ResponsiveLayoutMode SelectMode(double availableWidth, double compactThreshold = 720, double wideThreshold = 1040)
    {
        if (!double.IsFinite(availableWidth) || availableWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        }

        if (compactThreshold <= 0 || wideThreshold < compactThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(compactThreshold));
        }

        return availableWidth < compactThreshold
            ? ResponsiveLayoutMode.Compact
            : availableWidth < wideThreshold ? ResponsiveLayoutMode.Medium : ResponsiveLayoutMode.Wide;
    }

    // Coordinates are row,column[,rowSpan,columnSpan], independent of display DPI.
    internal static ResponsiveCell ParseCell(string value)
    {
        var parts = value.Split(',');
        if (parts.Length is not (2 or 4))
        {
            throw new FormatException($"Invalid responsive cell: {value}");
        }

        var row = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var column = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var rowSpan = parts.Length == 4 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 1;
        var columnSpan = parts.Length == 4 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : 1;
        if (row < 0 || column < 0 || rowSpan < 1 || columnSpan < 1)
        {
            throw new FormatException($"Invalid responsive cell: {value}");
        }

        return new(row, column, rowSpan, columnSpan);
    }
}
