using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class NamingPreviewTests
{
    [Fact]
    public void Preview_CancellationStopsBeforeReadingOrPlanningMorePaths()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new OutputPathService();
        Assert.Throws<OperationCanceledException>(() => service.Preview(TaskFor("source"), new AppSettings(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => service.PreviewMany([TaskFor("source")], new AppSettings(), cancellation.Token));
    }

    [Fact]
    public void Preview_DoesNotCreateDirectoriesOrConsumeNameOrDiskBudget()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "not-created", "nested");
        var settings = Settings(destination);
        var task = TaskFor(root.Path);
        var service = new OutputPathService(new DiskSpaceBudgetService(_ => throw new InvalidOperationException("Preview must not reserve disk space")));

        var first = service.Preview(task, settings);
        Assert.Equal(first, service.Preview(task, settings));
        Assert.False(Directory.Exists(destination));
        using var lease = new OutputPathService().Reserve(task, settings);
        Assert.Equal(first.FinalPath, lease.FinalPath);
    }

    [Fact]
    public void Preview_UsesSameExistingFileDirectoryAndInFlightConflictsAsReservation()
    {
        using var root = new TemporaryDirectory();
        var settings = Settings(root.Path);
        var task = TaskFor(root.Path);
        File.WriteAllText(Path.Combine(root.Path, "clip.mp4"), "existing");
        Directory.CreateDirectory(Path.Combine(root.Path, "clip (1).mp4"));
        var service = new OutputPathService();
        using var running = service.Reserve(task, settings);

        var preview = service.Preview(task, settings);
        Assert.True(preview.HasCollision);
        Assert.EndsWith("clip (3).mp4", preview.FinalPath);
        using var reserved = service.Reserve(task, settings);
        Assert.Equal(preview.FinalPath, reserved.FinalPath);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(root.Path, "clip.mp4")));
    }

    [Fact]
    public void PreviewMany_PlansCollisionsBetweenOutputsWithoutLeakingReservations()
    {
        using var root = new TemporaryDirectory();
        var settings = Settings(root.Path);
        var task = TaskFor(root.Path);
        var service = new OutputPathService();
        var result = service.PreviewMany([task, task], settings);

        Assert.Equal(Path.Combine(root.Path, "clip.mp4"), result[0].FinalPath);
        Assert.Equal(Path.Combine(root.Path, "clip (1).mp4"), result[1].FinalPath);
        Assert.Equal(result[0], service.Preview(task, settings));
        using var reserved = service.Reserve(task, settings);
        Assert.Equal(result[0].FinalPath, reserved.FinalPath);
    }

    [Fact]
    public void Preview_PreservesFoldersAndUsesSourceOutputMode()
    {
        using var root = new TemporaryDirectory();
        var task = TaskFor(Path.Combine(root.Path, "source", "album"));
        task.SourceRoot = Path.Combine(root.Path, "source");
        var settings = Settings(Path.Combine(root.Path, "destination"));
        var service = new OutputPathService();

        Assert.Equal(Path.Combine(settings.CustomOutputDirectory, "album", "clip.mp4"), service.Preview(task, settings).FinalPath);
        settings.PreserveFolderStructure = false;
        Assert.Equal(Path.Combine(settings.CustomOutputDirectory, "clip.mp4"), service.Preview(task, settings).FinalPath);
        settings.OutputMode = "source";
        Assert.Equal(Path.Combine(task.InputDirectory, "clip.mp4"), service.Preview(task, settings).FinalPath);
        Assert.False(Directory.Exists(settings.CustomOutputDirectory));
    }

    [Fact]
    public void Preview_FrameSequenceUsesDirectorySuffix()
    {
        using var root = new TemporaryDirectory();
        var task = TaskFor(root.Path);
        task.TargetFormatId = "frames";
        var service = new OutputPathService();
        var preview = service.Preview(task, Settings(root.Path));
        using var actual = service.Reserve(task, Settings(root.Path));

        Assert.True(preview.IsDirectory);
        Assert.Equal(Path.Combine(root.Path, "clip frames"), preview.FinalPath);
        Assert.Equal(preview.FinalPath, actual.FinalPath);
    }

    [Theory]
    [InlineData(OpticalDiscFormatIds.BluRayDisc, "mkv", " MKV")]
    [InlineData(OpticalDiscFormatIds.AudioCd, "flac", " FLAC")]
    [InlineData(OpticalDiscFormatIds.DataDisc, "iso", ".iso")]
    public void Preview_PhysicalDiscUsesVolumeLabelAndPipelineSuffix(string source, string target, string suffix)
    {
        using var root = new TemporaryDirectory();
        var task = new MediaTask { InputPath = @"D:\", ContentKind = ContentKind.Disc, SourceFormatId = source, TargetFormatId = target };
        task.ContentProbe.Metadata = new Dictionary<string, string> { ["volumeLabel"] = "My disc" };
        // This test exercises naming, not the physical drive's size or the developer's free space.
        var service = new OutputPathService(new DiskSpaceBudgetService(_ => new DiskSpaceInfo("preview-volume", 1L << 60)));
        var preview = service.Preview(task, Settings(root.Path));
        using var actual = service.Reserve(task, Settings(root.Path), suffix, !suffix.StartsWith('.'), "My disc");

        Assert.Equal(Path.Combine(root.Path, "My disc" + suffix), preview.FinalPath);
        Assert.Equal(actual.FinalPath, preview.FinalPath);
        Assert.Equal(!suffix.StartsWith('.'), preview.IsDirectory);
    }

    [Fact]
    public void PresetToken_UsesLabelOnceOrStableModeAndSourceDimensions()
    {
        var task = TaskFor("source");
        task.OutputLabel = "Mobile";
        task.Probe = new MediaProbeResult { Width = 1920, Height = 1080 };
        task.Options[MediaConversionOptions.MaxWidthKey] = "720";
        var settings = new AppSettings { OutputFileNameTemplate = "{NAME}_{PRESET}_{width}x{height}_{format}" };

        Assert.Equal("clip_Mobile_1920x1080_MP4", OutputPathService.BuildFileName(task, settings));
        task.OutputLabel = string.Empty;
        task.ConversionMode = "quality";
        Assert.Equal("clip_quality_1920x1080_MP4", OutputPathService.BuildFileName(task, settings));
        task.Probe = new MediaProbeResult();
        Assert.Equal("clip_quality_unknownxunknown_MP4", OutputPathService.BuildFileName(task, settings));
    }

    [Fact]
    public void LabelIsStillAutomaticallyAppendedUnlessPresetTokenAlreadyIncludesIt()
    {
        var task = TaskFor("source");
        task.OutputLabel = "../../unsafe:name";
        Assert.Equal("clip_.._.._unsafe_name", OutputPathService.BuildFileName(task, new AppSettings()));
        Assert.Equal("clip_.._.._unsafe_name", OutputPathService.BuildFileName(task,
            new AppSettings { OutputFileNameTemplate = "{name}_{preset}" }));
    }

    [Fact]
    public void InvalidTemplateNeverCreatesOutputDirectory()
    {
        using var root = new TemporaryDirectory();
        var settings = Settings(Path.Combine(root.Path, "not-created"));
        settings.OutputFileNameTemplate = "../{name}";
        var service = new OutputPathService();
        Assert.Throws<ArgumentException>(() => service.Preview(TaskFor(root.Path), settings));
        Assert.Throws<ArgumentException>(() => service.Reserve(TaskFor(root.Path), settings));
        Assert.False(Directory.Exists(settings.CustomOutputDirectory));
    }

    private static MediaTask TaskFor(string directory) => new() { InputPath = Path.Combine(directory, "clip.mov"), TargetFormatId = "mp4" };
    private static AppSettings Settings(string directory) => new() { OutputMode = "custom", CustomOutputDirectory = directory };
}
