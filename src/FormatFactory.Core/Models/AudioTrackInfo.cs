namespace FormatFactoryWinUI.Models;

public sealed class AudioTrackInfo
{
    public int RelativeIndex { get; set; }
    public string Codec { get; set; } = string.Empty;
    public string CodecTag { get; set; } = string.Empty;
    public int StreamIndex { get; set; } = -1;
    public string Language { get; set; } = "und";
    public string Title { get; set; } = string.Empty;
    public int Channels { get; set; }
    public string ChannelLayout { get; set; } = string.Empty;
}
