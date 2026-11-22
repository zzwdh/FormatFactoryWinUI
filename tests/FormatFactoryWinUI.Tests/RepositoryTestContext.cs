using System.Diagnostics;

namespace FormatFactoryWinUI.Tests;

internal static class RepositoryTestContext
{
    private static readonly Lazy<string> RootValue = new(FindRepositoryRoot);

    public static string Root => RootValue.Value;

    // These are the projects split from the desktop app; the separate shell
    // extension has its own UI and is outside the app localization contract.
    public static IEnumerable<string> EnumerateApplicationSources(string pattern) =>
        new[] { "FormatFactoryWinUI", "FormatFactory.Core", "FormatFactory.Documents", "FormatFactory.Engine", "FormatFactory.Presentation" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(Root, "src", project), pattern, SearchOption.AllDirectories));
    public static string DotNetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

    public static string TestHelperAssembly
    {
        get
        {
            var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = outputDirectory.Parent?.Name
                ?? throw new InvalidOperationException("Unable to determine test build configuration.");
            var path = Path.Combine(
                Root,
                "tests",
                "FormatFactoryWinUI.TestHelper",
                "bin",
                configuration,
                "net11.0",
                "FormatFactoryWinUI.TestHelper.dll");
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException("The test helper project was not built.", path);
        }
    }

    public static Process StartTestHelper(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(DotNetHost)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(TestHelperAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start test helper.");
    }

    public static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            await Task.Delay(20, timeoutSource.Token);
        }
    }

    public static async Task AssertProcessExitedAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Process {processId} was still alive after {timeout}.");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "VERSION")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "packaging")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }
}

