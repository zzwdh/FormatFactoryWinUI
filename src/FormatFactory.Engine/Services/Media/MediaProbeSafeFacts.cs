using System.ComponentModel;
using System.Security.Cryptography;

namespace FormatFactoryWinUI.Services;

internal enum MediaProbeReadStatus
{
    NotChecked, Readable, Empty, Missing, AccessDenied, SharingViolation, IoFailure,
    SkippedUnsafePath, Busy, TimedOut
}

internal enum MediaProbeEngineState
{
    NotChecked, Hashed, Cached, SkippedUnsafePath, TooLarge, Changed, IoFailure, Busy, TimedOut
}

/// <summary>Best-effort observations after failure, not proof of the failed process's input or loaded image.</summary>
internal sealed record MediaProbeSafeFacts(
    MediaProbeReadStatus ReadStatus, int? SourceNativeError, long? SourceLength, int? SourceBytesRead,
    MediaProbeEngineState EngineState, string? EngineSha256, long? EngineLength, DateTimeOffset ObservedAtUtc)
{
    private const long MaximumEngineBytes = 256L * 1024 * 1024;
    private const int MaximumCachedEngines = 8;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);
    // RecallOnOpen and RecallOnDataAccess are deliberately included even on frameworks without named flags.
    private const FileAttributes UnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline |
        FileAttributes.Device | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, EngineCacheEntry> EngineCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CachedEngineOrder = new();
    private static int _collecting;

    internal static Task<MediaProbeSafeFacts> CollectAsync(string probePath, string inputPath, CancellationToken token) =>
        CollectWithBudgetAsync((publish, workerToken) => CollectCoreAsync(probePath, inputPath, publish, workerToken), Budget, token);

    // One slot bounds abandoned OS I/O as well as running work. A deadline limits caller waiting;
    // it cannot interrupt an OS metadata/open call. Until that worker actually exits, later calls report Busy.
    internal static async Task<MediaProbeSafeFacts> CollectWithBudgetAsync(
        Func<Action<MediaProbeSafeFacts>, CancellationToken, Task<MediaProbeSafeFacts>> collect,
        TimeSpan budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
            return Empty with { ReadStatus = MediaProbeReadStatus.Busy, EngineState = MediaProbeEngineState.Busy };

        var snapshot = Empty;
        var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        workerCancellation.CancelAfter(budget);
        var worker = Task.Run(async () =>
        {
            try { return await collect(value => Volatile.Write(ref snapshot, value), workerCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested) { return Deadline(Volatile.Read(ref snapshot)); }
            catch (Exception) { return Volatile.Read(ref snapshot); } // Diagnostics must not replace the original probe error.
            finally
            {
                workerCancellation.Dispose();
                Volatile.Write(ref _collecting, 0);
            }
        });
        try
        {
            var facts = await worker.WaitAsync(budget, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return facts;
        }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return Deadline(Volatile.Read(ref snapshot));
        }
    }

    private static MediaProbeSafeFacts Empty => new(MediaProbeReadStatus.NotChecked, null, null, null,
        MediaProbeEngineState.NotChecked, null, null, DateTimeOffset.UtcNow);

    private static MediaProbeSafeFacts Deadline(MediaProbeSafeFacts value) => value with
    {
        ReadStatus = value.ReadStatus == MediaProbeReadStatus.NotChecked ? MediaProbeReadStatus.TimedOut : value.ReadStatus,
        EngineState = value.EngineState == MediaProbeEngineState.NotChecked ? MediaProbeEngineState.TimedOut : value.EngineState
    };

    private static async Task<MediaProbeSafeFacts> CollectCoreAsync(string probePath, string inputPath,
        Action<MediaProbeSafeFacts> publish, CancellationToken token)
    {
        var facts = Empty;
        token.ThrowIfCancellationRequested();
        try
        {
            if (!TryGetLocalFile(inputPath, token, out var localInput))
                facts = facts with { ReadStatus = MediaProbeReadStatus.SkippedUnsafePath };
            else
            {
                token.ThrowIfCancellationRequested();
                await using var input = new FileStream(localInput, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                var sourceLength = input.Length;
                var read = await input.ReadAsync(new byte[1], token).ConfigureAwait(false);
                facts = facts with { ReadStatus = read == 0 ? MediaProbeReadStatus.Empty : MediaProbeReadStatus.Readable,
                    SourceLength = sourceLength, SourceBytesRead = read };
            }
        }
        catch (Exception error) when (IsFileObservationError(error))
        {
            var native = NativeCode(error);
            facts = facts with { ReadStatus = error is FileNotFoundException or DirectoryNotFoundException
                    ? MediaProbeReadStatus.Missing : error is UnauthorizedAccessException || native == 5
                    ? MediaProbeReadStatus.AccessDenied : native is 32 or 33 ? MediaProbeReadStatus.SharingViolation
                    : MediaProbeReadStatus.IoFailure, SourceNativeError = native };
        }
        facts = facts with { ObservedAtUtc = DateTimeOffset.UtcNow };
        publish(facts);
        token.ThrowIfCancellationRequested();
        try
        {
            if (!TryGetLocalFile(probePath, token, out var localProbe))
                facts = facts with { EngineState = MediaProbeEngineState.SkippedUnsafePath };
            else
            {
                token.ThrowIfCancellationRequested();
                var before = new FileInfo(localProbe);
                var length = before.Length;
                var modified = before.LastWriteTimeUtc.Ticks;
                facts = facts with { EngineLength = length };
                if (length > MaximumEngineBytes)
                    facts = facts with { EngineState = MediaProbeEngineState.TooLarge };
                else if (FindCached(localProbe, length, modified) is { } cached)
                    facts = facts with { EngineState = MediaProbeEngineState.Cached, EngineSha256 = cached };
                else
                {
                    token.ThrowIfCancellationRequested();
                    // Observation must not prevent engine replacement or updates. Read only the bounded initial length,
                    // then reject metadata changes; this identity still describes the post-failure file observation.
                    await using var engine = new FileStream(localProbe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (engine.Length != length)
                        facts = facts with { EngineState = MediaProbeEngineState.Changed };
                    else
                    {
                        var digest = await HashBoundedAsync(engine, length, token).ConfigureAwait(false);
                        var after = new FileInfo(localProbe);
                        if (digest is null || engine.Length != length || after.Length != length || after.LastWriteTimeUtc.Ticks != modified)
                            facts = facts with { EngineState = MediaProbeEngineState.Changed };
                        else
                        {
                            Cache(localProbe, length, modified, digest);
                            facts = facts with { EngineState = MediaProbeEngineState.Hashed, EngineSha256 = digest };
                        }
                    }
                }
            }
        }
        catch (Exception error) when (IsFileObservationError(error))
        { facts = facts with { EngineState = MediaProbeEngineState.IoFailure, EngineSha256 = null }; }
        publish(facts);
        return facts;
    }

    private static bool TryGetLocalFile(string path, CancellationToken token, out string local)
    {
        local = string.Empty;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var full = Path.GetFullPath(path);
        if (!IsEligibleLocalFile(full, token, root => new DriveInfo(root).DriveType, File.GetAttributes)) return false;
        local = full;
        return true;
    }

    internal static bool IsEligibleLocalFile(string full, CancellationToken token,
        Func<string, DriveType> driveType, Func<string, FileAttributes> attributesForPath)
    {
        var root = Path.GetPathRoot(full);
        // Reject UNC, device namespaces, alternate streams and non-fixed volumes before opening content.
        if (root is not { Length: 3 } || root[1] != ':' || full.AsSpan(2).Contains(':')) return false;
        token.ThrowIfCancellationRequested();
        if (driveType(root) != DriveType.Fixed) return false;
        var current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var rootAttributes = attributesForPath(root);
        if ((rootAttributes & UnsafeAttributes) != 0) return false;
        for (var i = 0; i < parts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            current = Path.Combine(current, parts[i]);
            var attributes = attributesForPath(current);
            if ((attributes & UnsafeAttributes) != 0 ||
                i == parts.Length - 1 && (attributes & FileAttributes.Directory) != 0) return false;
        }
        return true;
    }

    private static bool IsFileObservationError(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or Win32Exception;

    private static int? NativeCode(Exception error) => error is Win32Exception native ? native.NativeErrorCode :
        (error.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? error.HResult & 0xFFFF : null;

    private static async Task<string?> HashBoundedAsync(FileStream engine, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = await engine.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) return null;
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        // One extra byte detects growth without hashing or draining appended data.
        if (await engine.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) return null;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? FindCached(string path, long length, long modified)
    {
        lock (CacheLock)
            return EngineCache.TryGetValue(path, out var cached) && cached.Length == length && cached.Modified == modified
                ? cached.Sha256 : null;
    }

    private static void Cache(string path, long length, long modified, string digest)
    {
        lock (CacheLock)
        {
            if (!EngineCache.ContainsKey(path))
            {
                if (EngineCache.Count >= MaximumCachedEngines)
                    EngineCache.Remove(CachedEngineOrder.Dequeue());
                CachedEngineOrder.Enqueue(path);
            }
            EngineCache[path] = new(length, modified, digest);
        }
    }

    private sealed record EngineCacheEntry(long Length, long Modified, string Sha256);
}
