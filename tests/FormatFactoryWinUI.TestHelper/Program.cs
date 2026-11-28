using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

var appDataRoot = args is ["app-data-lifecycle", var requestedRoot]
    ? requestedRoot
    : Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI.TestHelper", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
LogService.ConfigureAppDataRoot(appDataRoot);
return await TestHelperProgram.RunAsync(args);

internal static class TestHelperProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return 2;
        }

        return args[0] switch
        {
            "app-data-lifecycle" => InspectAppDataLifecycle(args),
            "echo" => Echo(args),
            "wait" => await WaitForeverAsync(),
            "progress-and-wait" => await ProgressAndWaitAsync(args),
            "pause-probe" => PauseProbe(args),
            "spawn-and-wait" => await SpawnChildAsync(args, waitForChild: true),
            "spawn-and-exit" => await SpawnChildAsync(args, waitForChild: false),
            "settings-writer" => await RunSettingsWriterAsync(args),
            "persistence-lock-hold" => await HoldPersistenceLockAsync(args),
            "flood-output" => await FloodOutputAsync(args),
            "filter-worker" => await SimulateFilterWorkerAsync(args),
            _ => 2
        };
    }

    private static async Task<int> HoldPersistenceLockAsync(string[] args)
    {
        if (args.Length != 5) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        FileStream? held = null;
        while (held is null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                held = args[2] == "legacy"
                    ? new FileStream(args[1], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous)
                    : PersistenceFileLock.Open(args[1], "settings");
            }
            catch (IOException) { await Task.Delay(20, timeout.Token); }
        }
        await using (held)
        {
            await File.WriteAllTextAsync(args[3], "held", timeout.Token);
            while (!File.Exists(args[4])) await Task.Delay(20, timeout.Token);
        }
        return 0;
    }

    private static int InspectAppDataLifecycle(string[] args)
    {
        if (args.Length != 2) return 2;
        var existsAfterConfiguration = Directory.Exists(args[1]);
        var root = LogService.AppDataRoot;
        var existsAfterPathResolution = Directory.Exists(root);
        LogService.WriteApp("app-data-lifecycle");
        LogService.FlushAll();
        Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Root = root,
            ExistsAfterConfiguration = existsAfterConfiguration,
            ExistsAfterPathResolution = existsAfterPathResolution,
            LogPath = LogService.AppLogPath
        }));
        return 0;
    }
    private static int Echo(string[] args)
    {
        Console.Out.WriteLine(args.ElementAtOrDefault(1) ?? string.Empty);
        Console.Error.WriteLine(args.ElementAtOrDefault(2) ?? string.Empty);
        return int.TryParse(args.ElementAtOrDefault(3), out var exitCode) ? exitCode : 0;
    }

    private static async Task<int> FloodOutputAsync(string[] args)
    {
        var bytes = int.Parse(args[1]);
        if (args.Length > 3) await File.WriteAllTextAsync(args[3], Environment.ProcessId.ToString());
        var block = Enumerable.Repeat((byte)'x', 8192).ToArray();
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        for (var offset = 0; offset < bytes; offset += block.Length)
        {
            var count = Math.Min(block.Length, bytes - offset);
            if (args[2] != "error") await output.WriteAsync(block.AsMemory(0, count));
            if (args[2] is "both" or "error") await error.WriteAsync(block.AsMemory(0, count));
        }
        await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes("END-OUT"));
        if (args[2] is "both" or "error") await error.WriteAsync(System.Text.Encoding.UTF8.GetBytes("END-ERR"));
        await output.FlushAsync();
        await error.FlushAsync();
        return args[2] == "wait" ? await WaitForeverAsync() : 0;
    }

    private static async Task<int> SimulateFilterWorkerAsync(string[] args)
    {
        using var request = System.Text.Json.JsonDocument.Parse(await Console.In.ReadToEndAsync());
        if (args[1] == "hang")
        {
            using var child = StartSelf("wait");
            await File.WriteAllTextAsync(args[2], child.Id.ToString());
            return await WaitForeverAsync();
        }
        if (args[1] == "crash") return 123;
        if (args[1] == "oversized") return await FloodOutputAsync(["flood-output", (17 * 1024 * 1024).ToString(), "wait"]);
        if (args[1] == "malformed") { Console.Write("{not-json"); return 0; }
        var response = new
        {
            Version = args[1] == "version" ? 2 : 1,
            IsAvailable = args[1] != "partial",
            Text = args[1] == "partial" ? "incomplete extraction" : "完成 extraction " + request.RootElement.GetProperty("Path").GetString(),
            Failure = args[1] == "partial" ? "chunk-read-failed" : (string?)null,
            HResult = args[1] == "partial" ? -1 : (int?)null
        };
        Console.Write(System.Text.Json.JsonSerializer.Serialize(response));
        return 0;
    }

    private static async Task<int> ProgressAndWaitAsync(string[] args)
    {
        await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString());
        Console.Out.WriteLine("out_time_us=1000000");
        Console.Out.WriteLine("progress=continue");
        await Console.Out.FlushAsync();
        return await WaitForeverAsync();
    }

    private static int PauseProbe(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 3) return 2;
        using var release = EventWaitHandle.OpenExisting(args[2]);
        File.WriteAllText(args[1], $"{Environment.ProcessId} {GetCurrentThreadId()}");
        Console.WriteLine("out_time_us=1");
        Console.WriteLine("progress=continue");
        Console.Out.Flush();
        return release.WaitOne(TimeSpan.FromSeconds(30)) ? 0 : 1;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static async Task<int> WaitForeverAsync()
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> SpawnChildAsync(string[] args, bool waitForChild)
    {
        if (args.Length != 2)
        {
            return 2;
        }

        using var child = StartSelf("wait");
        await File.WriteAllTextAsync(args[1], child.Id.ToString());
        if (waitForChild)
        {
            await child.WaitForExitAsync();
            return child.ExitCode;
        }

        return 0;
    }

    private static async Task<int> RunSettingsWriterAsync(string[] args)
    {
        if (args.Length != 6 ||
            !int.TryParse(args[2], out var writerId) ||
            !int.TryParse(args[3], out var iterations))
        {
            return 2;
        }

        var settingsPath = args[1];
        var startFlag = args[4];
        var readyFlag = args[5];
        await File.WriteAllTextAsync(readyFlag, writerId.ToString());

        using (var waitSource = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            while (!File.Exists(startFlag))
            {
                await Task.Delay(20, waitSource.Token);
            }
        }

        var service = new SettingsService(settingsPath);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            await service.SaveAsync(new AppSettings
            {
                CpuThreadsPerTask = writerId * 10_000 + iteration,
                ParallelCount = (writerId % 4) + 1,
                WatchFolderPath = $"writer-{writerId:D2}-iteration-{iteration:D2}-" + new string((char)('a' + writerId), 32_768)
            });
        }

        return 0;
    }

    private static Process StartSelf(params string[] arguments)
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = Environment.ProcessPath;
        }
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = "dotnet";
        }

        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start test helper child.");
    }
}


