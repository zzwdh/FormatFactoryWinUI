using System.Collections.Concurrent;
using System.Diagnostics;

namespace FormatFactoryWinUI.Services;

internal sealed class HardwareCapabilitiesService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _encoderChecks = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> CanEncodeAsync(string ffmpegPath, EncoderBackend backend, bool hdr)
    {
        var encoder = (backend, hdr) switch
        {
            (EncoderBackend.Nvidia, false) => "h264_nvenc",
            (EncoderBackend.Nvidia, true) => "hevc_nvenc",
            (EncoderBackend.Intel, false) => "h264_qsv",
            (EncoderBackend.Intel, true) => "hevc_qsv",
            (EncoderBackend.Amd, false) => "h264_amf",
            (EncoderBackend.Amd, true) => "hevc_amf",
            _ => string.Empty
        };

        if (encoder.Length == 0)
        {
            return Task.FromResult(true);
        }

        var key = $"{ffmpegPath}|{encoder}";
        return _encoderChecks.GetOrAdd(key, _ => new Lazy<Task<bool>>(
            () => ProbeEncoderAsync(ffmpegPath, encoder),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static async Task<bool> ProbeEncoderAsync(string ffmpegPath, string encoder)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi",
                "-i", "color=size=64x64:rate=1", "-frames:v", "1", "-c:v", encoder,
                "-f", "null", "-"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
