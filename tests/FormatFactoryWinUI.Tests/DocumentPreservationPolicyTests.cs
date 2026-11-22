using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class DocumentPreservationPolicyTests
{
    [Theory]
    [InlineData("docx", "txt")]
    [InlineData("docx", "pdf")]
    [InlineData("pdf", "pdf")]
    [InlineData("pdf", "epub")]
    [InlineData("png", "pdf")]
    [InlineData("tiff", "epub")]
    [InlineData("epub", "mobi")]
    [InlineData("caj", "pdf")]
    public void LosslessDocumentRoutes_RejectBeforeToolDiscoveryOrNormalization(string source, string target)
    {
        var request = Request(source, target, "lossless");
        Assert.Throws<NotSupportedException>(() => DocumentPreservationPolicy.Validate(request));
        Assert.Throws<NotSupportedException>(() => new DocumentConversionRoutePlanner().Plan(request, []));
    }

    [Fact]
    public async Task LosslessMixedImagePdf_RejectsBeforeInvokingAnyWorkflowEngine()
    {
        var request = Request("png", "pdf", "Lossless") with
        {
            Inputs = [new("alpha.png", "png"), new("sixteen-bit.tiff", "tiff")],
            OutputDirectory = "must-not-be-created"
        };
        var result = await new ConversionWorkflow([new UnexpectedEngine()]).ExecuteAsync(request, null, default);
        Assert.Equal(ConversionOutcome.Failed, result.Outcome);
        Assert.Empty(result.Execution.Artifacts);
        Assert.Equal(string.Empty, result.EngineId);
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("balanced")]
    [InlineData("fast")]
    [InlineData("fixed")]
    public void NormalDocumentModes_KeepExistingRoutes(string mode)
    {
        var request = Request("docx", "txt", mode);
        DocumentPreservationPolicy.Validate(request);
        Assert.NotEmpty(new DocumentConversionRoutePlanner().Plan(request, []).Steps);
        var images = Request("png", "pdf", mode) with { Inputs = [new("one.png", "png"), new("two.tiff", "tiff")] };
        DocumentPreservationPolicy.Validate(images);
        Assert.NotEmpty(new DocumentConversionRoutePlanner().Plan(images, []).Steps);
    }

    [Theory]
    [InlineData("png", "tiff")]
    [InlineData("wav", "flac")]
    [InlineData("iso", "cso")]
    public void LosslessNonDocumentRoutes_AreLeftToTheirVerifyingEngines(string source, string target) =>
        DocumentPreservationPolicy.Validate(Request(source, target, "lossless"));

    private static ConversionRequest Request(string source, string target, string mode) => new()
    {
        Inputs = [new("source." + source, source)], TargetFormatId = target, Mode = mode
    };

    private sealed class UnexpectedEngine : IConversionEngine
    {
        public string EngineId => "unreachable";
        public bool CanHandle(string source, string target) => throw new InvalidOperationException("Policy must reject before engine selection.");
        public Task<ConversionExecutionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionExecutionProgress>? progress,
            CancellationToken token) => throw new InvalidOperationException("Policy must reject before document conversion.");
    }
}
