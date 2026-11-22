using FormatFactoryWinUI.Services;
using System.Text.Json;

namespace FormatFactoryWinUI.Tests;

public sealed class ActivationArgumentParserTests
{
    [Fact]
    public void LaunchArguments_PreservesLiteralPercentEscapesInFileNames()
    {
        using var directory = new TemporaryDirectory();
        var literalPath = Path.Combine(directory.Path, "one%20file.mp4");
        var decodedPath = Path.Combine(directory.Path, "one file.mp4");
        File.WriteAllText(literalPath, "literal");
        File.WriteAllText(decodedPath, "decoded");

        var request = ActivationArgumentParser.FromLaunchArguments($"\"{literalPath}\"", SupportedExtensions);

        Assert.Equal([literalPath], request.FilePaths);
    }

    [Fact]
    public void LaunchArguments_ProtocolForwardsBatchResolverAndTarget()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "batch.mp4");
        File.WriteAllText(path, "video");
        var batchId = Guid.NewGuid().ToString("N");
        var resolvedIds = new List<string>();

        var request = ActivationArgumentParser.FromLaunchArguments(
            $"formatfactory://open?batch={batchId}&target=mkv",
            SupportedExtensions,
            batchResolver: id =>
            {
                resolvedIds.Add(id);
                return [path];
            });

        Assert.Equal([batchId], resolvedIds);
        Assert.Equal([path], request.FilePaths);
        Assert.Equal("mkv", request.TargetFormatId);
        Assert.False(request.OpenSettings);
    }

    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>([".mp4", ".mkv"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Protocol_ImportsExistingSupportedPathsAndRemovesDuplicates()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "one file.mp4");
        var second = Path.Combine(directory.Path, "second.MKV");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var uri = new Uri(
            $"formatfactory://open?path={Uri.EscapeDataString(first)}" +
            $"&path={Uri.EscapeDataString(second)}" +
            $"&path={Uri.EscapeDataString(first)}");

        var request = ActivationArgumentParser.FromProtocol(uri, SupportedExtensions);

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], request.FilePaths);
        Assert.False(request.OpenSettings);
        Assert.Null(request.OpenPath);
    }

    [Fact]
    public void Protocol_WithNoValidImportFallsBackToSettings()
    {
        using var directory = new TemporaryDirectory();
        var unsupported = Path.Combine(directory.Path, "notes.txt");
        File.WriteAllText(unsupported, "text");
        var missing = Path.Combine(directory.Path, "missing.mp4");
        var uri = new Uri(
            $"formatfactory://open?path={Uri.EscapeDataString(unsupported)}" +
            $"&path={Uri.EscapeDataString(missing)}");

        var request = ActivationArgumentParser.FromProtocol(uri, SupportedExtensions);

        Assert.Empty(request.FilePaths);
        Assert.True(request.OpenSettings);
    }

    [Fact]
    public void Protocol_CarriesRequestedTargetFormat()
    {
        var path = Path.Combine(Path.GetTempPath(), "document.docx");
        var request = ActivationArgumentParser.FromProtocol(
            new Uri("formatfactory://open?batch=0123456789abcdef0123456789abcdef&target=md"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx" },
            _ => true,
            _ => [path]);

        Assert.Equal([Path.GetFullPath(path)], request.FilePaths);
        Assert.Equal("md", request.TargetFormatId);
    }

    [Fact]
    public void Protocol_RejectsUnsafeTargetFormat()
    {
        var path = Path.Combine(Path.GetTempPath(), "document.docx");
        var request = ActivationArgumentParser.FromProtocol(
            new Uri("formatfactory://open?path=" + Uri.EscapeDataString(path) + "&target=../pdf"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx" },
            _ => true);

        Assert.Single(request.FilePaths);
        Assert.Null(request.TargetFormatId);
    }

    [Fact]
    public void Protocol_WithUnknownHostOrSchemeOpensSettings()
    {
        Assert.True(ActivationArgumentParser
            .FromProtocol(new Uri("formatfactory://settings"), SupportedExtensions)
            .OpenSettings);
        Assert.True(ActivationArgumentParser
            .FromProtocol(new Uri("https://open/?path=C%3A%5Cvideo.mp4"), SupportedExtensions)
            .OpenSettings);
    }

    [Fact]
    public void LaunchArguments_ParseQuotedFilesAndIgnoreUnsupportedOrMissingFiles()
    {
        using var directory = new TemporaryDirectory();
        var supported = Path.Combine(directory.Path, "quoted video.mp4");
        var unsupported = Path.Combine(directory.Path, "notes.txt");
        var missing = Path.Combine(directory.Path, "missing.mkv");
        File.WriteAllText(supported, "video");
        File.WriteAllText(unsupported, "text");
        var arguments = $"\"{supported}\" \"{unsupported}\" \"{missing}\" \"{supported}\"";

        var request = ActivationArgumentParser.FromLaunchArguments(arguments, SupportedExtensions);

        Assert.Equal([supported], request.FilePaths);
    }

    [Fact]
    public void LaunchArguments_ParseSettingsRestoreAndOutputRoundTrip()
    {
        var settings = ActivationArgumentParser.FromLaunchArguments("--open-settings", SupportedExtensions);
        var restore = ActivationArgumentParser.FromLaunchArguments("--restore", SupportedExtensions);
        var outputPath = @"C:\Output Folder\转换 a+b%20&#.mp4";
        var outputArgument = ActivationArgumentParser.BuildJumpListOutputArgument(outputPath);
        var output = ActivationArgumentParser.FromLaunchArguments(outputArgument, SupportedExtensions);

        Assert.True(settings.OpenSettings);
        Assert.Null(settings.NavigationTarget);
        Assert.False(restore.HasWork);
        Assert.Equal(outputPath, output.OpenPath);
        Assert.Null(output.NavigationTarget);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("video")]
    [InlineData("audio")]
    [InlineData("image")]
    [InlineData("document")]
    [InlineData("ebook")]
    [InlineData("disc")]
    [InlineData("archive")]
    [InlineData("automation")]
    [InlineData("history")]
    [InlineData("settings")]
    public void LaunchArguments_NavigationDestinationsRoundTripAndAcceptCaseInsensitiveArguments(string destination)
    {
        var argument = ActivationArgumentParser.BuildJumpListNavigationArgument(destination.ToUpperInvariant());
        Assert.Equal($"--navigate:{destination}", argument);

        foreach (var value in new[] { argument, $"  {argument.ToUpperInvariant()}  " })
        {
            var request = ActivationArgumentParser.FromLaunchArguments(value, SupportedExtensions);

            Assert.Equal(destination, request.NavigationTarget);
            Assert.True(request.HasWork);
            Assert.Empty(request.FilePaths);
            Assert.Null(request.OpenPath);
            Assert.False(request.OpenSettings);
            Assert.Equal(NotificationAction.None, request.NotificationAction);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("1")]
    [InlineData("../history")]
    [InlineData("%68istory")]
    [InlineData(" history")]
    [InlineData("history --open-settings")]
    [InlineData("history \"C:\\video.mp4\"")]
    public void LaunchArguments_InvalidNavigationDoesNotImportFilesOrPerformOtherActions(string destination)
    {
        var fileChecks = 0;
        var request = ActivationArgumentParser.FromLaunchArguments(
            $"--navigate:{destination}", SupportedExtensions,
            _ => { fileChecks++; return true; });

        Assert.False(request.HasWork);
        Assert.Null(request.NavigationTarget);
        Assert.Empty(request.FilePaths);
        Assert.Equal(0, fileChecks);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("history --open-settings")]
    [InlineData(" history")]
    public void JumpListNavigationBuilder_RejectsUnsupportedDestinations(string destination)
    {
        Assert.Throws<ArgumentException>(() => ActivationArgumentParser.BuildJumpListNavigationArgument(destination));
    }

    [Fact]
    public void LaunchArguments_ProtocolUsesSameImportSemantics()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "from protocol.mp4");
        File.WriteAllText(path, "video");
        var value = $"formatfactory://open?path={Uri.EscapeDataString(path)}";

        var request = ActivationArgumentParser.FromLaunchArguments(value, SupportedExtensions);

        Assert.Equal([Path.GetFullPath(path)], request.FilePaths);
    }

    [Fact]
    public void NotificationAction_MakesRequestActionable()
    {
        var request = new ActivationRequest([], NotificationAction: NotificationAction.RetryFailed);

        Assert.True(request.HasWork);
    }

    [Theory]
    [InlineData("pause", (int)NotificationAction.Pause)]
    [InlineData("resume", (int)NotificationAction.Resume)]
    [InlineData("cancel", (int)NotificationAction.Cancel)]
    [InlineData("retry", (int)NotificationAction.Retry)]
    public void NotificationTaskActions_ParseTaskId(string value, int expected)
    {
        var taskId = Guid.NewGuid();

        var request = ActivationArgumentParser.FromNotificationArguments(
            new Dictionary<string, string>
            {
                ["action"] = value,
                ["taskId"] = taskId.ToString("D")
            });

        Assert.Equal((NotificationAction)expected, request.NotificationAction);
        Assert.Equal(taskId, request.TaskId);
        Assert.True(request.HasWork);
    }

    [Fact]
    public void NotificationOpenOutput_CarriesTaskAndPath()
    {
        var taskId = Guid.NewGuid();
        var outputPath = @"C:\Output Folder\result.pdf";

        var request = ActivationArgumentParser.FromNotificationArguments(
            new Dictionary<string, string>
            {
                ["action"] = "open-output",
                ["taskId"] = taskId.ToString("N"),
                ["path"] = outputPath
            });

        Assert.Equal(NotificationAction.OpenOutput, request.NotificationAction);
        Assert.Equal(taskId, request.TaskId);
        Assert.Equal(outputPath, request.OpenPath);
    }

    [Fact]
    public void NotificationLegacyActions_RemainCompatible()
    {
        var retry = ActivationArgumentParser.FromNotificationArguments(
            new Dictionary<string, string> { ["action"] = "retry-failed" });
        var output = ActivationArgumentParser.FromNotificationArguments(
            new Dictionary<string, string>
            {
                ["action"] = "open",
                ["path"] = @"C:\Output"
            });
        var settings = ActivationArgumentParser.FromNotificationArguments(
            new Dictionary<string, string> { ["action"] = "settings" });
        var pathOnly = ActivationArgumentParser.FromNotificationArguments(
            new Dictionary<string, string> { ["path"] = @"C:\LegacyOutput" });

        Assert.Equal(NotificationAction.RetryFailed, retry.NotificationAction);
        Assert.Equal(@"C:\Output", output.OpenPath);
        Assert.Equal(NotificationAction.None, output.NotificationAction);
        Assert.True(settings.OpenSettings);
        Assert.Equal(@"C:\LegacyOutput", pathOnly.OpenPath);
    }

    [Fact]
    public void NotificationTaskAction_WithoutValidTaskId_IsIgnored()
    {
        var request = ActivationArgumentParser.FromNotificationArguments(
            new Dictionary<string, string>
            {
                ["action"] = "pause",
                ["taskId"] = "not-a-guid"
            });

        Assert.False(request.HasWork);
        Assert.Null(request.TaskId);
        Assert.Equal(NotificationAction.None, request.NotificationAction);
    }

    [Fact]
    public void Protocol_BatchImportsAllPathsAndConsumesBatchFileOnce()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "one file.mp4");
        var second = Path.Combine(directory.Path, "second.MKV");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");

        var batchId = Guid.NewGuid().ToString("N");
        var batchDirectory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "ShellBatches");
        Directory.CreateDirectory(batchDirectory);
        var batchPath = Path.Combine(batchDirectory, $"{batchId}.json");
        File.WriteAllText(batchPath, JsonSerializer.Serialize(new[] { first, second, first }));
        var uri = new Uri($"formatfactory://open?batch={batchId}");

        var firstRequest = ActivationArgumentParser.FromProtocol(
            uri,
            SupportedExtensions,
            batchResolver: ActivationBatchStore.ReadAndDelete);
        var secondRequest = ActivationArgumentParser.FromProtocol(
            uri,
            SupportedExtensions,
            batchResolver: ActivationBatchStore.ReadAndDelete);

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], firstRequest.FilePaths);
        Assert.True(secondRequest.OpenSettings);
        Assert.False(File.Exists(batchPath));
    }
}
