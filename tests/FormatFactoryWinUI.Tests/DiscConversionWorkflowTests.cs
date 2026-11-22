using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class DiscConversionWorkflowTests
{
    [Theory]
    [InlineData(OpticalDiscMediaKind.DvdVideo, OpticalDiscFormatIds.DvdDisc, "iso")]
    [InlineData(OpticalDiscMediaKind.DataDisc, OpticalDiscFormatIds.DataDisc, "cso")]
    [InlineData(OpticalDiscMediaKind.AudioCd, OpticalDiscFormatIds.AudioCd, "wav")]
    [InlineData(OpticalDiscMediaKind.BluRay, OpticalDiscFormatIds.BluRayDisc, "mkv")]
    public async Task ImportedPhysicalDiscSelectsDiscEngineAndRevalidatesMissingDrive(
        OpticalDiscMediaKind mediaKind, string sourceFormatId, string targetFormatId)
    {
        using var directory = new TemporaryDirectory();
        var root = UnusedDriveRoot();
        var disc = new OpticalDiscInfo(root, OpticalDiscPath.ToDevicePath(root),
            "SYNTHETIC DISC", mediaKind, true, mediaKind != OpticalDiscMediaKind.AudioCd, 0);
        var imported = await new MediaImportCoordinator().ImportOpticalDiscAsync(disc);
        Assert.Null(imported.Error);
        var task = Assert.IsType<MediaTask>(imported.Task);
        task.TargetFormatId = targetFormatId;
        var settings = OutputSettings(directory.Path);
        var request = ConversionRequestFactory.Capture(task, settings);
        Assert.Equal(sourceFormatId, request.Inputs.Single().FormatId);
        Assert.Equal(ContentKind.Disc, request.Inputs.Single().Probe!.ContentKind);
        Assert.Equal(ContentKind.Disc,
            ConversionRequestFactory.CreateMediaTask(request, DiscConversionService.EngineId).ContentKind);
        var reader = new UnexpectedDiscReader();
        var workflow = new ConversionWorkflow([
            new DiscConversionEngine(new DiscConversionService(nativeDiscReader: reader), string.Empty, settings)]);

        var result = await workflow.ExecuteAsync(request, null, default);

        // Admission uses the imported snapshot; execution must rediscover the current medium.
        Assert.Equal(DiscConversionService.EngineId, result.EngineId);
        Assert.Equal(ConversionOutcome.Failed, result.Outcome);
        Assert.False(result.Execution.Success);
        Assert.Empty(result.Execution.Artifacts);
        Assert.Equal(0, reader.ReadCount);
        Assert.False(Directory.Exists(settings.CustomOutputDirectory));
    }

    [Fact]
    public async Task GenericPhysicalDiscIdStillSelectsDiscEngineAndRequiresPresentMedia()
    {
        using var directory = new TemporaryDirectory();
        var settings = OutputSettings(directory.Path);
        var reader = new UnexpectedDiscReader();
        var workflow = new ConversionWorkflow([
            new DiscConversionEngine(new DiscConversionService(nativeDiscReader: reader), string.Empty, settings)]);
        var request = new ConversionRequest
        {
            Inputs = [new(UnusedDriveRoot(), OpticalDiscFormatIds.OpticalDisc,
                new ContentProbeResult { ContentKind = ContentKind.Disc, FormatId = OpticalDiscFormatIds.OpticalDisc })],
            TargetFormatId = "iso",
            OutputDirectory = settings.CustomOutputDirectory
        };

        var result = await workflow.ExecuteAsync(request, null, default);

        Assert.Equal(DiscConversionService.EngineId, result.EngineId);
        Assert.Equal(ConversionOutcome.Failed, result.Outcome);
        Assert.Empty(result.Execution.Artifacts);
        Assert.Equal(0, reader.ReadCount);
        Assert.False(Directory.Exists(settings.CustomOutputDirectory));
    }

    [Theory]
    [InlineData(OpticalDiscFormatIds.DvdDisc)]
    [InlineData(OpticalDiscFormatIds.DataDisc)]
    [InlineData(OpticalDiscFormatIds.AudioCd)]
    [InlineData(OpticalDiscFormatIds.BluRayDisc)]
    [InlineData(OpticalDiscFormatIds.OpticalDisc)]
    public void PhysicalDiscWithoutProbeRetainsDiscKindAfterCaptureAndRehydration(string sourceFormatId)
    {
        var task = new MediaTask
        {
            InputPath = "Z:\\", SourceFormatId = sourceFormatId, TargetFormatId = "iso",
            ContentKind = ContentKind.Disc
        };
        task.Inputs.Add(new(task.InputPath, sourceFormatId));

        var request = ConversionRequestFactory.Capture(task, new AppSettings());
        Assert.Null(request.Inputs.Single().Probe);
        var restored = ConversionRequestFactory.CreateMediaTask(request, DiscConversionService.EngineId);

        Assert.Equal(ContentKind.Disc, restored.ContentKind);
        Assert.Equal(ContentKind.Disc, restored.ContentProbe.ContentKind);
        Assert.Equal(sourceFormatId, restored.SourceFormatId);
        Assert.Equal(sourceFormatId, restored.ContentProbe.FormatId);
    }

    [Theory]
    [InlineData(OpticalDiscFormatIds.DvdDisc)]
    [InlineData(OpticalDiscFormatIds.DataDisc)]
    [InlineData(OpticalDiscFormatIds.AudioCd)]
    [InlineData(OpticalDiscFormatIds.BluRayDisc)]
    [InlineData(OpticalDiscFormatIds.OpticalDisc)]
    public void PhysicalDiscProbeAndMetadataSurviveCaptureAndRehydration(string sourceFormatId)
    {
        var probe = new ContentProbeResult
        {
            ContentKind = ContentKind.Disc, FormatId = sourceFormatId, FileSize = 8192,
            Metadata = new Dictionary<string, string> { ["volumeLabel"] = "SYNTHETIC DISC" }
        };
        var task = new MediaTask
        {
            InputPath = "Z:\\", SourceFormatId = sourceFormatId, TargetFormatId = "iso",
            ContentProbe = probe
        };
        task.Inputs.Add(new(task.InputPath, sourceFormatId, probe));

        var request = ConversionRequestFactory.Capture(task, new AppSettings());
        var restored = ConversionRequestFactory.CreateMediaTask(request, DiscConversionService.EngineId);

        Assert.Equal(ContentKind.Disc, restored.ContentKind);
        Assert.Equal(sourceFormatId, restored.ContentProbe.FormatId);
        Assert.Equal(8192, restored.FileSize);
        Assert.Equal("SYNTHETIC DISC", restored.ContentProbe.Metadata["volumeLabel"]);
        Assert.NotSame(probe, restored.ContentProbe);
    }

    [Theory]
    [InlineData("mp4", "mp3")]
    [InlineData("wav", "flac")]
    [InlineData("png", "jpg")]
    [InlineData("docx", "pdf")]
    public void NonDiscInputsRemainOutsideDiscEngine(string sourceFormatId, string targetFormatId)
    {
        var engine = new DiscConversionEngine(new DiscConversionService(), string.Empty, new AppSettings());

        Assert.False(engine.CanHandle(sourceFormatId, targetFormatId));
        var request = new ConversionRequest
        {
            Inputs = [new("synthetic-input", sourceFormatId)], TargetFormatId = targetFormatId
        };
        var restored = ConversionRequestFactory.CreateMediaTask(request, "other-engine");
        Assert.Equal(FormatCatalog.FindById(sourceFormatId)!.ContentKind, restored.ContentKind);
        Assert.NotEqual(ContentKind.Disc, restored.ContentKind);
    }

    [Fact]
    public async Task IsoAndCsoStillRoundTripByteForByteThroughDiscWorkflowWithoutPhysicalReader()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "synthetic-image.iso");
        var bytes = new byte[8193];
        new Random(39).NextBytes(bytes.AsSpan(4096));
        await File.WriteAllBytesAsync(input, bytes);
        var settings = OutputSettings(directory.Path);
        var reader = new UnexpectedDiscReader();
        var workflow = new ConversionWorkflow([
            new DiscConversionEngine(new DiscConversionService(nativeDiscReader: reader), string.Empty, settings)]);
        var compress = new ConversionRequest
        {
            Inputs = [new(input, "iso")], TargetFormatId = "cso",
            OutputDirectory = settings.CustomOutputDirectory
        };

        var compressed = await workflow.ExecuteAsync(compress, null, default);
        Assert.Equal(DiscConversionService.EngineId, compressed.EngineId);
        Assert.Equal(ConversionOutcome.Completed, compressed.Outcome);
        Assert.Single(compressed.Execution.Artifacts);
        Assert.True(File.Exists(compressed.OutputPath));
        var restore = new ConversionRequest
        {
            Inputs = [new(compressed.OutputPath, "cso")], TargetFormatId = "iso",
            OutputDirectory = settings.CustomOutputDirectory
        };
        var restored = await workflow.ExecuteAsync(restore, null, default);

        Assert.Equal(DiscConversionService.EngineId, restored.EngineId);
        Assert.Equal(ConversionOutcome.Completed, restored.Outcome);
        Assert.Single(restored.Execution.Artifacts);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(restored.OutputPath));
        Assert.Equal(0, reader.ReadCount);
        Assert.Empty(Directory.EnumerateFiles(settings.CustomOutputDirectory, "*.partial*"));
        Assert.Empty(Directory.EnumerateFiles(settings.CustomOutputDirectory, "*.tmp*"));
    }

    private static AppSettings OutputSettings(string root) => new()
    {
        OutputMode = "custom", CustomOutputDirectory = Path.Combine(root, "output")
    };

    private static string UnusedDriveRoot()
    {
        var occupied = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        var letter = Enumerable.Range('A', 26).Select(value => (char)value).Reverse()
            .First(value => !occupied.Contains(value));
        return $"{letter}:\\";
    }

    private sealed class UnexpectedDiscReader : IWindowsOpticalDiscReader
    {
        public int ReadCount { get; private set; }

        private Exception UnexpectedRead()
        {
            ReadCount++;
            return new InvalidOperationException("An absent synthetic drive must not be read.");
        }

        public Task<CdTableOfContents> ReadTableOfContentsAsync(string rootOrDevicePath,
            CancellationToken cancellationToken = default) => throw UnexpectedRead();

        public Task<IReadOnlyList<string>> ExtractAudioCdToWaveAsync(string rootOrDevicePath, string outputDirectory,
            IProgress<OpticalOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw UnexpectedRead();

        public Task<string> ReadDataDiscToIsoAsync(string rootOrDevicePath, string outputPath,
            IProgress<OpticalOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw UnexpectedRead();
    }
}
