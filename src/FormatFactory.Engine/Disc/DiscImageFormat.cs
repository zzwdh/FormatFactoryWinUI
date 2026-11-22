namespace FormatFactoryWinUI.Disc;

public enum DiscImageFormat
{
    Iso,
    Cso
}

public sealed record DiscImageConversionProgress(
    long ProcessedBytes,
    long TotalBytes,
    int ProcessedBlocks,
    int TotalBlocks)
{
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0d, 100d);
}

public sealed record DiscImageValidationResult(
    bool IsValid,
    DiscImageFormat Format,
    long UncompressedLength,
    int BlockSize,
    int BlockCount,
    string? Message = null);
