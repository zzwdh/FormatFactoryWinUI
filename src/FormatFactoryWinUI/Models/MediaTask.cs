using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace FormatFactoryWinUI.Models;

public sealed class MediaTask : INotifyPropertyChanged
{
    private string _targetFormat = string.Empty;
    private string _conversionMode = FormatCatalog.DefaultConversionMode;
    private ConversionState _state = ConversionState.Waiting;
    private double _progress;
    private string _speed = "—";
    private string _remaining = "—";
    private string _message = "等待开始";
    private string _outputPath = string.Empty;
    private int _selectedAudioTrack;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string InputPath { get; set; } = string.Empty;
    public string? SourceRoot { get; set; }
    public MediaKind Kind { get; set; }
    public MediaProbeResult Probe { get; set; } = new();
    public long FileSize { get; set; }
    public ObservableCollection<string> AvailableFormats { get; } = [];
    public ObservableCollection<string> ModeOptions { get; } = [.. FormatCatalog.ConversionModes];
    public ObservableCollection<AudioTrackInfo> AudioTracks { get; } = [];

    public string FileName => Path.GetFileName(InputPath);
    public string InputDirectory => Path.GetDirectoryName(InputPath) ?? string.Empty;
    public string KindText => Kind switch
    {
        MediaKind.Video => "视频",
        MediaKind.Audio => "音频",
        _ => "图片"
    };
    public string FileSizeText => FileSize switch
    {
        >= 1_073_741_824 => $"{FileSize / 1_073_741_824d:F2} GB",
        >= 1_048_576 => $"{FileSize / 1_048_576d:F1} MB",
        >= 1024 => $"{FileSize / 1024d:F1} KB",
        _ => $"{FileSize} B"
    };
    public string MediaInfo => Kind switch
    {
        MediaKind.Video => $"{Probe.Width}×{Probe.Height} · {FormatDuration(Probe.Duration)}{(Probe.IsHdr ? " · HDR" : string.Empty)}",
        MediaKind.Audio => $"{FormatDuration(Probe.Duration)} · {Probe.AudioTracks.FirstOrDefault()?.Codec.ToUpperInvariant() ?? "未知编码"}",
        _ => $"{Probe.Width}×{Probe.Height}{(Probe.IsAnimated ? " · 动态" : string.Empty)}"
    };

    public string TargetFormat
    {
        get => _targetFormat;
        set
        {
            if (SetField(ref _targetFormat, value))
            {
                OnPropertyChanged(nameof(AudioTrackVisibility));
            }
        }
    }

    public string ConversionMode
    {
        get => _conversionMode;
        set => SetField(ref _conversionMode, value);
    }

    public ConversionState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string StateText => State switch
    {
        ConversionState.Waiting => "等待",
        ConversionState.Probing => "分析中",
        ConversionState.Running => "转换中",
        ConversionState.Paused => "已暂停",
        ConversionState.Completed => "已完成",
        ConversionState.Failed => "失败",
        ConversionState.Cancelled => "已取消",
        _ => "未知"
    };

    public double Progress { get => _progress; set => SetField(ref _progress, value); }
    public string Speed { get => _speed; set => SetField(ref _speed, value); }
    public string Remaining { get => _remaining; set => SetField(ref _remaining, value); }
    public string Message { get => _message; set => SetField(ref _message, value); }
    public string OutputPath { get => _outputPath; set => SetField(ref _outputPath, value); }
    public int SelectedAudioTrack { get => _selectedAudioTrack; set => SetField(ref _selectedAudioTrack, value); }

    public Visibility AudioTrackVisibility => Kind == MediaKind.Video && FormatCatalog.AudioFormats.Contains(TargetFormat, StringComparer.OrdinalIgnoreCase)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatDuration(TimeSpan duration) => duration <= TimeSpan.Zero
        ? "静态"
        : duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
}
