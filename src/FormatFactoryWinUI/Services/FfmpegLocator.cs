using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FormatFactoryWinUI.Services;

public sealed class FfmpegLocator
{
    private const string EssentialsZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string EssentialsSha256Url = EssentialsZipUrl + ".sha256";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(15) };

    public async Task<(string Ffmpeg, string Ffprobe)?> LocateAsync(string? configuredPath = null)
    {
        foreach (var candidate in EnumerateCandidates(configuredPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = ResolveSymbolicLink(candidate);
            if (!File.Exists(resolved) || !await CanRunAsync(resolved, "-version"))
            {
                continue;
            }

            var ffprobe = Path.Combine(Path.GetDirectoryName(resolved)!, "ffprobe.exe");
            ffprobe = ResolveSymbolicLink(ffprobe);
            if (File.Exists(ffprobe) && await CanRunAsync(ffprobe, "-version"))
            {
                return (resolved, ffprobe);
            }
        }

        return null;
    }

    public async Task<(string Ffmpeg, string Ffprobe)?> DownloadAndInstallAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installRoot = Path.Combine(LogService.AppDataRoot, "FFmpeg");
        var downloadRoot = Path.Combine(LogService.AppDataRoot, "Downloads");
        var archivePath = Path.Combine(downloadRoot, "ffmpeg-release-essentials.zip");
        var stagingRoot = Path.Combine(downloadRoot, $"ffmpeg-staging-{Guid.NewGuid():N}");

        Directory.CreateDirectory(downloadRoot);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            LogService.WriteApp("未找到 FFmpeg，开始下载 Windows essentials 构建。");
            await DownloadFileAsync(EssentialsZipUrl, archivePath, progress, cancellationToken);
            await VerifySha256Async(archivePath, cancellationToken);

            ZipFile.ExtractToDirectory(archivePath, stagingRoot, overwriteFiles: true);
            var ffmpeg = Directory.EnumerateFiles(stagingRoot, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            var ffprobe = Directory.EnumerateFiles(stagingRoot, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (ffmpeg is null || ffprobe is null)
            {
                throw new InvalidDataException("下载包中未找到 ffmpeg.exe 或 ffprobe.exe。");
            }

            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }

            var binDirectory = Path.GetDirectoryName(ffmpeg)!;
            Directory.Move(binDirectory, installRoot);

            var installedFfmpeg = Path.Combine(installRoot, "ffmpeg.exe");
            var installedFfprobe = Path.Combine(installRoot, "ffprobe.exe");
            if (!await ValidatePairAsync(installedFfmpeg))
            {
                throw new InvalidDataException("FFmpeg 安装后校验失败。");
            }

            LogService.WriteApp($"FFmpeg 已自动安装: {installedFfmpeg}");
            progress?.Report(100);
            return (installedFfmpeg, installedFfprobe);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch
            {
                // 清理失败不影响已经完成的安装。
            }
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        var buffer = new byte[1024 * 128];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (total is > 0)
            {
                progress?.Report((int)Math.Clamp(received * 100 / total.Value, 0, 100));
            }
        }
    }

    private static async Task VerifySha256Async(string archivePath, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(EssentialsSha256Url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var checksumText = await response.Content.ReadAsStringAsync(cancellationToken);
        var expected = Regex.Match(checksumText, "[0-9a-fA-F]{64}").Value;
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidDataException("无法读取 FFmpeg 下载包校验值。");
        }

        await using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("FFmpeg 下载包校验失败，文件可能已损坏。");
        }
    }

    public async Task<bool> ValidatePairAsync(string ffmpegPath)
    {
        var resolved = ResolveSymbolicLink(ffmpegPath);
        var ffprobe = Path.Combine(Path.GetDirectoryName(resolved) ?? string.Empty, "ffprobe.exe");
        return File.Exists(resolved) && File.Exists(ffprobe) &&
               await CanRunAsync(resolved, "-version") && await CanRunAsync(ffprobe, "-version");
    }

    public static string GetFfprobePath(string ffmpegPath) =>
        Path.Combine(Path.GetDirectoryName(ResolveSymbolicLink(ffmpegPath)) ?? string.Empty, "ffprobe.exe");

    public static string ResolveSymbolicLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? path : info.ResolveLinkTarget(true)?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }

    private static IEnumerable<string> EnumerateCandidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory.Trim('"'), "ffmpeg.exe");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");

        var packagesRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packagesRoot))
        {
            foreach (var package in Directory.EnumerateDirectories(packagesRoot, "Gyan.FFmpeg*"))
            {
                foreach (var executable in Directory.EnumerateFiles(package, "ffmpeg.exe", SearchOption.AllDirectories))
                {
                    yield return executable;
                }
            }
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FFmpeg", "bin", "ffmpeg.exe");
    }

    private static async Task<bool> CanRunAsync(string path, string argument)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path, argument)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
