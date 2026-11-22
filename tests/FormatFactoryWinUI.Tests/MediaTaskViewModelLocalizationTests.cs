using System.Collections.Specialized;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class MediaTaskViewModelLocalizationTests
{
    [Theory]
    [InlineData(ConversionState.Waiting, "Waiting to start", "等待开始")]
    [InlineData(ConversionState.Probing, "Analyzing", "分析中")]
    [InlineData(ConversionState.Running, "Converting…", "正在转换…")]
    [InlineData(ConversionState.Paused, "Paused", "已暂停")]
    [InlineData(ConversionState.Completed, "Conversion complete", "转换完成")]
    [InlineData(ConversionState.Cancelled, "Cancelled", "已取消")]
    public void RefreshLocalization_UpdatesStableStatePresentationWithoutChangingPersistedMessage(
        ConversionState state,
        string expectedEnglish,
        string expectedSimplifiedChinese)
    {
        try
        {
            LocalizationService.SetLanguage("en-US");
            var model = new MediaTask { State = state, Message = "persisted detail" };
            using var viewModel = new MediaTaskViewModel(model);

            Assert.Equal(expectedEnglish, viewModel.Message);
            Assert.Equal("persisted detail", model.Message);

            LocalizationService.SetLanguage("zh-Hans");
            viewModel.RefreshLocalization();

            Assert.Equal(expectedSimplifiedChinese, viewModel.Message);
            Assert.Equal("persisted detail", model.Message);
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public void RefreshLocalization_PreservesFailureDetailsAndNewProgressReplacesGenericOverride()
    {
        try
        {
            LocalizationService.SetLanguage("zh-Hans");
            var failedModel = new MediaTask { State = ConversionState.Failed, Message = "codec exploded: 42" };
            using var failed = new MediaTaskViewModel(failedModel);
            failed.RefreshLocalization();
            Assert.Equal("codec exploded: 42", failed.Message);

            var runningModel = new MediaTask { State = ConversionState.Running, Message = "old progress" };
            using var running = new MediaTaskViewModel(runningModel);
            Assert.Equal("正在转换…", running.Message);

            running.Message = "阶段 2";
            Assert.Equal("阶段 2", running.Message);
            Assert.Equal("阶段 2", runningModel.Message);
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public void WaitingTask_UsesCurrentEngineRequirementAheadOfGenericStateMessage()
    {
        try
        {
            LocalizationService.SetLanguage("zh-Hans");
            var model = new MediaTask { State = ConversionState.Waiting, Message = "old waiting text" };
            using var viewModel = new MediaTaskViewModel(model);

            viewModel.SetEngineAvailability(false, "需要当前工具");

            Assert.Equal("需要当前工具", viewModel.Message);
            Assert.Equal("old waiting text", model.Message);
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public void RefreshLocalization_PreservesPresetAndAudioTrackDuringCollectionReset()
    {
        var model = new MediaTask
        {
            ConversionMode = "quality",
            SelectedAudioTrack = 1
        };
        model.AudioTracks.Add(new AudioTrackInfo { RelativeIndex = 0, Codec = "aac", Channels = 2 });
        model.AudioTracks.Add(new AudioTrackInfo { RelativeIndex = 1, Codec = "ac3", Channels = 6 });
        using var viewModel = new MediaTaskViewModel(model);
        viewModel.ModeOptions.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                viewModel.ConversionMode = null!;
            }
        };
        viewModel.AudioTrackOptions.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                viewModel.SelectedAudioTrack = -1;
            }
        };

        viewModel.RefreshLocalization();

        Assert.Equal("quality", model.ConversionMode);
        Assert.Equal(1, model.SelectedAudioTrack);
    }

    [Fact]
    public void RefreshLocalization_NotifiesLocalizedMultiInputFileName()
    {
        try
        {
            LocalizationService.SetLanguage("en-US");
            var model = new MediaTask { Kind = MediaKind.Image, InputPath = @"C:\images\first.png" };
            model.Inputs.Add(new ConversionInput(@"C:\images\first.png", "png"));
            model.Inputs.Add(new ConversionInput(@"C:\images\second.png", "png"));
            model.Inputs.Add(new ConversionInput(@"C:\images\third.png", "png"));
            using var viewModel = new MediaTaskViewModel(model);
            var fileNameChanged = false;
            viewModel.PropertyChanged += (_, args) =>
                fileNameChanged |= args.PropertyName == nameof(MediaTaskViewModel.FileName);

            LocalizationService.SetLanguage("zh-Hans");
            viewModel.RefreshLocalization();

            Assert.True(fileNameChanged);
            Assert.Equal("first.png 等 2 个文件", viewModel.FileName);
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }
}
