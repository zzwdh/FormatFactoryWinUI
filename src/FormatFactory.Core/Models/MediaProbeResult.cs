namespace FormatFactoryWinUI.Models;

public sealed class MediaProbeResult : ContentProbeResult
{
    public MediaKind Kind
    {
        get => ContentKind.ToMediaKind();
        set => ContentKind = value.ToContentKind();
    }
    public TimeSpan Duration { get; set; }
    public uint Bitrate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string FormatName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public bool IsHdr { get; set; }
    public bool IsAnimated { get; set; }
    public string VideoCodec { get; set; } = string.Empty;
    public string VideoCodecTag { get; set; } = string.Empty;
    public int VideoStreamIndex { get; set; } = -1;
    public string PixelFormat { get; set; } = string.Empty;
    public string ColorTransfer { get; set; } = string.Empty;
    public string ColorPrimaries { get; set; } = string.Empty;
    public string ColorSpace { get; set; } = string.Empty;
    public string ColorRange { get; set; } = string.Empty;
    public IReadOnlyList<AudioTrackInfo> AudioTracks { get; set; } = [];
    public IReadOnlyList<string> SubtitleCodecs { get; set; } = [];
}
