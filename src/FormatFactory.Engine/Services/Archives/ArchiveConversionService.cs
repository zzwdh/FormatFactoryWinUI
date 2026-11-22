using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private const int MaximumListingCharacters = 32 * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private readonly string _enginePath;

    public ArchiveConversionService(string? enginePath = null) => _enginePath = enginePath ??
        Path.Combine(AppContext.BaseDirectory, "Engines", "7zip", "7z.exe");

    public bool IsAvailable => File.Exists(_enginePath) && File.Exists(Path.Combine(Path.GetDirectoryName(_enginePath)!, "7z.dll"));

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
        ArchiveInput.ValidateVolumes(primary);
        var destination = string.Empty;
        var scratch = string.Empty;
        var content = string.Empty;
        try
        {
            destination = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(destination);
            scratch = Path.Combine(destination, ".ffarchive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            content = Path.Combine(scratch, "content");
            Directory.CreateDirectory(content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (scratch.Length > 0)
            {
                try { DeleteScratchChild(scratch, destination); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                { SafeDiagnostics.Report(cleanupError); }
            }
            throw new ArchiveConversionException(ArchiveFailureCode.OutputUnavailable, "The output folder is not accessible.");
        }
        try
        {
            progress?.Report(new(0.02, "inspecting"));
            var attempt = 0;
            string? inputPassword = null;
            IReadOnlyList<ArchiveEntry> sourceEntries;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var listing = await RunAsync(["l", "-slt", "-ba", primary], inputPassword, null, cancellationToken);
                if (listing.ExitCode != 0)
                {
                    if (NeedsPassword(listing, inputPassword is not null))
                    {
                        inputPassword = await AskAsync(inputPassword is not null);
                        continue;
                    }
                    // -ba hides the archive header, including its missing-volume diagnostic.
                    // Read the full technical header only on failure to distinguish missing
                    // final volumes from a generic corrupt archive.
                    var details = await RunAsync(["l", "-slt", primary], inputPassword, null, cancellationToken);
                    ThrowReadFailure(new(listing.ExitCode, listing.Output + details.Output, listing.Error + details.Error));
                }
                sourceEntries = ParseArchiveListing(listing.Output, primary);
                ValidateEntries(sourceEntries);
                if (sourceEntries.Any(entry => entry.Encrypted) && inputPassword is null)
                {
                    inputPassword = await AskAsync(false);
                    continue;
                }
                EnsureSpace(destination, sourceEntries, options, ArchiveInput.IsCompressedTar(primary));
                progress?.Report(new(0.12, "extracting"));
                var extraction = await ExtractAsync(primary, content, inputPassword, cancellationToken);
                if (extraction.ExitCode != 0)
                {
                    if (NeedsPassword(extraction, inputPassword is not null))
                    {
                        inputPassword = await AskAsync(true);
                        DeleteScratchChild(content, scratch);
                        Directory.CreateDirectory(content);
                        continue;
                    }
                    ThrowReadFailure(extraction);
                }
                ValidateExtractedTree(content, sourceEntries);
                break;
            }
            // A .tar.gz/.tar.xz has one transport layer around a TAR. Never recursively unpack
            // arbitrary archives found inside a user archive.
            if (ArchiveInput.IsCompressedTar(primary))
            {
                var outer = Snapshot(content);
                if (outer.Count != 1 || outer[0].Directory) Fail(ArchiveFailureCode.CorruptArchive, "The compressed TAR container is invalid.");
                var tar = Path.Combine(content, outer[0].Name.Replace('/', Path.DirectorySeparatorChar));
                var innerListing = await RunAsync(["l", "-slt", "-ba", "-ttar", tar], null, null, cancellationToken);
                if (innerListing.ExitCode != 0) ThrowReadFailure(innerListing);
                var innerEntries = ParseListing(innerListing.Output);
                ValidateEntries(innerEntries);
                EnsureSpace(destination, innerEntries, options, true);
                var inner = Path.Combine(scratch, "inner-content");
                Directory.CreateDirectory(inner);
                var extraction = await ExtractAsync(tar, inner, null, cancellationToken, "-ttar");
                if (extraction.ExitCode != 0) ThrowReadFailure(extraction);
                ValidateExtractedTree(inner, innerEntries);
                content = inner;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sourceSnapshot = Snapshot(content);
            Dictionary<string, string>? sourceHashes = null;
            if (options.StrictVerification) sourceHashes = await HashFilesAsync(content, sourceSnapshot, cancellationToken);
            progress?.Report(new(0.42, "compressing"));
            var staged = Path.Combine(scratch, "converted" + ArchiveFormats.Extension(options.TargetFormat));
            await CreateAsync(content, staged, options, scratch, cancellationToken);
            progress?.Report(new(0.72, "verifying"));
            var verifiedArchive = await VerifyAsync(staged, options, scratch, cancellationToken);
            var targetListing = await RunAsync(["l", "-slt", "-ba", verifiedArchive],
                verifiedArchive == staged ? options.OutputPassword : null, null, cancellationToken);
            if (targetListing.ExitCode != 0) Fail(ArchiveFailureCode.ValidationFailed, "The converted archive could not be inspected.");
            var targetEntries = ParseListing(targetListing.Output);
            ValidateEntries(targetEntries);
            CompareStructure(sourceSnapshot, ExpandParents(targetEntries));
            if (options.StrictVerification)
            {
                progress?.Report(new(0.86, "comparing"));
                var verification = Path.Combine(scratch, "verified-content");
                Directory.CreateDirectory(verification);
                var extraction = await ExtractAsync(verifiedArchive, verification,
                    verifiedArchive == staged ? options.OutputPassword : null, cancellationToken);
                if (extraction.ExitCode != 0) Fail(ArchiveFailureCode.ValidationFailed, "The converted archive failed the extraction check.");
                ValidateExtractedTree(verification, targetEntries);
                var verifiedSnapshot = Snapshot(verification);
                CompareStructure(sourceSnapshot, verifiedSnapshot);
                var targetHashes = await HashFilesAsync(verification, verifiedSnapshot, cancellationToken);
                if (sourceHashes!.Count != targetHashes.Count || sourceHashes.Any(pair =>
                    !targetHashes.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    Fail(ArchiveFailureCode.ValidationFailed, "The converted archive changed file contents.");
            }
            cancellationToken.ThrowIfCancellationRequested();
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
                    File.Move(staged, final, overwrite: false);
                    progress?.Report(new(1, "completed"));
                    return final;
                }
                catch (IOException) when (File.Exists(final) || Directory.Exists(final)) { }
            }

            async Task<string> AskAsync(bool incorrect)
            {
                var password = await requestPassword(new(primary, incorrect, ++attempt), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (password is null) throw new ArchiveConversionException(ArchiveFailureCode.PasswordSkipped, "The archive was skipped because no password was supplied.");
                ValidatePassword(password);
                return password;
            }
        }
        finally
        {
            DeleteScratchChild(scratch, destination);
        }
    }

    private async Task CreateAsync(string content, string staged, ArchiveConversionOptions options,
        string scratch, CancellationToken cancellationToken)
    {
        var type = options.TargetFormat switch
        {
            ArchiveFormat.Zip => "zip", ArchiveFormat.SevenZip => "7z", _ => "tar"
        };
        var tarWrapped = options.TargetFormat is ArchiveFormat.TarGZip or ArchiveFormat.TarXz;
        var first = tarWrapped ? Path.Combine(scratch, "payload.tar") : staged;
        var args = new List<string> { "a", "-t" + type, "-y", "-spd" };
        if (options.TargetFormat == ArchiveFormat.Zip) args.Add("-mcu=on");
        if (!string.IsNullOrEmpty(options.OutputPassword))
        {
            args.Add("-p");
            args.Add(options.TargetFormat == ArchiveFormat.Zip ? "-mem=AES256" : "-mhe=on");
        }
        args.Add(first);
        args.Add(".");
        var result = await RunAsync(args, options.OutputPassword, content, cancellationToken);
        if (result.ExitCode != 0) Fail(ArchiveFailureCode.EngineFailed, "The archive could not be created.");
        if (tarWrapped)
        {
            var wrapped = await RunAsync(["a", options.TargetFormat == ArchiveFormat.TarGZip ? "-tgzip" : "-txz",
                "-y", staged, first], null, scratch, cancellationToken);
            if (wrapped.ExitCode != 0) Fail(ArchiveFailureCode.EngineFailed, "The compressed TAR archive could not be created.");
        }
    }

    private async Task<string> VerifyAsync(string staged, ArchiveConversionOptions options, string scratch,
        CancellationToken cancellationToken)
    {
        var tested = await RunAsync(["t", staged], options.OutputPassword, null, cancellationToken);
        if (tested.ExitCode != 0) Fail(ArchiveFailureCode.ValidationFailed, "The converted archive failed its integrity check.");
        if (options.TargetFormat is not (ArchiveFormat.TarGZip or ArchiveFormat.TarXz)) return staged;
        var wrapper = Path.Combine(scratch, "verified-wrapper");
        Directory.CreateDirectory(wrapper);
        var outer = await RunAsync(["l", "-slt", "-ba", staged], null, null, cancellationToken);
        if (outer.ExitCode != 0) Fail(ArchiveFailureCode.ValidationFailed, "The converted container could not be inspected.");
        var entries = ParseArchiveListing(outer.Output, staged);
        ValidateEntries(entries);
        var extracted = await ExtractAsync(staged, wrapper, null, cancellationToken);
        if (extracted.ExitCode != 0) Fail(ArchiveFailureCode.ValidationFailed, "The converted container failed its integrity check.");
        ValidateExtractedTree(wrapper, entries);
        var files = Snapshot(wrapper);
        if (files.Count != 1 || files[0].Directory) Fail(ArchiveFailureCode.ValidationFailed, "The converted TAR container is invalid.");
        var tar = Path.Combine(wrapper, files[0].Name.Replace('/', Path.DirectorySeparatorChar));
        tested = await RunAsync(["t", "-ttar", tar], null, null, cancellationToken);
        if (tested.ExitCode != 0) Fail(ArchiveFailureCode.ValidationFailed, "The inner TAR failed its integrity check.");
        return tar;
    }

    private Task<ProcessResult> ExtractAsync(string archive, string directory, string? password,
        CancellationToken cancellationToken, string? type = null)
    {
        var args = new List<string> { "x", "-y", "-aos", "-o" + directory };
        if (type is not null) args.Add(type);
        args.Add(archive);
        return RunAsync(args, password, null, cancellationToken);
    }

    private async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, string? password,
        string? workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process();
        var info = process.StartInfo;
        info.FileName = _enginePath;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(_enginePath)!;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var argument in new[] { "-sccUTF-8", "-bb0", "-bd" }) info.ArgumentList.Add(argument);
        // Creation uses bare -p to request a password; opening must OMIT -p entirely:
        // bare -p on read means an explicitly empty password and suppresses its prompt.
        // Encrypted reads prompt naturally and consume stdin without a command-line secret.
        try
        {
            if (!process.Start()) Fail(ArchiveFailureCode.EngineFailed, "The archive engine could not be started.");
            using var registration = cancellationToken.Register(() => Kill(process));
            var output = ReadBoundedAsync(process.StandardOutput, MaximumListingCharacters);
            var error = ReadBoundedAsync(process.StandardError, 256 * 1024);
            if (password is not null)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync(password.AsMemory(), cancellationToken);
                    await process.StandardInput.WriteLineAsync(password.AsMemory(), cancellationToken);
                }
                catch (IOException) when (process.HasExited) { }
            }
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode, await output, await error);

            async Task<string> ReadBoundedAsync(StreamReader reader, int maximum)
            {
                try
                {
                    var buffer = new char[8192];
                    var result = new StringBuilder();
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                    {
                        if (result.Length + read > maximum) Fail(ArchiveFailureCode.UnsafeEntry, "The archive entry listing exceeds the supported limit.");
                        result.Append(buffer, 0, read);
                    }
                    return result.ToString();
                }
                catch { Kill(process); throw; }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ArchiveConversionException(ArchiveFailureCode.EngineUnavailable, "The archive engine could not be started.");
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
        }
    }

    private static void Kill(Process process)
    {
        ProcessTermination.TryTerminate(() => { if (!process.HasExited) process.Kill(entireProcessTree: true); });
    }

    private static bool NeedsPassword(ProcessResult result, bool passwordProvided)
    {
        var text = result.Output + result.Error;
        if (new[] { "Unsupported Method", "Missing volume", "Access is denied", "Not enough memory", "No space left", "Can not open output file" }
            .Any(message => text.Contains(message, StringComparison.OrdinalIgnoreCase))) return false;
        return text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Data Error in encrypted file", StringComparison.OrdinalIgnoreCase) ||
            !passwordProvided && text.Contains("Enter password", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowReadFailure(ProcessResult result)
    {
        var text = result.Output + result.Error;
        if (text.Contains("Missing volume", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Unexpected end of archive", StringComparison.OrdinalIgnoreCase) && text.Contains("volume", StringComparison.OrdinalIgnoreCase))
            Fail(ArchiveFailureCode.MissingVolume, "One or more archive volumes are missing.");
        Fail(ArchiveFailureCode.CorruptArchive, "The archive is damaged, incomplete, or uses an unsupported compression method.");
    }

    private static void ValidatePassword(string? password)
    {
        if (password is not null && (password.Length > 1024 || password.Any(char.IsControl)))
            Fail(ArchiveFailureCode.InvalidOptions, "A password cannot contain control characters or exceed 1024 characters.");
    }

    internal static IReadOnlyList<ArchiveEntry> ParseListing(string listing)
    {
        var entries = new List<ArchiveEntry>();
        Dictionary<string, string>? values = null;
        foreach (var line in listing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) { Flush(); continue; }
            var equals = line.IndexOf(" = ", StringComparison.Ordinal);
            if (equals < 0) continue; // Password prompts are outside the technical listing.
            var key = line[..equals];
            if (key == "Path")
            {
                if (values is not null) Fail(ArchiveFailureCode.UnsafeEntry, "The archive entry listing is ambiguous.");
                values = new(StringComparer.Ordinal);
            }
            if (values is not null && !values.TryAdd(key, line[(equals + 3)..]))
                Fail(ArchiveFailureCode.UnsafeEntry, "The archive contains ambiguous entry metadata.");
        }
        Flush();
        return entries;

        void Flush()
        {
            if (values is null) return;
            if (entries.Count >= MaximumEntries) Fail(ArchiveFailureCode.UnsafeEntry, "The archive contains too many entries.");
            var path = values["Path"].Replace('\\', '/').TrimEnd('/');
            var attributes = values.GetValueOrDefault("Attributes", "");
            var isDirectory = values.GetValueOrDefault("Folder") == "+" ||
                attributes.Split(' ')[0].Contains('D') ||
                attributes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(token => token.Length >= 10 && token[0] == 'd');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            if (!long.TryParse(values.GetValueOrDefault("Size", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                Fail(ArchiveFailureCode.UnsafeEntry, "The archive contains an invalid file size.");
            var link = values.Keys.Any(key => key.Contains("Link", StringComparison.OrdinalIgnoreCase) && values[key].Length != 0) ||
                values.GetValueOrDefault("Attributes", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Any(token => token.Length >= 10 && token[0] is 'l' or 's' or 'p' or 'b' or 'c') ||
                values.GetValueOrDefault("Mode", "").StartsWith('l') ||
                values.GetValueOrDefault("Anti") == "+";
            if (!link && isDirectory && path is "." or "") { values = null; return; }
            entries.Add(new(path, isDirectory, size,
                values.GetValueOrDefault("Encrypted") == "+", link));
            values = null;
        }
    }

    private static IReadOnlyList<ArchiveEntry> ParseArchiveListing(string listing, string archive)
    {
        var entries = ParseListing(listing);
        if (entries.Count != 0 || !(archive.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
            archive.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))) return entries;
        // XZ has no stored filename. 7-Zip's technical listing consequently has a Size
        // without a Path, and extraction derives the .tar name from the container name.
        var sizeLine = listing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .FirstOrDefault(line => line.StartsWith("Size = ", StringComparison.Ordinal));
        if (sizeLine is null) Fail(ArchiveFailureCode.CorruptArchive, "The XZ container does not declare its expanded size.");
        if (!long.TryParse(sizeLine[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            Fail(ArchiveFailureCode.CorruptArchive, "The XZ container does not declare its expanded size.");
        var name = Path.GetFileName(archive);
        name = name.EndsWith(".txz", StringComparison.OrdinalIgnoreCase) ? name[..^4] + ".tar" : name[..^3];
        return [new(name, false, size, false, false)];
    }

    internal static void ValidateEntries(IReadOnlyList<ArchiveEntry> entries)
    {
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            var name = entry.Name;
            if (entry.Link || string.IsNullOrEmpty(name) || name.Length > 30000 || name.StartsWith('/') ||
                Path.IsPathRooted(name) || name.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)))
                Fail(ArchiveFailureCode.UnsafeEntry, "The archive contains an unsafe path or link.");
            foreach (var segment in name.Split('/'))
            {
                var stem = segment.Split('.')[0];
                if (segment is "" or "." or ".." || segment.EndsWith('.') || char.IsWhiteSpace(segment[^1]) ||
                    stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                    stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                    stem[3] is >= '1' and <= '9')
                    Fail(ArchiveFailureCode.UnsafeEntry, "The archive contains a path that cannot be preserved safely on Windows.");
            }
            if (!names.TryAdd(name, entry.Directory)) Fail(ArchiveFailureCode.UnsafeEntry, "The archive contains duplicate or case-conflicting paths.");
            try { total = checked(total + entry.Size); }
            catch (OverflowException) { Fail(ArchiveFailureCode.UnsafeEntry, "The archive's expanded size is invalid."); }
        }
        foreach (var name in names.Keys)
        {
            var slash = name.IndexOf('/');
            while (slash >= 0)
            {
                if (names.TryGetValue(name[..slash], out var directory) && !directory)
                    Fail(ArchiveFailureCode.UnsafeEntry, "A file conflicts with an archive directory.");
                slash = name.IndexOf('/', slash + 1);
            }
        }
    }

    private static void EnsureSpace(string destination, IReadOnlyList<ArchiveEntry> entries,
        ArchiveConversionOptions options, bool sourceWrapped)
    {
        var total = entries.Sum(entry => entry.Size);
        // Extraction, atomic output staging, TAR wrappers, and optional full verification live
        // on the output volume. Reserve a conservative budget rather than fill the system disk.
        var copies = 2.1 + (sourceWrapped ? 1 : 0) +
            (options.TargetFormat is ArchiveFormat.TarGZip or ArchiveFormat.TarXz ? 1 : 0) +
            (options.StrictVerification ? 1 : 0);
        var needed = total * copies + 1024 * 1024;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(destination)!);
            if (drive.IsReady && needed > drive.AvailableFreeSpace)
                Fail(ArchiveFailureCode.InsufficientSpace, "The output drive does not have enough free space for conversion and verification.");
        }
        catch (ArgumentException) { } // Network shares may not expose local drive capacity.
    }

    private static List<ArchiveEntry> Snapshot(string root)
    {
        var result = new List<ArchiveEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Fail(ArchiveFailureCode.UnsafeEntry, "Archive links cannot be converted safely.");
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) Fail(ArchiveFailureCode.UnsafeEntry, "Archive links cannot be converted safely.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                result.Add(new(Path.GetRelativePath(root, item).Replace('\\', '/'), isDirectory,
                    isDirectory ? 0 : new FileInfo(item).Length, false, false));
                if (result.Count > MaximumEntries) Fail(ArchiveFailureCode.UnsafeEntry, "The archive contains too many entries.");
                if (isDirectory) pending.Push(item);
            }
        }
        ValidateEntries(result);
        return result;
    }

    private static List<ArchiveEntry> ExpandParents(IReadOnlyList<ArchiveEntry> entries)
    {
        var result = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var slash = entry.Name.LastIndexOf('/');
            while (slash >= 0)
            {
                var parent = entry.Name[..slash];
                result.TryAdd(parent, new(parent, true, 0, false, false));
                slash = parent.LastIndexOf('/');
            }
        }
        return result.Values.ToList();
    }

    private static void ValidateExtractedTree(string root, IReadOnlyList<ArchiveEntry> entries) =>
        CompareStructure(ExpandParents(entries), Snapshot(root));

    private static void CompareStructure(IReadOnlyList<ArchiveEntry> expected, IReadOnlyList<ArchiveEntry> actual)
    {
        var actualByName = actual.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        if (expected.Count != actual.Count || expected.Any(entry =>
            !actualByName.TryGetValue(entry.Name, out var other) || entry.Directory != other.Directory || !entry.Directory && entry.Size != other.Size))
            Fail(ArchiveFailureCode.ValidationFailed, "The converted archive does not preserve all file names, directories, and sizes.");
    }

    private static async Task<Dictionary<string, string>> HashFilesAsync(string root, IReadOnlyList<ArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry => !entry.Directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(Path.Combine(root, entry.Name.Replace('/', Path.DirectorySeparatorChar)),
                FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            result.Add(entry.Name, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        return result;
    }

    private static void DeleteScratchChild(string path, string owner)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(owner)) + Path.DirectorySeparatorChar;
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

    internal sealed record ArchiveEntry(string Name, bool Directory, long Size, bool Encrypted, bool Link);
    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
