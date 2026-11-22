using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class NativeConversionOutputRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeImageCommitConflict_PreservesValidatedBytesEvenWhenRecoveryStorageIsBlocked(bool blockedRecovery)
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path, "bmp", "png");
        var settings = Settings(directory.Path);
        var final = Path.Combine(settings.CustomOutputDirectory, "source.png");
        var recovery = Recovery(directory.Path, blockedRecovery);
        var service = new NativeImageConversionService(new ImageCodecs(),
            (_, _, _, _, _) => throw new InvalidOperationException("A commit conflict must not restart encoding."),
            recovery, CollidingPaths(final));

        var result = await service.ConvertAsync(task, task.InputPath, settings, _ => { }, default);

        AssertCommitFailure(result, final, recovery, blockedRecovery);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.PreservedOutputPath!));
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(task.InputPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscImageCommitConflict_ReturnsRecoverableValidatedImageWithoutDeletingPrivateFallback(bool blockedRecovery)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "source.iso");
        var bytes = new byte[8192];
        new Random(37).NextBytes(bytes);
        await File.WriteAllBytesAsync(input, bytes);
        var task = new MediaTask
        {
            InputPath = input, SourceFormatId = "iso", TargetFormatId = "cso",
            ContentProbe = new() { ContentKind = ContentKind.Disc, FormatId = "iso", FileSize = bytes.Length }
        };
        var settings = Settings(directory.Path);
        var final = Path.Combine(settings.CustomOutputDirectory, "source.cso");
        var recovery = Recovery(directory.Path, blockedRecovery);
        var service = new DiscConversionService(CollidingPaths(final), recovery);

        var result = await service.ConvertAsync(task, settings, string.Empty, _ => { }, default);

        AssertCommitFailure(result, final, recovery, blockedRecovery);
        Assert.True((await new DiscImageValidator().ValidateCsoAsync(result.PreservedOutputPath!)).IsValid);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(input));
        if (!blockedRecovery)
        {
            var record = recovery.GetById(result.RecoverableOutputId)!;
            Assert.Equal("cso", record.TargetFormatId);
            Assert.Equal(DiscConversionService.EngineId, record.EngineId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PngIntermediateCommitConflict_KeepsPrivateOutputAndReportsNoPrematureCompletion(bool blockedRecovery)
    {
        using var directory = new TemporaryDirectory();
        var task = ImageTask(directory.Path, "heif", "avif");
        var settings = Settings(directory.Path);
        var chosen = Path.Combine(directory.Path, "chosen");
        task.Options[OutputPathService.DirectoryOverrideKey] = chosen;
        task.Options["output.groupId"] = Guid.NewGuid().ToString("D");
        task.Options["outputLabel"] = "web";
        task.OutputGroupId = Guid.Parse(task.Options["output.groupId"]);
        task.OutputLabel = "web";
        var final = Path.Combine(chosen, "source_web.avif");
        var recovery = Recovery(directory.Path, blockedRecovery);
        var progress = new List<ConversionProgress>();
        string? workspace = null;
        var service = new NativeImageConversionService(new ImageCodecs(), async (normalized, _, isolated, report, token) =>
        {
            workspace = isolated.CustomOutputDirectory;
            Assert.NotEqual(chosen, workspace);
            Assert.Equal(workspace, OutputPathService.ResolveOutputDirectory(normalized, isolated));
            Assert.Null(normalized.OutputGroupId);
            Assert.False(normalized.Options.ContainsKey("output.groupId"));
            Assert.False(normalized.Options.ContainsKey("outputLabel"));
            using var output = new OutputPathService().Reserve(normalized, isolated);
            Assert.Equal(workspace, Path.GetDirectoryName(output.FinalPath));
            await File.WriteAllBytesAsync(output.TemporaryPath, [4, 5, 6], token);
            output.Commit();
            report(new(100, "—", "0 s", LocalizationService.T("ConversionFinished")));
            return new(true, false, output.FinalPath, LocalizationService.T("ConversionFinished"), string.Empty);
        }, recovery, CollidingPaths(final));

        var result = await service.ConvertAsync(task, task.InputPath, settings, progress.Add, default);

        AssertCommitFailure(result, final, recovery, blockedRecovery);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(result.PreservedOutputPath!));
        Assert.Equal(chosen, task.Options[OutputPathService.DirectoryOverrideKey]);
        Assert.NotNull(workspace);
        Assert.False(Directory.Exists(workspace));
        Assert.DoesNotContain(Directory.EnumerateFiles(chosen), path => Path.GetFileName(path).StartsWith("decoded", StringComparison.Ordinal));
        Assert.NotEmpty(progress);
        Assert.All(progress, value => Assert.InRange(value.Percent, 0, 99.5));
        Assert.DoesNotContain(progress, value => value.Message == LocalizationService.T("ConversionFinished"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PngIntermediate_ReportsCompletionOnlyAfterFinalCommitAndCleansOnCancellation(bool cancelAfterEncoding)
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var task = ImageTask(directory.Path, "heif", "avif");
        var settings = Settings(directory.Path);
        task.Options[OutputPathService.DirectoryOverrideKey] = settings.CustomOutputDirectory;
        var final = Path.Combine(settings.CustomOutputDirectory, "source.avif");
        var progress = new List<(ConversionProgress Value, bool FinalExists)>();
        string? workspace = null;
        var service = new NativeImageConversionService(new ImageCodecs(), async (normalized, _, isolated, report, token) =>
        {
            workspace = isolated.CustomOutputDirectory;
            using var output = new OutputPathService().Reserve(normalized, isolated);
            await File.WriteAllBytesAsync(output.TemporaryPath, [4, 5, 6], token);
            output.Commit();
            report(new(100, "—", "0 s", LocalizationService.T("ConversionFinished")));
            if (cancelAfterEncoding) cancellation.Cancel();
            return new(true, false, output.FinalPath, LocalizationService.T("ConversionFinished"), string.Empty);
        });

        var result = await service.ConvertAsync(task, task.InputPath, settings,
            value => progress.Add((value, File.Exists(final))), cancellation.Token);

        Assert.Equal(!cancelAfterEncoding, result.Success);
        Assert.Equal(cancelAfterEncoding, result.Cancelled);
        Assert.False(Directory.Exists(workspace));
        Assert.All(progress.Where(item => item.Value.Percent == 100), item => Assert.True(item.FinalExists));
        if (cancelAfterEncoding)
        {
            Assert.False(File.Exists(final));
            Assert.DoesNotContain(progress, item => item.Value.Percent == 100 || item.Value.Message == LocalizationService.T("ConversionFinished"));
        }
        else
        {
            Assert.Equal(final, result.OutputPath);
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(final));
            Assert.Single(progress, item => item.Value.Percent == 100);
        }
    }

    private static void AssertCommitFailure(ConversionResult result, string final, OutputRecoveryService recovery, bool blockedRecovery)
    {
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(result.OutputPath);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(final));
        Assert.NotNull(result.PreservedOutputPath);
        Assert.True(File.Exists(result.PreservedOutputPath));
        if (blockedRecovery)
        {
            Assert.Null(result.RecoverableOutputId);
            Assert.Contains(".partial", result.PreservedOutputPath);
        }
        else
        {
            Assert.NotNull(result.RecoverableOutputId);
            Assert.Equal(result.PreservedOutputPath, recovery.GetById(result.RecoverableOutputId)!.ArtifactPath);
        }
    }

    private static OutputPathService CollidingPaths(string final) => new(new DiskSpaceBudgetService(_ =>
    {
        File.WriteAllBytes(final, [9]);
        return null;
    }));

    private static OutputRecoveryService Recovery(string root, bool blocked)
    {
        var path = Path.Combine(root, "recovery");
        if (blocked) File.WriteAllBytes(path, [8]);
        return new OutputRecoveryService(path);
    }

    private static AppSettings Settings(string root) => new()
    { OutputMode = "custom", CustomOutputDirectory = Path.Combine(root, "output") };

    private static MediaTask ImageTask(string root, string sourceFormat, string targetFormat)
    {
        var path = Path.Combine(root, "source." + (sourceFormat == "heif" ? "heic" : sourceFormat));
        File.WriteAllBytes(path, [7]);
        return new()
        {
            InputPath = path, SourceFormatId = sourceFormat, TargetFormatId = targetFormat,
            Probe = new() { Kind = MediaKind.Image, Width = 2, Height = 2, PageCount = 1 },
            EngineId = "windows-image"
        };
    }

    private sealed class ImageCodecs : IImageCodecService
    {
        public bool CanEncode(string targetFormatId) => targetFormatId == "png";
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<MediaProbeResult?>(new() { Kind = MediaKind.Image, Width = 2, Height = 2, PageCount = 1 });
        public Task TranscodeAsync(string inputPath, string outputPath, string targetFormatId, string mode,
            CancellationToken cancellationToken = default) => File.WriteAllBytesAsync(outputPath, [1, 2, 3], cancellationToken);
    }
}
