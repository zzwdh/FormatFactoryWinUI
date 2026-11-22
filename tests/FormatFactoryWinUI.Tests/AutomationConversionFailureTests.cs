using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationConversionFailureTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]
    [InlineData(unchecked((int)0x80070040), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(unchecked((int)0x80070070), false)]
    public async Task WorkflowKeepsTypedRetryClassificationWhenConvertingExceptionToMessage(int hresult, bool retryable)
    {
        var request = new ConversionRequest
        {
            Inputs = [new("input.wav", "wav")], TargetFormatId = "mp3"
        };
        var workflow = new ConversionWorkflow([new ThrowingEngine(new IOException("localized message", hresult))]);
        var result = await workflow.ExecuteAsync(request, null, default);
        Assert.Equal(ConversionOutcome.Failed, result.Outcome);
        Assert.Equal(retryable, result.Execution.Retryable);
        Assert.Contains("localized message", result.Execution.Message);
    }

    [Fact]
    public void EngineResultMappingKeepsRetryabilityAndRecoveryEvidence()
    {
        var transient = new ConversionResult(false, false, string.Empty, "sharing conflict", string.Empty) { Retryable = true };
        Assert.True(ConversionRequestFactory.ToExecutionResult(transient, "mp3").Retryable);
        var preserved = transient with { RecoverableOutputId = "recovery-id", PreservedOutputPath = "safe.partial" };
        var mapped = ConversionRequestFactory.ToExecutionResult(preserved, "mp3");
        Assert.Equal("recovery-id", mapped.RecoverableOutputId);
        Assert.Equal("safe.partial", mapped.PreservedOutputPath);
    }

    private sealed class ThrowingEngine(Exception failure) : IConversionEngine
    {
        public string EngineId => "typed-failure";
        public bool CanHandle(string sourceFormatId, string targetFormatId) => true;
        public Task<ConversionExecutionResult> ConvertAsync(ConversionRequest request,
            IProgress<ConversionExecutionProgress>? progress, CancellationToken cancellationToken) => throw failure;
    }
}
