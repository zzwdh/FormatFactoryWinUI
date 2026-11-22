using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputGroupPresentationTests
{
    [Fact]
    public void HeaderlessSegmentDoesNotAggregateOrCollapseIndependentRows()
    {
        using var first = new MediaTaskViewModel(new MediaTask { InputPath = "first.mp4", State = ConversionState.Running, Progress = 30 });
        using var second = new MediaTaskViewModel(new MediaTask { InputPath = "second.mp4", State = ConversionState.Waiting });
        using var segment = new OutputTaskGroupViewModel(Guid.NewGuid(), false);
        var changed = new List<string?>();
        ((System.ComponentModel.INotifyPropertyChanged)segment).PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var collectionChanges = 0;
        segment.CollectionChanged += (_, _) => collectionChanges++;
        segment.Add(first);
        segment.Add(second);
        first.Progress = 90;
        first.Model.State = ConversionState.Completed;
        segment.RefreshLocalization();
        segment.IsExpanded = false;

        Assert.Equal(2, collectionChanges);
        Assert.DoesNotContain(nameof(segment.Summary), changed);
        Assert.DoesNotContain(nameof(segment.Progress), changed);
        Assert.DoesNotContain(nameof(segment.Title), changed);
        Assert.Empty(segment.Title);
        Assert.Empty(segment.Summary);
        Assert.Equal(0, segment.Progress);
        Assert.True(segment.IsExpanded);
        Assert.True(first.OutputGroupExpanded);
        Assert.True(second.OutputGroupExpanded);
    }

    [Fact]
    public void CollapsingAndRemovingGroupKeepsIndependentTasksAndProgress()
    {
        using var first = new MediaTaskViewModel(new MediaTask { InputPath = "source.mp4", TargetFormatId = "mp4", State = ConversionState.Completed });
        using var second = new MediaTaskViewModel(new MediaTask { InputPath = "source.mp4", TargetFormatId = "mp3", State = ConversionState.Running, Progress = 40 });
        using var group = new OutputTaskGroupViewModel(Guid.NewGuid(), true) { first, second };
        Assert.Equal(70, group.Progress);
        group.IsExpanded = false;
        Assert.False(first.OutputGroupExpanded);
        Assert.False(second.OutputGroupExpanded);
        Assert.Equal(ConversionState.Running, second.State);
        second.Progress = 80;
        Assert.Equal(90, group.Progress);
        group.Remove(second);
        Assert.True(second.OutputGroupExpanded);
        Assert.Equal(100, group.Progress);
    }

    [Fact]
    public void RequestSnapshotRetainsPerOutputOverridesAndIsIsolated()
    {
        var task = new MediaTask { InputPath = "source.docx", SourceFormatId = "docx", TargetFormatId = "epub" };
        task.Options["layoutMode"] = "fixed";
        task.Options["ocrLanguage"] = "en-US";
        task.Options["outputFileNameTemplate"] = "{name}-book";
        task.Options["media.targetSizeBytes"] = "104857600";
        var settings = new AppSettings { EbookLayoutMode = "reflow", OcrLanguage = "auto", OutputFileNameTemplate = "{name}" };
        var request = ConversionRequestFactory.Capture(task, settings);
        Assert.Equal("fixed", request.Options["layoutMode"]);
        Assert.Equal("en-US", request.Options["ocrLanguage"]);
        Assert.Equal("{name}-book", request.Options["outputFileNameTemplate"]);
        task.Options["layoutMode"] = "reflow";
        Assert.Equal("fixed", request.Options["layoutMode"]);
        Assert.Equal("auto", settings.OcrLanguage);
    }

    [Fact]
    public void CustomParametersRemainVisibleAfterEditingAndLocalizationRefresh()
    {
        using var task = new MediaTaskViewModel(new MediaTask { InputPath = "source.mp4", TargetFormatId = "mp4" });
        Assert.False(task.HasConversionSettingsSummary);
        new MediaConversionOptions { TargetSizeBytes = 104857600, MaxWidth = 1280 }.ApplyTo(task.Model.Options);
        task.RefreshLocalization();
        Assert.True(task.HasConversionSettingsSummary);
        Assert.Contains("100 MB", task.ConversionSettingsSummary);
        Assert.Contains("1280", task.ConversionSettingsSummary);
    }

    [Fact]
    public void OutputDescriptionNotifiesWhenItsTargetChanges()
    {
        using var task = new MediaTaskViewModel(new MediaTask { InputPath = "source.mp4", TargetFormatId = "mp4", OutputLabel = "share" });
        var changed = new List<string?>();
        task.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        task.TargetFormat = "MP3";
        Assert.Equal("share · MP3", task.OutputDescription);
        Assert.Contains(nameof(MediaTaskViewModel.OutputDescription), changed);
    }
}
