using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class QueueCardPresentationTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationService.CurrentLanguageCode;

    public QueueCardPresentationTests() => LocalizationService.SetLanguage("en-US");

    public void Dispose() => LocalizationService.SetLanguage(_originalLanguage);

    [Theory]
    [InlineData(ConversionState.Waiting, true)]
    [InlineData(ConversionState.Failed, true)]
    [InlineData(ConversionState.Completed, true)]
    [InlineData(ConversionState.Cancelled, true)]
    [InlineData(ConversionState.Blocked, true)]
    [InlineData(ConversionState.Running, false)]
    [InlineData(ConversionState.Paused, false)]
    [InlineData(ConversionState.Probing, false)]
    public void Expansion_PreservesTheModelAndShowsOnlyTheAppropriateDetails(ConversionState state, bool editable)
    {
        var model = new MediaTask { State = state, TargetFormat = "MP4", ConversionMode = "quality" };
        using var view = new MediaTaskViewModel(model);
        Assert.False(view.IsDetailsExpanded);
        Assert.False(view.CanShowDetailsEditor);
        Assert.False(view.CanShowReadOnlyDetails);

        view.IsDetailsExpanded = true;

        Assert.Equal(editable, view.CanShowDetailsEditor);
        Assert.Equal(!editable, view.CanShowReadOnlyDetails);
        Assert.Equal(state, model.State);
        Assert.Equal("MP4", model.TargetFormat);
        Assert.Equal("quality", model.ConversionMode);
        using var secondPresentation = new MediaTaskViewModel(model);
        Assert.False(secondPresentation.IsDetailsExpanded);

        model.IsRecoveringOutput = true;
        Assert.True(view.IsDetailsExpanded);
        Assert.False(view.CanShowDetailsEditor);
        Assert.True(view.CanShowReadOnlyDetails);
        view.IsDetailsExpanded = false;
        Assert.False(view.CanShowReadOnlyDetails);
    }

    [Fact]
    public void CompactParameters_UseTheSelectedPresetAndLocalizeWithoutExpandingDetails()
    {
        var model = new MediaTask { TargetFormat = "MP4", ConversionMode = "quality" };
        model.Options[MediaConversionOptions.MaxWidthKey] = "1920";
        using var view = new MediaTaskViewModel(model);
        Assert.Equal("Quality", view.CompactParametersSummary);
        Assert.Contains("1920", view.ConversionSettingsSummary);

        var notifications = Observe(view);
        model.ConversionMode = "fast";
        Assert.Equal("Fast", view.CompactParametersSummary);
        Assert.Contains(nameof(MediaTaskViewModel.CompactParametersSummary), notifications);
        notifications.Clear();
        model.Options[MediaConversionOptions.MaxWidthKey] = "1280";
        model.ParametersVersion++;
        Assert.Contains(nameof(MediaTaskViewModel.ConversionSettingsSummary), notifications);
        Assert.Contains("1280", view.ConversionSettingsSummary);
        notifications.Clear();
        LocalizationService.SetLanguage("zh-Hans");
        view.RefreshLocalization();
        Assert.Equal("快速", view.CompactParametersSummary);
        Assert.Contains("1280", view.ConversionSettingsSummary);
        Assert.Contains(nameof(MediaTaskViewModel.CompactParametersSummary), notifications);
        Assert.Contains(nameof(MediaTaskViewModel.ConversionSettingsSummary), notifications);
        Assert.Contains(nameof(MediaTaskViewModel.DetailsToggleLabel), notifications);
        Assert.Equal("设置", view.DetailsToggleLabel);
    }

    [Fact]
    public void CompactParameters_RetainTheChosenTrackWhenProbeOptionsAreRebuilt()
    {
        var model = new MediaTask { Kind = MediaKind.Video, TargetFormat = "MP3", SelectedAudioTrack = 1 };
        using var view = new MediaTaskViewModel(model);
        var notifications = Observe(view);
        model.AudioTracks.Add(new AudioTrackInfo { RelativeIndex = 0, Codec = "aac", Language = "eng", Channels = 2 });
        Assert.DoesNotContain("AAC", view.CompactParametersSummary);
        model.AudioTracks.Add(new AudioTrackInfo { RelativeIndex = 1, Codec = "ac3", Language = "fra", Channels = 6 });
        Assert.Contains("AC3", view.CompactParametersSummary);
        Assert.DoesNotContain("AAC", view.CompactParametersSummary);
        Assert.Equal(1, model.SelectedAudioTrack);
        Assert.Contains(nameof(MediaTaskViewModel.CompactParametersSummary), notifications);

        notifications.Clear();
        view.SelectedAudioTrack = 0;
        Assert.Contains("AAC", view.CompactParametersSummary);
        Assert.DoesNotContain("AC3", view.CompactParametersSummary);
        Assert.Contains(nameof(MediaTaskViewModel.CompactParametersSummary), notifications);
        model.TargetFormat = "MP4";
        Assert.DoesNotContain("AAC", view.CompactParametersSummary);
    }

    [Fact]
    public void StatusMetrics_KeepLiveValuesSeparateFromTheAccessibleStateLabel()
    {
        var model = new MediaTask { State = ConversionState.Running };
        using var view = new MediaTaskViewModel(model);
        Assert.Empty(view.StatusMetricsSummary);
        Assert.Equal(view.StateText, view.StatusSummary);
        var notifications = Observe(view);
        model.Speed = "2.4x";
        model.Remaining = "00:24";
        Assert.Contains("2.4x", view.StatusMetricsSummary);
        Assert.Contains("00:24", view.StatusMetricsSummary);
        Assert.DoesNotContain(view.StateText, view.StatusMetricsSummary);
        Assert.Contains(view.StateText, view.StatusSummary);
        Assert.Contains(nameof(MediaTaskViewModel.StatusMetricsSummary), notifications);
        Assert.DoesNotContain(nameof(MediaTaskViewModel.StateText), notifications);

        model.State = ConversionState.Paused;
        Assert.DoesNotContain("2.4x", view.StatusMetricsSummary);
        Assert.Contains("00:24", view.StatusMetricsSummary);
        model.State = ConversionState.Completed;
        Assert.Empty(view.StatusMetricsSummary);
        Assert.Equal(view.StateText, view.StatusSummary);
    }

    [Fact]
    public void DetailMessages_HideOnlyGenericStateTextInEverySupportedLanguage()
    {
        var states = new[] { ConversionState.Waiting, ConversionState.Probing, ConversionState.Running,
            ConversionState.Paused, ConversionState.Completed, ConversionState.Cancelled };
        foreach (var language in LocalizationService.LanguageOptions.Where(option => option.Key != "auto").Select(option => option.Key))
        {
            LocalizationService.SetLanguage(language);
            foreach (var state in states)
            {
                using var view = new MediaTaskViewModel(new MediaTask { State = state });
                Assert.False(string.IsNullOrEmpty(view.Message));
                Assert.Empty(view.DetailMessage);
                Assert.False(view.HasDetailMessage);
                view.Message = "Distinct stage detail: 42";
                Assert.Equal("Distinct stage detail: 42", view.DetailMessage);
                Assert.True(view.HasDetailMessage);
                view.Message = view.StateText;
                Assert.Empty(view.DetailMessage);
            }
        }
    }

    [Theory]
    [InlineData(ConversionState.Failed)]
    [InlineData(ConversionState.Blocked)]
    public void DetailMessages_KeepFailureAndBlockedInformationEvenWhenEqualToTheState(ConversionState state)
    {
        var model = new MediaTask { State = state, Message = "codec detail: 42" };
        using var view = new MediaTaskViewModel(model);
        Assert.Equal("codec detail: 42", view.DetailMessage);
        view.Message = view.StateText;
        Assert.Equal(view.StateText, view.DetailMessage);
        Assert.True(view.HasDetailMessage);
    }

    [Fact]
    public void DetailMessages_PreserveHdrAndEngineWarningsAndNotifyWhenTheyChange()
    {
        using var hdr = new MediaTaskViewModel(new MediaTask { Probe = new MediaProbeResult { IsHdr = true } });
        Assert.Equal(LocalizationService.T("HdrAuto"), hdr.DetailMessage);
        Assert.True(hdr.HasDetailMessage);
        hdr.Model.Probe = new MediaProbeResult { IsHdr = false };
        Assert.Empty(hdr.DetailMessage);
        hdr.Model.Probe = new MediaProbeResult { IsHdr = true };
        Assert.Equal(LocalizationService.T("HdrAuto"), hdr.DetailMessage);

        using var view = new MediaTaskViewModel(new MediaTask());
        var notifications = Observe(view);
        view.SetEngineAvailability(false, "Missing required engine");
        Assert.Equal("Missing required engine", view.DetailMessage);
        Assert.Contains(nameof(MediaTaskViewModel.DetailMessage), notifications);
        Assert.Contains(nameof(MediaTaskViewModel.HasDetailMessage), notifications);
        view.SetEngineAvailability(true);
        Assert.Empty(view.DetailMessage);
        view.State = ConversionState.Running;
        Assert.Empty(view.DetailMessage);
        view.Message = "Encoding second pass";
        Assert.Equal("Encoding second pass", view.DetailMessage);
    }

    [Fact]
    public void CompatibilityWarning_StaysVisibleForRetainedChoicesWhenDetailsCloseOrConversionStarts()
    {
        var model = new MediaTask
        {
            InputPath = "synthetic.mp4", SourceFormatId = "mp4", TargetFormatId = "mp4",
            Probe = new MediaProbeResult
            {
                Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 0,
                AudioTracks =
                [
                    new() { Codec = "apple_apac", StreamIndex = 1, RelativeIndex = 0, Channels = 6 },
                    new() { Codec = "aac", StreamIndex = 2, RelativeIndex = 1, Channels = 2 }
                ]
            }
        };
        using var view = new MediaTaskViewModel(model);
        Assert.True(view.IsCompatibleAudioTrackVisible);
        Assert.False(view.ShowCompatibilityWarning);
        var notifications = Observe(view);
        view.IsDetailsExpanded = true;
        Assert.True(view.ShowCompatibilityWarning);
        Assert.Contains(nameof(MediaTaskViewModel.ShowCompatibilityWarning), notifications);
        view.UseCompatibleAudioTrack = true;
        view.IsDetailsExpanded = false;
        Assert.True(view.ShowCompatibilityWarning);
        model.State = ConversionState.Running;
        Assert.True(view.ShowCompatibilityWarning);
        Assert.True(view.UseCompatibleAudioTrack);
        model.State = ConversionState.Waiting;
        view.UseCompatibleAudioTrack = false;
        Assert.False(view.ShowCompatibilityWarning);
        view.IsDetailsExpanded = true;
        Assert.True(view.ShowCompatibilityWarning);
        model.TargetFormatId = "png";
        Assert.False(view.ShowCompatibilityWarning);
    }

    private static HashSet<string?> Observe(MediaTaskViewModel view)
    {
        var notifications = new HashSet<string?>();
        view.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        return notifications;
    }
}
