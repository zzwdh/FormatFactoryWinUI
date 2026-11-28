using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class FolderDiscovery
{
    internal sealed record Entry(string Path, long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks)
    {
        public AutomationFileFingerprint Fingerprint => new(Length, LastWriteTimeUtcTicks, CreationTimeUtcTicks);
    }

    // Callers enumerate on a background thread. Breaking out of a preview stops the scan.
    internal static IEnumerable<Entry> Enumerate(string root, bool recursive, bool skipHidden,
        bool supportedOnly, CancellationToken token)
    {
        using var worker = ConversionBackend.Start("discover-files", token);
        using var registration = token.Register(worker.Terminate);
        var process = worker.Process;
        var errors = process.StandardError.ReadToEndAsync();
        var request = new { Root = root, Recursive = recursive, SkipHidden = skipHidden,
            IgnoreInaccessible = skipHidden, Extensions = supportedOnly ? FormatCatalog.SupportedExtensions.ToArray() : null };
        try
        {
            JsonSerializer.Serialize(process.StandardInput.BaseStream, request);
            process.StandardInput.Close();
        }
        catch (IOException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        while (process.StandardOutput.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (line == "ok") break;
            yield return JsonSerializer.Deserialize<Entry>(line) ?? throw new InvalidDataException("Invalid scan result.");
        }
        worker.Exited.GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw ConversionBackend.DecodeFailure(errors.GetAwaiter().GetResult());
    }
}
