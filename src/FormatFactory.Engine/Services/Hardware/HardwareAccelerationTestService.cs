using System.Text.Json;

namespace FormatFactoryWinUI.Services;

public sealed class HardwareAccelerationTestService
{
    private static HardwareDecodeReport? _currentReport;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly DecoderDefinition[] DecoderDefinitions =
    [
        new("h264", "H.264", "libx264", "matroska"),
        new("hevc", "HEVC", "libx265", "matroska"),
        new("vp9", "VP9", "libvpx-vp9", "webm"),
        new("av1", "AV1", "libaom-av1", "webm"),
        new("mpeg2video", "MPEG-2 Video", "mpeg2video", "mpegts"),
        new("vc1", "VC-1", null, null),
        new("mjpeg", "Motion JPEG", "mjpeg", "mjpeg")
    ];
    private static readonly HardwareBackend[] HardwareBackends =
    [
        new("cuda", "NVIDIA CUDA", "cuda"),
        new("qsv", "Intel Quick Sync", "qsv"),
        new("d3d11va", "Direct3D 11", "d3d11"),
        new("dxva2", "DXVA2", null)
    ];
    private readonly string _cachePath;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<HardwareProbeProcessResult>> _runCaptureAsync;
    private readonly Action<string> _writeLog;

    public HardwareAccelerationTestService()
        : this(
            Path.Combine(LogService.AppDataRoot, "hardware-decode-report.json"),
            RunCaptureAsync,
            LogService.WriteApp)
    {
    }

    internal HardwareAccelerationTestService(
        string cachePath,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<HardwareProbeProcessResult>> runCaptureAsync,
        Action<string>? writeLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentNullException.ThrowIfNull(runCaptureAsync);
        _cachePath = cachePath;
        _runCaptureAsync = runCaptureAsync;
        _writeLog = writeLog ?? (_ => { });
    }

    public static void SetCurrentReport(HardwareDecodeReport? report) => _currentReport = report;

    public static bool SupportsHardwareDecode(string codec)
    {
        var report = _currentReport;
        if (report is null)
        {
            return true;
        }

        return report.Decoders?.FirstOrDefault(item =>
            item.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase)) is not { } capability ||
            !capability.TestCompleted ||
            capability.Supported;
    }

    public async Task<HardwareDecodeReport?> LoadCachedAsync(
        string ffmpegPath,
        HardwareInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        try
        {
            if (!File.Exists(_cachePath))
            {
                SetCurrentReport(null);
                return null;
            }

            await using var stream = File.OpenRead(_cachePath);
            var report = await JsonSerializer.DeserializeAsync<HardwareDecodeReport>(stream, JsonOptions);
            if (report?.SchemaVersion != HardwareDecodeReport.CurrentSchemaVersion ||
                report.TestCompleted != true ||
                report.ContextFingerprint is null ||
                report.Decoders is null)
            {
                SetCurrentReport(null);
                _writeLog(LocalizationService.T("OperationalLogHardwareDecodeCacheIncompatible"));
                return null;
            }

            var currentFingerprint = HardwareDecodeContextFingerprint.Create(inventory, ffmpegPath);
            if (!report.ContextFingerprint.Matches(currentFingerprint))
            {
                SetCurrentReport(null);
                _writeLog(LocalizationService.T("OperationalLogHardwareDecodeCacheContextChanged"));
                return null;
            }

            SetCurrentReport(report);
            return report;
        }
        catch (Exception ex)
        {
            SetCurrentReport(null);
            _writeLog(LocalizationService.T("OperationalLogHardwareDecodeReportReadFailed", ex.Message));
            return null;
        }
    }

    public async Task SaveAsync(HardwareDecodeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.TestCompleted || report.ContextFingerprint is null)
        {
            throw new InvalidOperationException(LocalizationService.T("HardwareSettingsIncompleteReportNotSaved"));
        }

        report.SchemaVersion = HardwareDecodeReport.CurrentSchemaVersion;
        report.Decoders ??= [];

        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions);
            }
            File.Move(temporaryPath, _cachePath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // 报告缓存不是转换结果，清理失败不应影响应用运行。
            }
        }
    }

    public Task<HardwareDecodeReport> RunAsync(
        string ffmpegPath,
        CancellationToken cancellationToken = default) =>
        RunAsync(ffmpegPath, HardwareInventoryService.GetCurrent(), cancellationToken);

    public async Task<HardwareDecodeReport> RunAsync(
        string ffmpegPath,
        HardwareInventory inventory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var hardwareProbe = await _runCaptureAsync(ffmpegPath, ["-hide_banner", "-hwaccels"], cancellationToken);
        EnsureInitialProbeSucceeded("-hwaccels", hardwareProbe);
        var decoderProbe = await _runCaptureAsync(ffmpegPath, ["-hide_banner", "-decoders"], cancellationToken);
        EnsureInitialProbeSucceeded("-decoders", decoderProbe);

        var availableHardware = ParseHardwareBackends(hardwareProbe.Output);
        var availableDecoders = ParseVideoDecoders(decoderProbe.Output);
        var report = new HardwareDecodeReport
        {
            SchemaVersion = HardwareDecodeReport.CurrentSchemaVersion,
            TestedAt = DateTimeOffset.Now,
            ContextFingerprint = HardwareDecodeContextFingerprint.Create(inventory, ffmpegPath)
        };
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", $"ha-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            foreach (var definition in DecoderDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var softwareSupported = availableDecoders.Contains(definition.Decoder);
                if (!softwareSupported)
                {
                    report.Decoders.Add(CompletedUnsupported(definition, softwareSupported: false));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(definition.Encoder) || string.IsNullOrWhiteSpace(definition.Muxer))
                {
                    report.Decoders.Add(UntestedHardware(definition));
                    continue;
                }

                if (availableHardware.Count == 0)
                {
                    report.Decoders.Add(CompletedUnsupported(definition, softwareSupported: true));
                    continue;
                }

                var samplePath = Path.Combine(temporaryDirectory, $"{definition.Decoder}.sample");
                var sampleArguments = new List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-f", "lavfi", "-i", "color=size=96x96:rate=1", "-frames:v", "1",
                    "-c:v", definition.Encoder, "-f", definition.Muxer, samplePath
                };
                var sample = await _runCaptureAsync(ffmpegPath, sampleArguments, cancellationToken);
                if (!sample.Success || !File.Exists(samplePath))
                {
                    report.Decoders.Add(UntestedHardware(definition));
                    continue;
                }

                var workingBackends = new List<HardwareBackend>();
                foreach (var backend in HardwareBackends.Where(item => availableHardware.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var decodeArguments = new List<string>
                    {
                        "-hide_banner", "-loglevel", "error", "-nostdin",
                        "-hwaccel", backend.Name
                    };
                    if (!string.IsNullOrWhiteSpace(backend.OutputFormat))
                    {
                        decodeArguments.AddRange(["-hwaccel_output_format", backend.OutputFormat]);
                    }
                    decodeArguments.AddRange(["-i", samplePath, "-frames:v", "1", "-f", "null", "-"]);
                    var decode = await _runCaptureAsync(ffmpegPath, decodeArguments, cancellationToken);
                    if (decode.Success)
                    {
                        workingBackends.Add(backend);
                    }
                }

                report.Decoders.Add(new HardwareDecodeCapability
                {
                    Codec = definition.Decoder,
                    DisplayName = definition.DisplayName,
                    Supported = workingBackends.Count > 0,
                    Backend = string.Join(" / ", workingBackends.Select(backend => backend.DisplayName)),
                    TestCompleted = true,
                    SoftwareTestCompleted = true,
                    SoftwareSupported = true,
                    BackendIds = workingBackends.Select(backend => backend.Name).ToList(),
                    TestedBackendIds = HardwareBackends.Select(backend => backend.Name).ToList()
                });
            }

            report.TestCompleted = true;
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
                // 临时样本会由系统清理。
            }
        }

        SetCurrentReport(report);
        return report;
    }

    private static HardwareDecodeCapability CompletedUnsupported(
        DecoderDefinition definition,
        bool softwareSupported) => new()
    {
        Codec = definition.Decoder,
        DisplayName = definition.DisplayName,
        Supported = false,
        TestCompleted = true,
        SoftwareTestCompleted = true,
        SoftwareSupported = softwareSupported,
        TestedBackendIds = HardwareBackends.Select(backend => backend.Name).ToList()
    };

    private static HardwareDecodeCapability UntestedHardware(DecoderDefinition definition) => new()
    {
        Codec = definition.Decoder,
        DisplayName = definition.DisplayName,
        Supported = false,
        TestCompleted = false,
        SoftwareTestCompleted = true,
        SoftwareSupported = true
    };

    private static void EnsureInitialProbeSucceeded(string command, HardwareProbeProcessResult result)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException(LocalizationService.T(
                "HardwareSettingsProbeCommandFailed",
                command,
                result.Error));
        }
    }

    private static HashSet<string> ParseHardwareBackends(string output) => output
        .Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => HardwareBackends.Any(backend => backend.Name.Equals(line, StringComparison.OrdinalIgnoreCase)))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ParseVideoDecoders(string output)
    {
        var decoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && columns[0].Length >= 2 && columns[0][0] == 'V')
            {
                decoders.Add(columns[1]);
            }
        }
        return decoders;
    }
    private static async Task<HardwareProbeProcessResult> RunCaptureAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(6),
                cancellationToken);
            return new HardwareProbeProcessResult(
                result.ExitCode == 0,
                result.StandardOutput + Environment.NewLine + result.StandardError,
                result.StandardError);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new HardwareProbeProcessResult(
                false,
                string.Empty,
                LocalizationService.T("GeneralServiceProbeTimedOut"));
        }
        catch (Exception ex)
        {
            return new HardwareProbeProcessResult(false, string.Empty, ex.Message);
        }
    }

    private sealed record DecoderDefinition(string Decoder, string DisplayName, string? Encoder, string? Muxer);
    private sealed record HardwareBackend(string Name, string DisplayName, string? OutputFormat);
}

internal sealed record HardwareProbeProcessResult(bool Success, string Output, string Error);
