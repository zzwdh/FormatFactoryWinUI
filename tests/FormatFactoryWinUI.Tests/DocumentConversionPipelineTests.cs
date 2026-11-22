using System.Runtime.CompilerServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class DocumentConversionPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageToTextStreamsRecognizedPagesAndCleansWorkspace(bool cancelAfterFirstPage)
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var input = Path.Combine(directory.Path, "page.png");
        var output = Path.Combine(directory.Path, "result.txt");
        await File.WriteAllBytesAsync(input, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aMVEAAAAASUVORK5CYII="));
        string? workspace = null;
        var pipeline = new DocumentConversionPipeline(
            _ => Task.FromResult<IReadOnlyList<ExternalToolInfo>>([]), new PdfContentExtractor(),
            (_, _, _, _, _, _, _) => throw new InvalidOperationException("Unexpected PDF reader"), Decode);

        async IAsyncEnumerable<DecodedImagePage> Decode(IReadOnlyList<string> paths, bool recognizeText,
            string? language, string? imageDirectory, bool includeImages, [EnumeratorCancellation] CancellationToken token)
        {
            Assert.Equal(input, Assert.Single(paths));
            Assert.True(recognizeText);
            Assert.False(includeImages);
            Assert.Equal("en-US", language);
            Assert.True(Directory.Exists(imageDirectory));
            workspace = imageDirectory;
            await Task.Yield();
            yield return new([], 1, 1, "First page", "page.png", 0);
            if (cancelAfterFirstPage) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            yield return new([], 1, 1, "Second page", "page.png", 1);
        }

        var progress = new RecordingProgress();
        var request = new ConversionRequest
        {
            Inputs = [new(input, "png")], TargetFormatId = "txt",
            Options = new Dictionary<string, string> { ["outputPath"] = output, ["ocrLanguage"] = "en-US" }
        };
        var result = await pipeline.ConvertAsync(request, progress, cancellation.Token);
        Assert.NotNull(workspace);
        Assert.False(Directory.Exists(workspace));
        if (cancelAfterFirstPage)
        {
            Assert.True(result.Cancelled, result.Message);
            Assert.False(result.Success);
            Assert.Empty(result.Artifacts);
            Assert.Equal(input, Assert.Single(Directory.EnumerateFiles(directory.Path)));
            Assert.DoesNotContain("completed", progress.Stages);
            return;
        }

        Assert.True(result.Success, result.Message);
        Assert.Equal(output, Assert.Single(result.Artifacts).Path);
        Assert.Equal(["First page", "Second page"],
            (await File.ReadAllLinesAsync(output)).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
        Assert.Equal(["planning", "image-decode", "text", "completed"], progress.Stages);
    }

    private sealed class RecordingProgress : IProgress<ConversionExecutionProgress>
    {
        public List<string> Stages { get; } = [];
        public void Report(ConversionExecutionProgress value) => Stages.Add(value.Stage);
    }
}
