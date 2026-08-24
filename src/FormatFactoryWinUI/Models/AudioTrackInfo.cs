namespace FormatFactoryWinUI.Models;

public sealed class AudioTrackInfo
{
    public int StreamIndex { get; set; }
    public int RelativeIndex { get; set; }
    public string Codec { get; set; } = string.Empty;
    public string Language { get; set; } = "未知";
    public string Title { get; set; } = string.Empty;
    public int Channels { get; set; }

    public string DisplayName => $"音轨 {RelativeIndex + 1} · {Language} · {Codec.ToUpperInvariant()} · {Channels} 声道";
}
