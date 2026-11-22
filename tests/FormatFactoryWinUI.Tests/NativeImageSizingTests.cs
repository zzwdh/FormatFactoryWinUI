using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class NativeImageSizingTests
{
    [Fact]
    public async Task SizeSearch_SelectsHighestFittingQualityWithoutResizingOrChangingTaskOptions()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory);
        var codecs = new SizingCodecs();
        var result = await Service(codecs).ConvertAsync(task, string.Empty, Settings(directory), _ => { }, default);
        Assert.True(result.Success, result.Message);
        Assert.Equal(4000, new FileInfo(result.OutputPath).Length);
        Assert.Equal(40, (await File.ReadAllBytesAsync(result.OutputPath))[0]);
        Assert.InRange(codecs.Qualities.Count, 2, MediaSizeBudget.MaximumImageAttempts);
        Assert.False(task.Options.ContainsKey(MediaConversionOptions.ImageQualityKey));
        Assert.All(codecs.Dimensions, pair => { Assert.Null(pair.Item1); Assert.Null(pair.Item2); });
        Assert.Single(Directory.EnumerateFiles(Settings(directory).CustomOutputDirectory));
    }

    [Fact]
    public async Task ImpossibleNativeImageSize_FailsAndCleansAllCandidates()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask(directory);
        task.Options[MediaConversionOptions.TargetSizeKey] = "1";
        var result = await Service(new()).ConvertAsync(task, string.Empty, Settings(directory), _ => { }, default);
        Assert.False(result.Success);
        Assert.Empty(result.OutputPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Settings(directory).CustomOutputDirectory));
    }

    [Fact]
    public async Task CancellationDuringSearch_DoesNotPublishOrLeaveBestCandidate()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var task = CreateTask(directory);
        var result = await Service(new() { Cancel = cancellation }).ConvertAsync(task, string.Empty,
            Settings(directory), _ => { }, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(result.OutputPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Settings(directory).CustomOutputDirectory));
    }

    private static MediaTask CreateTask(TemporaryDirectory directory)
    {
        var input = Path.Combine(directory.Path, "source.bmp");
        File.WriteAllBytes(input, [1]);
        var task = new MediaTask { InputPath = input, Kind = MediaKind.Image, SourceFormatId = "bmp", TargetFormatId = "jpg" };
        task.Options[MediaConversionOptions.TargetSizeKey] = "4035";
        return task;
    }
    private static AppSettings Settings(TemporaryDirectory directory) => new()
    { OutputMode = "custom", CustomOutputDirectory = Path.Combine(directory.Path, "output") };
    private static NativeImageConversionService Service(SizingCodecs codecs) => new(codecs,
        (_, _, _, _, _) => throw new InvalidOperationException("A size-search failure must not start an unrelated fallback."));

    private sealed class SizingCodecs : IImageCodecService
    {
        public List<int> Qualities { get; } = [];
        public List<(int?, int?)> Dimensions { get; } = [];
        public CancellationTokenSource? Cancel { get; init; }
        public bool CanEncode(string targetFormatId) => true;
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<MediaProbeResult?>(new() { Kind = MediaKind.Image, Width = 640, Height = 480, PageCount = 1 });
        public Task TranscodeAsync(string inputPath, string outputPath, string targetFormatId, string mode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task TranscodeWithOptionsAsync(string inputPath, string outputPath, string targetFormatId,
            string mode, string colorPolicy, MediaConversionOptions options, CancellationToken cancellationToken = default)
        {
            var quality = options.ImageQuality!.Value;
            Qualities.Add(quality);
            Dimensions.Add((options.MaxWidth, options.MaxHeight));
            await File.WriteAllBytesAsync(outputPath, Enumerable.Repeat((byte)quality, quality * 100).ToArray(), cancellationToken);
            Cancel?.Cancel();
        }
    }
}
