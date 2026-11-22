using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace FormatFactoryWinUI.Controls;

/// <summary>Native templates keep NavigationView item content textual and accessible.</summary>
internal static class NavigationRailLabel
{
    internal static DataTemplate CreateTemplate(string glyph, bool expanded, bool iconOnly, bool rtl)
    {
        // Only fixed framework markup and our own icon codes enter this parser.
        // Localized content is supplied solely through Binding.
        var icon = $"<FontIcon Glyph=\"&#x{(int)glyph[0]:X4};\" FontSize=\"20\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" AutomationProperties.AccessibilityView=\"Raw\" />";
        var alignment = rtl ? "Right" : "Left";
        var body = expanded
            ? $"<Grid x:Name=\"NavigationLabelSurface\" MinHeight=\"36\" Margin=\"12,0,16,0\" ColumnSpacing=\"12\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"24\"/><ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>{icon}<TextBlock Grid.Column=\"1\" Text=\"{{Binding}}\" FontSize=\"14\" TextWrapping=\"Wrap\" MaxLines=\"2\" TextTrimming=\"CharacterEllipsis\" TextAlignment=\"{alignment}\" VerticalAlignment=\"Center\" AutomationProperties.AccessibilityView=\"Raw\" /></Grid>"
            : iconOnly
                ? $"<Grid x:Name=\"NavigationLabelSurface\" Width=\"24\" Height=\"32\" HorizontalAlignment=\"Center\">{icon}</Grid>"
                : $"<Grid x:Name=\"NavigationLabelSurface\" Width=\"64\" HorizontalAlignment=\"Center\" RowSpacing=\"4\" Padding=\"0,4\"><Grid.RowDefinitions><RowDefinition Height=\"20\"/><RowDefinition Height=\"Auto\"/></Grid.RowDefinitions>{icon}<TextBlock Grid.Row=\"1\" Text=\"{{Binding}}\" FontSize=\"12\" MinHeight=\"16\" LineHeight=\"16\" LineStackingStrategy=\"BlockLineHeight\" TextAlignment=\"Center\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" AutomationProperties.AccessibilityView=\"Raw\" /></Grid>";
        return (DataTemplate)XamlReader.Load($"<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">{body}</DataTemplate>");
    }
}
