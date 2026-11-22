using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.ConversionEngines;

/// <summary>Shared option interpretation for planning and executing document routes.</summary>
public static class DocumentConversionOptions
{
    public static bool IsFixedLayout(ConversionRequest request) =>
        string.Equals(request.Mode?.Trim(), "fixed", StringComparison.OrdinalIgnoreCase) ||
        IsValue(request, "layoutMode", "fixed") || IsValue(request, "layout", "fixed");

    public static bool RepairDocx(ConversionRequest request) => IsValue(request, "repairDocx", "true");

    private static bool IsValue(ConversionRequest request, string key, string expected) =>
        request.Options.TryGetValue(key, out var value) &&
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
