namespace FormatFactoryWinUI.Models;

/// <summary>Measurements captured for one attempt. Null byte counts mean unavailable, never zero.</summary>
public sealed record ConversionResultStatistics(long? InputBytes, long? OutputBytes, long ElapsedMilliseconds);
