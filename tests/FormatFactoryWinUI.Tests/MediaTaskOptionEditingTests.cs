using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaTaskOptionEditingTests
{
    [Fact]
    public void ClearedAudioSelection_DoesNotReplaceSelectedTrack()
    {
        var model = new MediaTask { SelectedAudioTrack = 1 };
        using var viewModel = new MediaTaskViewModel(model);

        viewModel.SelectedAudioTrack = -1;

        Assert.Equal(1, model.SelectedAudioTrack);
        viewModel.SelectedAudioTrack = 0;
        Assert.Equal(0, model.SelectedAudioTrack);
    }

    [Fact]
    public void RestoredNegativeAudioTrack_UsesFirstTrack()
    {
        var model = new MediaTask { SelectedAudioTrack = -1 };

        Assert.Equal(0, model.SelectedAudioTrack);
    }

    [Theory]
    [InlineData(ConversionState.Running)]
    [InlineData(ConversionState.Paused)]
    [InlineData(ConversionState.Probing)]
    public void ActiveTask_RejectsParameterEditsUntilFinished(ConversionState activeState)
    {
        var model = new MediaTask
        {
            TargetFormat = "MP4",
            ConversionMode = "quality",
            SelectedAudioTrack = 1
        };
        using var viewModel = new MediaTaskViewModel(model);
        var editability = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MediaTaskViewModel.CanEditConversionOptions))
                editability.Add(viewModel.CanEditConversionOptions);
        };

        model.State = activeState;
        viewModel.TargetFormat = "MP3";
        viewModel.ConversionMode = "fast";
        viewModel.SelectedAudioTrack = 0;

        Assert.False(viewModel.CanEditConversionOptions);
        Assert.Equal("MP4", model.TargetFormat);
        Assert.Equal("quality", model.ConversionMode);
        Assert.Equal(1, model.SelectedAudioTrack);

        model.State = ConversionState.Cancelled;
        viewModel.TargetFormat = "MP3";
        viewModel.ConversionMode = "fast";
        viewModel.SelectedAudioTrack = 0;

        Assert.True(viewModel.CanEditConversionOptions);
        Assert.Equal("MP3", model.TargetFormat);
        Assert.Equal("fast", model.ConversionMode);
        Assert.Equal(0, model.SelectedAudioTrack);
        Assert.Equal([false, true], editability);
    }
}
