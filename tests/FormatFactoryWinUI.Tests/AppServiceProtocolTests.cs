using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AppServiceProtocolTests
{
    [Theory]
    [InlineData("status", true)]
    [InlineData("get-state", true)]
    [InlineData("GET-STATE", true)]
    [InlineData("scan-watch-folder", false)]
    [InlineData("unknown", false)]
    public void DocumentedStateCommandAndLegacyAliasAreSupported(string command, bool supported)
    {
        var request = AppServiceProtocol.Parse(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2, ["requestId"] = "state-check", ["command"] = command
        });
        Assert.True(request.IsValid);
        Assert.Equal(command, request.Command);
        Assert.Equal(supported, AppServiceProtocol.IsStateQuery(request.Command));
    }

    [Fact]
    public void Version2Envelope_ParsesRequestAndArguments()
    {
        var request = AppServiceProtocol.Parse(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["requestId"] = "request-1",
            ["command"] = "scan-watch-folder",
            ["arguments"] = new Dictionary<string, object?> { ["force"] = true }
        });

        Assert.True(request.IsValid);
        Assert.False(request.IsLegacy);
        Assert.Equal(2, request.SchemaVersion);
        Assert.Equal("request-1", request.RequestId);
        Assert.Equal("scan-watch-folder", request.Command);
        Assert.Equal(true, request.Arguments["force"]);
    }

    [Fact]
    public void MissingSchema_UsesLegacyStatusContract()
    {
        var request = AppServiceProtocol.Parse(new Dictionary<string, object?>());

        Assert.True(request.IsValid);
        Assert.True(request.IsLegacy);
        Assert.Equal(1, request.SchemaVersion);
        Assert.Equal("status", request.Command);
    }

    [Theory]
    [InlineData(3, "request-1", "status", AppServiceErrorCodes.UnsupportedSchema)]
    [InlineData(2, "", "status", AppServiceErrorCodes.InvalidRequest)]
    [InlineData(2, "request-1", "", AppServiceErrorCodes.InvalidRequest)]
    public void InvalidVersion2Envelope_ReturnsStableError(
        int schemaVersion,
        string requestId,
        string command,
        string expectedError)
    {
        var request = AppServiceProtocol.Parse(new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["requestId"] = requestId,
            ["command"] = command
        });

        Assert.False(request.IsValid);
        Assert.Equal(expectedError, request.ErrorCode);
    }
}
