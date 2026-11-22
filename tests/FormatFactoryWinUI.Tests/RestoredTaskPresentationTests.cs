using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class RestoredTaskPresentationTests
{
    [Fact]
    public void Reprobe_UpdatesBoundMetadataWithoutReplacingTheTaskOrItsOptions()
    {
        var model = new MediaTask { Kind = MediaKind.Video, InputPath = @"C:\offline\input.mp4",
            State = ConversionState.Blocked, TargetFormat = "MP3", SelectedAudioTrack = 1 };
        using var row = new MediaTaskViewModel(model);
        var changed = new HashSet<string>();
        row.PropertyChanged += (_, args) => { if (args.PropertyName is { } name) changed.Add(name); };
        row.AudioTrackOptions.CollectionChanged += (_, _) => row.SelectedAudioTrack = -1;

        model.Probe = new MediaProbeResult { ContentKind = ContentKind.Video, Width = 1920, Height = 1080,
            Duration = TimeSpan.FromSeconds(5) };
        model.FileSize = 2048;
        model.AudioTracks.Add(new AudioTrackInfo { RelativeIndex = 0, Codec = "aac", Channels = 2 });
        model.AudioTracks.Add(new AudioTrackInfo { RelativeIndex = 1, Codec = "ac3", Channels = 6 });

        Assert.Contains(nameof(MediaTaskViewModel.MediaInfo), changed);
        Assert.Contains(nameof(MediaTaskViewModel.FileSizeText), changed);
        Assert.Contains("1920×1080", row.MediaInfo);
        Assert.Equal("2.0 KB", row.FileSizeText);
        Assert.Equal(2, row.AudioTrackOptions.Count);
        Assert.Equal(1, model.SelectedAudioTrack);
        Assert.Equal("MP3", model.TargetFormat);
        Assert.Same(model, row.Model);
    }

    [Fact]
    public void RecoveredOrderedInputs_UpdateTheCollectionLabelAndDetachOnDisposal()
    {
        var model = new MediaTask { Kind = MediaKind.Image, InputPath = @"C:\images\first.png" };
        var row = new MediaTaskViewModel(model);
        var changed = new HashSet<string>();
        row.PropertyChanged += (_, args) => { if (args.PropertyName is { } name) changed.Add(name); };
        model.Inputs.Add(new ConversionInput(model.InputPath, "png"));
        model.Inputs.Add(new ConversionInput(@"C:\images\second.png", "png"));
        Assert.Contains(nameof(MediaTaskViewModel.FileName), changed);
        Assert.Contains(nameof(MediaTaskViewModel.MediaInfo), changed);
        row.Dispose();
        changed.Clear();
        model.Inputs.Clear();
        model.AudioTracks.Add(new AudioTrackInfo());
        model.FileSize = 4096;
        Assert.Empty(changed);
    }
}
