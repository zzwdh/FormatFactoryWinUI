using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed record ConversionProgress(double Percent, string Speed, string Remaining, string Message);
public sealed record ConversionResult(bool Success, bool Cancelled, string OutputPath, string Message, string LogPath);

public sealed class ConversionService
{
    private readonly ConcurrentDictionary<Guid, ActiveProcess> _activeProcesses = new();
    private readonly ConcurrentDictionary<Guid, byte> _cancelRequests = new();
    private readonly PresetService _presetService = new();
    private readonly HardwareCapabilitiesService _hardwareCapabilities = new();
    private readonly OutputPathService _outputPathService = new();

    public bool HasActiveTasks => !_activeProcesses.IsEmpty;

    public async Task<ConversionResult> ConvertAsync(
        MediaTask task,
        string ffmpegPath,
        AppSettings settings,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        _cancelRequests.TryRemove(task.Id, out _);
        var sourceExtension = NormalizeExtension(Path.GetExtension(task.InputPath));
        var targetExtension = NormalizeExtension(FormatCatalog.ExtensionFor(task.TargetFormat));
        if (task.ConversionMode == "无损" && sourceExtension == targetExtension)
        {
            return new ConversionResult(false, false, string.Empty, "源格式与目标格式相同，无损模式没有转换必要。", string.Empty);
        }

        var (finalPath, temporaryPath) = _outputPathService.Reserve(task, settings);
        var logPath = LogService.CreateTaskLogPath(task.Id);
        var warning = _presetService.GetPreservationWarning(task);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            report(new ConversionProgress(0, "—", "—", warning));
        }

        try
        {
            if (task.TargetFormat == "ICO" && task.Kind == MediaKind.Image)
            {
                var icoResult = await ConvertToIcoAsync(task, ffmpegPath, temporaryPath, logPath, report, cancellationToken);
                if (!icoResult.Success)
                {
                    return icoResult with { OutputPath = string.Empty };
                }
            }
            else
            {
                var attempts = await BuildAttemptsAsync(task, ffmpegPath, settings);
                var cpuThreads = Math.Max(4, Environment.ProcessorCount / Math.Max(1, settings.ParallelCount));

                ProcessRunResult? lastRun = null;
                foreach (var attempt in attempts)
                {
                    var backend = attempt.Backend;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_cancelRequests.ContainsKey(task.Id))
                    {
                        return CancelledResult(logPath);
                    }

                    DeleteIfExists(temporaryPath);
                    var backendName = backend switch
                    {
                        EncoderBackend.Nvidia => "NVIDIA NVENC",
                        EncoderBackend.Intel => "Intel Quick Sync",
                        EncoderBackend.Amd => "AMD AMF",
                        _ => $"CPU · {cpuThreads} 线程"
                    };
                    var pathName = attempt.HardwareDecode ? $"{backendName} · GPU 解码" : backendName;
                    report(new ConversionProgress(0, "—", "—", pathName));
                    var arguments = _presetService.BuildArguments(task, temporaryPath, backend, attempt.HardwareDecode, cpuThreads);
                    lastRun = await RunProcessAsync(task, ffmpegPath, arguments, logPath, report, cancellationToken);
                    if (lastRun.ExitCode == 0 && File.Exists(temporaryPath))
                    {
                        break;
                    }

                    if (_cancelRequests.ContainsKey(task.Id) || cancellationToken.IsCancellationRequested)
                    {
                        return CancelledResult(logPath);
                    }

                    if (backend != EncoderBackend.Cpu && !attempts.SkipWhile(item => item != attempt).Skip(1).Any(item => item.Backend == backend))
                    {
                        report(new ConversionProgress(0, "—", "—", $"{backendName} 失败 · 自动回退"));
                    }
                }

                if (lastRun is null || lastRun.ExitCode != 0 || !File.Exists(temporaryPath))
                {
                    var message = BuildFailureMessage(task, lastRun?.LastErrorLines ?? []);
                    DeleteIfExists(temporaryPath);
                    return new ConversionResult(false, false, string.Empty, message, logPath);
                }
            }

            File.Move(temporaryPath, finalPath, false);
            report(new ConversionProgress(100, "完成", "0 秒", "转换完成"));
            return new ConversionResult(true, false, finalPath, "转换完成", logPath);
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(temporaryPath);
            return CancelledResult(logPath);
        }
        catch (Exception ex)
        {
            DeleteIfExists(temporaryPath);
            LogService.AppendTask(logPath, $"{Environment.NewLine}异常: {ex}{Environment.NewLine}");
            return new ConversionResult(false, false, string.Empty, $"转换失败：{ex.Message}", logPath);
        }
        finally
        {
            _activeProcesses.TryRemove(task.Id, out _);
            _cancelRequests.TryRemove(task.Id, out _);
        }
    }

    private async Task<IReadOnlyList<EncoderAttempt>> BuildAttemptsAsync(MediaTask task, string ffmpegPath, AppSettings settings)
    {
        if (!settings.HardwareAcceleration || task.ConversionMode == "无损" || !_presetService.WillUseHardware(task))
        {
            return [new EncoderAttempt(EncoderBackend.Cpu, false)];
        }

        var hdr = task.Probe.IsHdr && task.TargetFormat is "MP4" or "MKV" or "MOV";
        var checks = await Task.WhenAll(
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Nvidia, hdr),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Intel, hdr),
            _hardwareCapabilities.CanEncodeAsync(ffmpegPath, EncoderBackend.Amd, hdr));
        var attempts = new List<EncoderAttempt>();
        if (checks[0])
        {
            attempts.Add(new EncoderAttempt(EncoderBackend.Nvidia, CanUseHardwareDecode(task)));
            if (attempts[^1].HardwareDecode)
            {
                attempts.Add(new EncoderAttempt(EncoderBackend.Nvidia, false));
            }
        }
        if (checks[1])
        {
            attempts.Add(new EncoderAttempt(EncoderBackend.Intel, CanUseHardwareDecode(task)));
            if (attempts[^1].HardwareDecode)
            {
                attempts.Add(new EncoderAttempt(EncoderBackend.Intel, false));
            }
        }
        if (checks[2])
        {
            attempts.Add(new EncoderAttempt(EncoderBackend.Amd, false));
        }
        attempts.Add(new EncoderAttempt(EncoderBackend.Cpu, false));
        return attempts;
    }

    private static bool CanUseHardwareDecode(MediaTask task) =>
        task.Kind == MediaKind.Video &&
        !(task.Probe.IsHdr && task.TargetFormat is "AVI" or "FLV") &&
        task.Probe.VideoCodec is "h264" or "hevc" or "av1" or "vp9" or "vp8" or "mpeg2video" or "vc1" or "mjpeg";

    public bool Pause(Guid taskId)
    {
        if (!_activeProcesses.TryGetValue(taskId, out var active) || active.Process.HasExited || active.IsSuspended)
        {
            return false;
        }

        try
        {
            var status = NtSuspendProcess(active.Process.Handle);
            if (status != 0)
            {
                return false;
            }
            active.IsSuspended = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Resume(Guid taskId)
    {
        if (!_activeProcesses.TryGetValue(taskId, out var active) || active.Process.HasExited || !active.IsSuspended)
        {
            return false;
        }

        try
        {
            var status = NtResumeProcess(active.Process.Handle);
            if (status != 0)
            {
                return false;
            }
            active.IsSuspended = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Cancel(Guid taskId)
    {
        _cancelRequests[taskId] = 1;
        if (_activeProcesses.TryGetValue(taskId, out var active))
        {
            TryTerminate(active);
        }
    }

    public void CancelAll()
    {
        foreach (var pair in _activeProcesses)
        {
            _cancelRequests[pair.Key] = 1;
            TryTerminate(pair.Value);
        }
    }

    private async Task<ConversionResult> ConvertToIcoAsync(
        MediaTask task,
        string ffmpegPath,
        string temporaryOutput,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", task.Id.ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var frames = new List<(int Size, string PngPath)>();
        try
        {
            for (var index = 0; index < sizes.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = sizes[index];
                var pngPath = Path.Combine(temporaryDirectory, $"icon-{size}.png");
                report(new ConversionProgress(index * 100d / sizes.Length, "处理中", "—", $"正在生成 {size}×{size} 图标…"));
                var arguments = _presetService.BuildIcoFrameArguments(task, size, pngPath);
                var run = await RunProcessAsync(task, ffmpegPath, arguments, logPath, _ => { }, cancellationToken);
                if (run.ExitCode != 0 || !File.Exists(pngPath))
                {
                    return new ConversionResult(false, false, string.Empty, BuildFailureMessage(task, run.LastErrorLines), logPath);
                }
                frames.Add((size, pngPath));
            }

            IcoBuilder.Build(frames, temporaryOutput);
            return new ConversionResult(true, false, temporaryOutput, "ICO 生成完成", logPath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, true);
                }
            }
            catch
            {
                // 临时文件将在系统清理时移除。
            }
        }
    }

    private async Task<ProcessRunResult> RunProcessAsync(
        MediaTask task,
        string executable,
        IReadOnlyList<string> arguments,
        string logPath,
        Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        LogService.AppendTask(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {executable} {string.Join(' ', arguments.Select(Quote))}{Environment.NewLine}{Environment.NewLine}");
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var active = new ActiveProcess(process);
        _activeProcesses[task.Id] = active;
        using var registration = cancellationToken.Register(() =>
        {
            _cancelRequests[task.Id] = 1;
            TryTerminate(active);
        });

        var errorLines = new Queue<string>();
        var progressTask = ReadProgressAsync(process.StandardOutput, task.Probe.Duration, report, cancellationToken);
        var errorTask = ReadErrorsAsync(process.StandardError, logPath, errorLines, cancellationToken);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(progressTask, errorTask);
        }
        finally
        {
            _activeProcesses.TryRemove(task.Id, out _);
        }

        return new ProcessRunResult(process.ExitCode, errorLines.ToArray());
    }

    private static async Task ReadProgressAsync(StreamReader reader, TimeSpan duration, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator]] = line[(separator + 1)..];
            if (!line.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var microseconds = GetLong(values, "out_time_us");
            if (microseconds == 0)
            {
                microseconds = GetLong(values, "out_time_ms");
            }
            var processed = TimeSpan.FromMilliseconds(microseconds / 1000d);
            var percent = duration > TimeSpan.Zero ? Math.Clamp(processed.TotalMilliseconds / duration.TotalMilliseconds * 100d, 0, 99.5) : 0;
            var speedText = values.TryGetValue("speed", out var speed) ? speed : "—";
            var remaining = CalculateRemaining(duration, processed, speedText);
            report(new ConversionProgress(percent, speedText, remaining, "正在转换…"));
            values.Clear();
        }
    }

    private static async Task ReadErrorsAsync(StreamReader reader, string logPath, Queue<string> lastLines, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            LogService.AppendTask(logPath, line + Environment.NewLine);
            lastLines.Enqueue(line);
            while (lastLines.Count > 20)
            {
                lastLines.Dequeue();
            }
        }
    }

    private static string CalculateRemaining(TimeSpan duration, TimeSpan processed, string speedText)
    {
        if (duration <= TimeSpan.Zero || !speedText.EndsWith('x') || !double.TryParse(speedText.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
        {
            return "—";
        }

        var seconds = Math.Max(0, (duration - processed).TotalSeconds / speed);
        var remaining = TimeSpan.FromSeconds(seconds);
        return remaining.TotalHours >= 1 ? remaining.ToString(@"h\:mm\:ss") : remaining.ToString(@"m\:ss");
    }

    private static string BuildFailureMessage(MediaTask task, IReadOnlyList<string> errorLines)
    {
        var detail = errorLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "FFmpeg 未返回详细原因。";
        if (task.ConversionMode == "无损")
        {
            return $"无法无损转换：{detail}";
        }
        if (Path.GetExtension(task.InputPath).Equals(".m4s", StringComparison.OrdinalIgnoreCase))
        {
            return $"M4S 转换失败：{detail}";
        }
        return $"转换失败：{detail}";
    }

    private static ConversionResult CancelledResult(string logPath) => new(false, true, string.Empty, "已取消", logPath);

    private static void TryTerminate(ActiveProcess active)
    {
        try
        {
            if (active.IsSuspended && !active.Process.HasExited)
            {
                NtResumeProcess(active.Process.Handle);
                active.IsSuspended = false;
            }
            if (!active.Process.HasExited)
            {
                active.Process.Kill(true);
            }
        }
        catch
        {
            // 进程可能已自然结束。
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 后续日志会记录主错误，清理失败不覆盖它。
        }
    }

    private static string NormalizeExtension(string extension) => extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension.ToLowerInvariant();
    private static long GetLong(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && long.TryParse(value, out var result) ? result : 0;
    private static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private sealed class ActiveProcess(Process process)
    {
        public Process Process { get; } = process;
        public bool IsSuspended { get; set; }
    }

    private sealed record ProcessRunResult(int ExitCode, IReadOnlyList<string> LastErrorLines);
    private sealed record EncoderAttempt(EncoderBackend Backend, bool HardwareDecode);
}
