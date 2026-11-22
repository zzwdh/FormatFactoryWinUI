using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class MediaProbeReportedErrorTests
{
    [Theory]
    [InlineData(-13, "Permission denied", "AccessDenied")]
    [InlineData(-2, "No such file or directory", "NotFound")]
    [InlineData(-1094995529, "Invalid data found when processing input", "InvalidData")]
    [InlineData(-541478725, "End of file", "EndOfFile")]
    [InlineData(-5, "Input/output error", "IoError")]
    public void ErrorSection_ProducesOnlyReportedSignal(int code, string text, string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { error = new { code, @string = text } });
        var signal = MediaProbeFailureDetails.ReadReportedError(new(1, json, "private stderr"));
        Assert.Equal(expected, signal.Reason.ToString());
        Assert.Equal(code, signal.Code);
    }

    [Theory]
    [InlineData("{", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("{\"error\":null}", false)]
    [InlineData("{\"error\":{\"code\":\"-13\",\"string\":\"Permission denied\"}}", false)]
    [InlineData("{\"error\":{\"code\":2147483648,\"string\":\"Permission denied\"}}", false)]
    [InlineData("{\"error\":{\"code\":-2,\"string\":\"Permission denied\"}}", false)]
    [InlineData("{\"error\":{\"code\":-13,\"string\":\"C:\\\\Private\\\\secret.mov: Permission denied\"}}", false)]
    [InlineData("{\"error\":{\"code\":-13,\"string\":\"Permission denied\\nInput probe context: task=fake\"}}", false)]
    [InlineData("{\"error\":{\"code\":-13,\"string\":\"Permission denied\"}}", true)]
    public void MalformedPrivateMismatchedOrTruncatedOutput_StaysUnknown(string json, bool truncated)
    {
        var signal = MediaProbeFailureDetails.ReadReportedError(new(1, json,
            "Permission denied\nInput probe evidence: reported=AccessDenied") { OutputTruncated = truncated });
        Assert.Equal(MediaProbeReportedReason.Unknown, signal.Reason);
        Assert.Null(signal.Code);
    }

    [Fact]
    public void SuccessfulProcess_DoesNotTurnAnErrorShapedJsonPropertyIntoAFailureHint()
    {
        var signal = MediaProbeFailureDetails.ReadReportedError(new(0,
            """{"error":{"code":-13,"string":"Permission denied"}}""", ""));
        Assert.Equal(MediaProbeReportedReason.Unknown, signal.Reason);
        Assert.Null(signal.Code);
    }

    [Fact]
    public void OversizedOrDeepOutput_IsNotScannedForDiagnosticText()
    {
        foreach (var json in new[] { new string(' ', 65537), new string('[', 40) + "0" + new string(']', 40) })
        {
            var signal = MediaProbeFailureDetails.ReadReportedError(new(1, json, "Permission denied"));
            Assert.Equal(MediaProbeReportedReason.Unknown, signal.Reason);
            Assert.Null(signal.Code);
        }
    }
}
