using System.Text.Json;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AppDataPathTests
{
    [Fact]
    public void ConfiguredRootIsStableWhenServicesShareTheProcess()
    {
        var root = LogService.AppDataRoot;
        LogService.ConfigureAppDataRoot(root + Path.DirectorySeparatorChar);

        Assert.Throws<InvalidOperationException>(() =>
            LogService.ConfigureAppDataRoot(Path.Combine(root, "another-host")));
        Assert.Equal(root, AppDataPaths.Root);
        Assert.Equal(root, LogService.AppDataRoot);
    }

    [Fact]
    public async Task HostConfigurationHasNoStorageSideEffectsUntilLoggingStarts()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "app-data");
        var result = await ProcessRunner.RunAsync(
            RepositoryTestContext.DotNetHost,
            [RepositoryTestContext.TestHelperAssembly, "app-data-lifecycle", root],
            TimeSpan.FromSeconds(20));

        Assert.True(result.ExitCode == 0, result.StandardError);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(root, report.RootElement.GetProperty("Root").GetString());
        Assert.False(report.RootElement.GetProperty("ExistsAfterConfiguration").GetBoolean());
        Assert.False(report.RootElement.GetProperty("ExistsAfterPathResolution").GetBoolean());
        var logPath = report.RootElement.GetProperty("LogPath").GetString()!;
        Assert.Equal(Path.Combine(root, "Logs", "application.log"), logPath);
        Assert.Contains("app-data-lifecycle", await File.ReadAllTextAsync(logPath), StringComparison.Ordinal);
        Assert.Equal([logPath], Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }
}
