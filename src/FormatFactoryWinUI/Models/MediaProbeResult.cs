namespace FormatFactoryWinUI.Models;

public sealed class MediaProbeResult
{
    public MediaKind Kind { get; set; }
    public TimeSpan Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsHdr { get; set; }
    public bool IsAnimated { get; set; }
    public string VideoCodec { get; set; } = string.Empty;
    public string PixelFormat { get; set; } = string.Empty;
    public string ColorTransfer { get; set; } = string.Empty;
    public string ColorPrimaries { get; set; } = string.Empty;
    public string ColorSpace { get; set; } = string.Empty;
    public string ColorRange { get; set; } = string.Empty;
    public IReadOnlyList<AudioTrackInfo> AudioTracks { get; set; } = [];
    public IReadOnlyList<string> SubtitleCodecs { get; set; } = [];
}
