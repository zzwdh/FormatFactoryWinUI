using System.Text.Json.Nodes;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum ArchiveFailureCode
{
    EngineUnavailable, UnsupportedInput, MissingVolume, PasswordRequired, PasswordSkipped,
    CorruptArchive, UnsafeEntry, InsufficientSpace, ValidationFailed, EngineFailed, InvalidOptions, OutputUnavailable
}

public sealed class ArchiveConversionException(ArchiveFailureCode code, string message) : IOException(message)
{
    public ArchiveFailureCode Code { get; } = code;
}

/// <summary>
/// Converts exactly one logical archive using a private 7-Zip process. No command line, report,
/// queue persistence, or temporary file contains a password. Publication follows validation.
/// </summary>
public sealed class ArchiveConversionService : IArchiveConverter
{
    private readonly string _enginePath;

    public ArchiveConversionService(string? enginePath = null) => _enginePath = enginePath ??
        Path.Combine(AppContext.BaseDirectory, "Engines", "7zip", "7z.exe");

    public bool IsAvailable => File.Exists(ConversionWorkerProcess.ExecutablePath) && File.Exists(_enginePath) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(_enginePath)!, "7z.dll"));

    public async Task<string> ConvertAsync(string sourcePath, string outputDirectory, ArchiveConversionOptions options,
        Func<ArchivePasswordRequest, CancellationToken, Task<string?>> requestPassword,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default,
        Func<string, string, CancellationToken, Task>? prepareCommit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requestPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(options.TargetFormat)) Fail(ArchiveFailureCode.InvalidOptions, "The target archive format is invalid.");
        ValidatePassword(options.OutputPassword);
        if (!string.IsNullOrEmpty(options.OutputPassword) && !ArchiveFormats.SupportsEncryption(options.TargetFormat))
            Fail(ArchiveFailureCode.InvalidOptions, "This archive format does not support password encryption.");
        if (!ArchiveFormats.IsValidOutputPassword(options.TargetFormat, options.OutputPassword))
            Fail(ArchiveFailureCode.InvalidOptions, "ZIP encryption requires at most 99 printable ASCII password characters. Use 7Z for Unicode passwords.");
        if (!ArchiveInput.IsSupported(sourcePath)) Fail(ArchiveFailureCode.UnsupportedInput, "The archive format is not supported.");
        if (!IsAvailable) Fail(ArchiveFailureCode.EngineUnavailable, "The bundled archive conversion engine is unavailable.");
        var primary = ArchiveInput.GetPrimaryPath(sourcePath);
        string destination;
        try { destination = Path.GetFullPath(outputDirectory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw WorkerFailure(ArchiveFailureCode.OutputUnavailable);
        }
        var scratch = Path.Combine(destination, ".ffarchive-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RunWorkerAsync(primary, scratch, options, requestPassword, progress, cancellationToken);
            var staged = Path.Combine(scratch, "converted" + ArchiveFormats.Extension(options.TargetFormat));
            var stem = ArchiveInput.OutputStem(primary);
            var extension = ArchiveFormats.Extension(options.TargetFormat);
            for (var suffix = 0; ; suffix++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var final = Path.Combine(destination, stem + (suffix == 0 ? "" : $" ({suffix})") + extension);
                if (File.Exists(final) || Directory.Exists(final) || string.Equals(final, primary, StringComparison.OrdinalIgnoreCase)) continue;
                // Persist a validated artifact's identity before making it visible. Callback
                // failures abort publication; a competing final name causes a new receipt.
                if (prepareCommit is not null) await prepareCommit(staged, final, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Atomic same-volume publication; never truncate an existing archive, including
                    // another source archive that was selected in the same batch.
                    OutputRecoveryService.Publish(staged, final, isDirectory: false);
                    progress?.Report(new(1, "completed"));
                    return final;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && File.Exists(staged) &&
                    (File.Exists(final) || Directory.Exists(final))) { }
            }
        }
        finally
        {
            await DeleteScratchAsync(scratch, destination).ConfigureAwait(false);
        }
    }

    private async Task RunWorkerAsync(string primary, string scratch, ArchiveConversionOptions options,
        Func<ArchivePasswordRequest, CancellationToken, Task<string?>> requestPassword, IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var lifetime = ConversionBackend.Start("archive-convert", cancellationToken);
            var process = lifetime.Process;
            // Release the request only after assigning the worker to its process job.
            // 7-Zip descendants share that lifetime, including while a prompt is open.
            var protocol = ExchangeAsync();
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await lifetime.WaitAndDrainAsync(Timeout.InfiniteTimeSpan, cancellationToken, protocol, errors);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !await protocol) throw WorkerFailure(ArchiveFailureCode.EngineFailed);

            async Task<bool> ExchangeAsync()
            {
                var request = new JsonObject
                {
                    ["engine"] = Path.GetFullPath(_enginePath), ["source"] = primary, ["scratch"] = scratch,
                    ["format"] = options.TargetFormat.ToString(), ["strict"] = options.StrictVerification
                };
                var input = process.StandardInput;
                await input.WriteLineAsync(request.ToJsonString().AsMemory(), cancellationToken);
                await input.WriteLineAsync((options.OutputPassword ?? "").AsMemory(), cancellationToken);
                var attempt = 0;
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } message)
                {
                    if (message == "ok") return true;
                    if (message.StartsWith("error ", StringComparison.Ordinal))
                    {
                        if (Enum.TryParse<ArchiveFailureCode>(message.AsSpan(6), out var code) && Enum.IsDefined(code))
                            throw WorkerFailure(code);
                        throw WorkerFailure(ArchiveFailureCode.EngineFailed);
                    }
                    if (message is "password required" or "password retry")
                    {
                        var password = await requestPassword(new(primary, message == "password retry", ++attempt), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (password is null) throw WorkerFailure(ArchiveFailureCode.PasswordSkipped);
                        ValidatePassword(password);
                        await input.WriteLineAsync(password.AsMemory(), cancellationToken);
                        continue;
                    }
                    double fraction = message switch
                    {
                        "inspecting" => 0.02, "extracting" => 0.12, "compressing" => 0.42,
                        "verifying" => 0.72, "comparing" => 0.86, _ => throw WorkerFailure(ArchiveFailureCode.EngineFailed)
                    };
                    if (message == "inspecting")
                    {
                        // Volume preflight has succeeded. Create scratch only after that
                        // point, so a missing part cannot create an output directory.
                        try { Directory.CreateDirectory(scratch); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            throw WorkerFailure(ArchiveFailureCode.OutputUnavailable);
                        }
                    }
                    progress?.Report(new(fraction, message));
                    cancellationToken.ThrowIfCancellationRequested();
                    await input.WriteLineAsync("ok".AsMemory(), cancellationToken);
                }
                return false;
            }
        }
        catch (System.ComponentModel.Win32Exception) { throw WorkerFailure(ArchiveFailureCode.EngineUnavailable); }
    }

    private static ArchiveConversionException WorkerFailure(ArchiveFailureCode code) => new(code, code switch
    {
        ArchiveFailureCode.EngineUnavailable => "The archive conversion engine could not be started.",
        ArchiveFailureCode.PasswordSkipped => "The archive was skipped because no password was supplied.",
        ArchiveFailureCode.MissingVolume => "One or more archive volumes are missing.",
        ArchiveFailureCode.CorruptArchive => "The archive is damaged, incomplete, or uses an unsupported compression method.",
        ArchiveFailureCode.UnsafeEntry => "The archive contains an unsafe entry or exceeds the supported limits.",
        ArchiveFailureCode.InsufficientSpace => "The output drive does not have enough free space for conversion and verification.",
        ArchiveFailureCode.ValidationFailed => "The converted archive did not pass verification.",
        ArchiveFailureCode.InvalidOptions => "The archive conversion options are invalid.",
        ArchiveFailureCode.OutputUnavailable => "The output folder is not accessible.",
        _ => "The archive conversion engine could not complete the conversion."
    });

    private static void ValidatePassword(string? password)
    {
        if (password is not null && (password.Length > 1024 || password.Any(char.IsControl)))
            Fail(ArchiveFailureCode.InvalidOptions, "A password cannot contain control characters or exceed 1024 characters.");
    }

    internal static async Task DeleteScratchAsync(string scratch, string destination)
    {
        // A terminated 7-Zip descendant can still hold its working directory or an output file
        // briefly after the worker exits. Retry only the private tree, even on caller cancellation.
        for (var delayMilliseconds = 25; ; delayMilliseconds *= 2)
        {
            DeleteScratchChild(scratch, destination);
            if (!Directory.Exists(scratch) || delayMilliseconds > 800) return;
            await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        }
    }

    private static void DeleteScratchChild(string path, string owner)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(owner);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return;
        try
        {
            // Do not follow a junction introduced by an archive or another process during cleanup.
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(full); return; }
            foreach (var child in Directory.EnumerateFileSystemEntries(full))
            {
                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) == 0) DeleteScratchChild(child, full);
                        else Directory.Delete(child);
                    }
                    else
                    {
                        if ((attributes & FileAttributes.ReparsePoint) == 0)
                            File.SetAttributes(child, attributes & ~FileAttributes.ReadOnly);
                        File.Delete(child);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            File.SetAttributes(full, File.GetAttributes(full) & ~FileAttributes.ReadOnly);
            Directory.Delete(full);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(ArchiveFailureCode code, string message) => throw new ArchiveConversionException(code, message);
}
