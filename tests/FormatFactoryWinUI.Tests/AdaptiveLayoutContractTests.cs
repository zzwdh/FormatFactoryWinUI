using System.Xml.Linq;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AdaptiveLayoutContractTests
{
    [Theory]
    [InlineData("Compact")]
    [InlineData("Medium")]
    [InlineData("Wide")]
    public void DeclaredLayoutsKeepChildrenInsideColumnsAndInSeparateCells(string mode)
    {
        var host = Path.Combine(RepositoryTestContext.Root, "src", "FormatFactoryWinUI");
        var documents = Directory.EnumerateFiles(Path.Combine(host, "Views"), "*.xaml", SearchOption.AllDirectories)
            .Append(Path.Combine(host, "MainWindow.xaml")).Select(XDocument.Load);
        var grids = documents.SelectMany(document => document.Descendants())
            .Where(element => element.Name.LocalName == "AdaptiveGrid").ToArray();
        Assert.NotEmpty(grids);
        foreach (var grid in grids)
        {
            var wideColumns = grid.Elements().FirstOrDefault(element => element.Name.LocalName.EndsWith(".ColumnDefinitions"))?.Elements().Count() ?? 1;
            var columnsText = mode == "Wide" ? null : (string?)grid.Attribute(mode + "Columns");
            var columnCount = mode == "Compact"
                ? (columnsText ?? "*").Split(',').Length
                : string.IsNullOrWhiteSpace(columnsText) ? Math.Max(wideColumns, 1) : columnsText.Split(',').Length;
            var children = grid.Elements().Where(element => !element.Name.LocalName.Contains('.')).ToArray();
            var occupied = new List<(XElement Element, ResponsiveCell Cell)>();
            var compactRow = 0;
            foreach (var child in children)
            {
                var cellText = child.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName == "AdaptiveGrid." + mode + "Cell")?.Value;
                var cell = mode == "Compact" && (bool?)grid.Attribute("AutoStackCompact") == true
                    ? new ResponsiveCell(compactRow++, 0)
                    : string.IsNullOrWhiteSpace(cellText)
                    ? new ResponsiveCell(ReadIndex(child, "Grid.Row", 0), ReadIndex(child, "Grid.Column", 0),
                        ReadIndex(child, "Grid.RowSpan", 1), ReadIndex(child, "Grid.ColumnSpan", 1))
                    : ResponsiveLayoutPolicy.ParseCell(cellText);
                Assert.True(cell.Column + cell.ColumnSpan <= columnCount,
                    $"{Describe(child)} exceeds {columnCount} columns in {mode} mode.");
                if ((string?)child.Attribute("Visibility") == "Collapsed") continue;
                foreach (var previous in occupied)
                {
                    var intersects = cell.Row < previous.Cell.Row + previous.Cell.RowSpan
                        && previous.Cell.Row < cell.Row + cell.RowSpan
                        && cell.Column < previous.Cell.Column + previous.Cell.ColumnSpan
                        && previous.Cell.Column < cell.Column + cell.ColumnSpan;
                    Assert.False(intersects, $"{Describe(child)} overlaps {Describe(previous.Element)} in {mode} mode.");
                }
                occupied.Add((child, cell));
            }
        }
    }

    private static int ReadIndex(XElement element, string attribute, int fallback) => (int?)element.Attribute(attribute) ?? fallback;

    private static string Describe(XElement element) =>
        (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
        ?? element.Name.LocalName;
}
