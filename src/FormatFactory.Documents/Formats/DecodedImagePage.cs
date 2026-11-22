namespace FormatFactoryWinUI.Documents;

public sealed record DecodedImagePage(
    byte[] PngContent,
    uint Width,
    uint Height,
    string? RecognizedText,
    string SourceName,
    uint FrameIndex)
{
    public string? PngPath { get; init; }
    public OcrPageLayout? OcrLayout { get; init; }

    public Stream OpenRead() => Models.NormalizedResource.OpenContentRead(PngContent, PngPath);
}
