using System.ComponentModel;
using System.Globalization;

namespace FormatFactoryWinUI.Services;

public static class IcoBuilder
{
    public static void Build(IEnumerable<(int Size, string PngPath)> frames, string outputPath)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $".{Guid.NewGuid():N}.ico");
        try
        {
            BuildAsync(frames, temporary, CancellationToken.None).GetAwaiter().GetResult();
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static async Task BuildAsync(IEnumerable<(int Size, string PngPath)> frames, string temporaryOutput,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { ConversionWorkerProcess.ProtocolVersionArgument, "ico-pack", "--output", temporaryOutput };
        foreach (var (size, path) in frames)
        {
            arguments.Add(size.ToString(CultureInfo.InvariantCulture));
            arguments.Add(path);
        }
        var result = await ProcessRunner.RunAsync(ConversionWorkerProcess.ExecutablePath, arguments,
            Timeout.InfiniteTimeSpan, new ProcessRunOptions { MaximumOutputBytes = 1024, MaximumErrorBytes = 1024 },
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode == 0 && result.StandardOutput.TrimEnd('\r', '\n') == "ok" &&
            result.StandardError.Length == 0 && !result.OutputTruncated) return;

        var error = result.StandardError.Trim().Split(' ');
        if (error is ["error", "io", var code] && int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            var cause = new Win32Exception(number);
            throw new IOException(cause.Message, cause);
        }
        throw new InvalidDataException(LocalizationService.T("ConversionUiToolExecutionFailed",
            ConversionWorkerProcess.FileName, result.ExitCode, result.StandardError.Trim()));
    }
}
