using System.Collections.Concurrent;

namespace FormatFactoryWinUI.Services;

internal sealed class HardwareCapabilitiesService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _encoderChecks = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, EncoderBackend, string, bool, Task<bool>> _probeEncoder;

    internal HardwareCapabilitiesService(Func<string, EncoderBackend, string, bool, Task<bool>>? probeEncoder = null) =>
        _probeEncoder = probeEncoder ?? ProbeEncoderAsync;

    public Task<bool> CanEncodeAsync(string ffmpegPath, EncoderBackend backend, bool hdr, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoder = (backend, hdr) switch
        {
            (EncoderBackend.Nvidia, false) => "h264_nvenc",
            (EncoderBackend.Nvidia, true) => "hevc_nvenc",
            (EncoderBackend.Intel, false) => "h264_qsv",
            (EncoderBackend.Intel, true) => "hevc_qsv",
            (EncoderBackend.Amd, false) => "h264_amf",
            (EncoderBackend.Amd, true) => "hevc_amf",
            (EncoderBackend.MediaFoundation, false) => "h264_mf",
            (EncoderBackend.MediaFoundation, true) => "hevc_mf",
            _ => string.Empty
        };

        if (encoder.Length == 0)
        {
            return Task.FromResult(true);
        }

        var key = $"{ffmpegPath}|{encoder}|{hdr}";
        var sharedProbe = _encoderChecks.GetOrAdd(key, _ => new Lazy<Task<bool>>(
            () => _probeEncoder(ffmpegPath, backend, encoder, hdr),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        // A caller owns its wait; cancelling it must not poison another task's cached probe.
        return sharedProbe.WaitAsync(cancellationToken);
    }

    private static async Task<bool> ProbeEncoderAsync(string ffmpegPath, EncoderBackend backend, string encoder, bool hdr)
    {
        try
        {
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi",
                "-i", "color=size=64x64:rate=1", "-frames:v", "1", "-c:v", encoder
            };
            if (backend == EncoderBackend.MediaFoundation)
            {
                arguments.AddRange(["-hw_encoding", "1", "-rate_control", "quality", "-quality", "70"]);
                if (hdr)
                {
                    arguments.AddRange(["-profile:v", "main10", "-pix_fmt", "p010le"]);
                }
            }
            arguments.AddRange(["-f", "null", "-"]);

            var result = await ProcessRunner.RunAsync(ffmpegPath, arguments, TimeSpan.FromSeconds(8));
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
