using System.Diagnostics;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Uses a compatibility decoder while retaining the primary engine for final encoding.</summary>
public sealed class MediaCompatibilityDecodeService
{
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<ProcessExecutionResult>> _run;
    private readonly Func<string, string, CancellationToken, Task<MediaProbeResult>> _probe;

    public MediaCompatibilityDecodeService()
    {
        _run = (executable, arguments, logPath, token) => ProcessRunner.RunAsync(executable, arguments,
            TimeSpan.FromHours(12), new ProcessRunOptions
            {
                CaptureCompleteOutput = false, MaximumOutputBytes = BoundedProcessOutput.TailBytes,
                MaximumErrorBytes = BoundedProcessOutput.TailBytes, LogPath = logPath, UseConversionWorker = true
            }, token);
        _probe = new MediaProbeService().ProbeAsync;
    }

    internal MediaCompatibilityDecodeService(
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<ProcessExecutionResult>> run,
        Func<string, string, CancellationToken, Task<MediaProbeResult>> probe)
    { _run = run; _probe = probe; }

    public async Task<PreparedMediaInput> PrepareAsync(string fallbackFfmpegPath, string primaryFfmpegPath,
        MediaTask task, string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackFfmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryFfmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(workingDirectory);
        var privateDirectory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateDirectory);
        var intermediatePath = Path.Combine(privateDirectory, "decoded.mkv");
        var logPath = Path.Combine(LogService.LogsDirectory,
            $"task-{DateTime.Now:yyyyMMdd-HHmmss}-{task.Id:N}-compat-{Guid.NewGuid():N}.log");
        try
        {
            var arguments = await BuildArgumentsAsync(task, intermediatePath, cancellationToken);
            var run = await _run(fallbackFfmpegPath, arguments, logPath, cancellationToken);
            if (run.ExitCode != 0)
                throw new InvalidDataException(string.IsNullOrWhiteSpace(run.StandardError)
                    ? $"Compatibility decoding failed with exit code {run.ExitCode}."
                    : FfmpegErrorCapture.BuildFailureMessage(task, run.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)));
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(intermediatePath) || new FileInfo(intermediatePath).Length == 0)
                throw new InvalidDataException("The compatibility decoder produced no intermediate media.");
            var probe = await _probe(FfmpegLocator.GetFfprobePath(primaryFfmpegPath), intermediatePath, cancellationToken);
            ValidateIntermediate(task, probe);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedMediaInput(intermediatePath, probe, privateDirectory, logPath);
        }
        catch
        {
            PreparedMediaInput.Cleanup(privateDirectory);
            throw;
        }
    }

    internal static IReadOnlyList<string> BuildArguments(MediaTask task, string intermediatePath) =>
        BuildArgumentsAsync(task, intermediatePath, CancellationToken.None).GetAwaiter().GetResult();

    private static Task<string[]> BuildArgumentsAsync(MediaTask task, string intermediatePath, CancellationToken token) =>
        ConversionBackend.RequestAsync<string[]>("media-plan", new
        {
            action = "compatibility", input = task.InputPath, output = intermediatePath,
            maps = MediaInputPreflightService.RequiredStreams(task, task.Probe).Select(stream => stream.Map),
            audioOutput = FormatCatalog.IsAudioFormat(task.TargetFormat),
            imageOutput = FormatCatalog.IsImageFormat(task.TargetFormat) || FormatCatalog.IsFrameSequence(task.TargetFormat)
        }, token);

    internal static void ValidateIntermediate(MediaTask task, MediaProbeResult intermediate)
    {
        var audioOutput = FormatCatalog.IsAudioFormat(task.TargetFormat);
        var imageOutput = FormatCatalog.IsImageFormat(task.TargetFormat) || FormatCatalog.IsFrameSequence(task.TargetFormat);
        var expectedAudio = imageOutput ? 0 : CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe).Count;
        if (audioOutput && intermediate.VideoStreamIndex >= 0 || !audioOutput &&
            (intermediate.VideoCodec != "ffv1" || intermediate.Width <= 0 || intermediate.Height <= 0) ||
            intermediate.AudioTracks.Count != expectedAudio || intermediate.AudioTracks.Any(track => track.Codec != "pcm_s32le" || track.Channels <= 0) ||
            CompatibleAudioTrackPolicy.IsRequested(task) && CompatibleAudioTrackPolicy.IsSupportedOutput(task) &&
            intermediate.AudioTracks.Any(track => track.Channels != 2) ||
            audioOutput && intermediate.Duration <= TimeSpan.Zero)
            throw new InvalidDataException("The compatibility intermediate has missing or unexpected media streams.");
        if (!audioOutput && ((task.Probe.Width > 0 && intermediate.Width != task.Probe.Width) ||
            (task.Probe.Height > 0 && intermediate.Height != task.Probe.Height)))
            throw new InvalidDataException("The compatibility intermediate changed the source dimensions.");
    }
}

public sealed class PreparedMediaInput : IDisposable
{
    private string? _privateDirectory;
    internal PreparedMediaInput(string path, MediaProbeResult probe, string privateDirectory, string logPath)
    { Path = path; Probe = probe; _privateDirectory = privateDirectory; LogPath = logPath; }
    public string Path { get; }
    public MediaProbeResult Probe { get; }
    public string LogPath { get; }
    public void Dispose()
    {
        var directory = Interlocked.Exchange(ref _privateDirectory, null);
        if (directory is not null) Cleanup(directory);
    }
    internal static void Cleanup(string privateDirectory)
    {
        try { if (Directory.Exists(privateDirectory)) Directory.Delete(privateDirectory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Debug.WriteLine($"Compatibility intermediate cleanup failed: {exception.Message}"); }
    }
}
