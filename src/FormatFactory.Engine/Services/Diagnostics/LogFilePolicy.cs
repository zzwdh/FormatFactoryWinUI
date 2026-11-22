using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Services;

/// <summary>Rotates individual streams and prunes only inactive, application-owned log files.</summary>
internal sealed class LogFilePolicy
{
    internal const long DefaultMaximumFileBytes = 8 * 1024 * 1024;
    internal const long DefaultDirectoryBudgetBytes = 256 * 1024 * 1024;
    private static readonly Regex OwnedName = new(
        @"^(?:application|task-\d{8}-\d{6}-[a-fA-F0-9]{32})\.log(?:\.stderr)?(?:\.[12])?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _maintenanceGate = new();
    private readonly object _activeGate = new();
    private readonly Func<DateTimeOffset> _clock;
    private long _nextMaintenanceTicks;
    private int _maintenanceScheduled;

    internal static LogFilePolicy Shared { get; } = new(Path.Combine(AppDataPaths.Root, "Logs"));

    internal LogFilePolicy(string root, long maximumFileBytes = DefaultMaximumFileBytes,
        int backups = 2, long directoryBudgetBytes = DefaultDirectoryBudgetBytes,
        TimeSpan? retention = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(backups, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(backups, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(directoryBudgetBytes);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        MaximumFileBytes = maximumFileBytes;
        Backups = backups;
        DirectoryBudgetBytes = directoryBudgetBytes;
        Retention = retention ?? TimeSpan.FromDays(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal string Root { get; }
    internal long MaximumFileBytes { get; }
    internal int Backups { get; }
    internal long DirectoryBudgetBytes { get; }
    internal TimeSpan Retention { get; }

    internal IDisposable Protect(string path)
    {
        path = Path.GetFullPath(path);
        lock (_activeGate) _active[path] = _active.GetValueOrDefault(path) + 1;
        return new Protection(this, path);
    }

    private void Release(string path)
    {
        lock (_activeGate)
        {
            if (!_active.TryGetValue(path, out var count)) return;
            if (count == 1) _active.Remove(path);
            else _active[path] = count - 1;
        }
    }

    internal void AppendText(string path, string content)
    {
        using var stream = new RollingLogStream(path, this, createNew: false);
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    internal void Prune(bool force = false)
    {
        if (!Monitor.TryEnter(_maintenanceGate)) return;
        try
        {
            var now = _clock();
            if (!force && now.UtcTicks < Volatile.Read(ref _nextMaintenanceTicks)) return;
            Volatile.Write(ref _nextMaintenanceTicks, now.AddMinutes(5).UtcTicks);
            if (!IsSafeDirectory(Root) || !Directory.Exists(Root)) return;
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly))
            {
                if (!OwnedName.IsMatch(Path.GetFileName(path))) continue;
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0) files.Add(info);
            }
            var total = files.Sum(file => file.Length);
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (now - file.LastWriteTimeUtc <= Retention && total <= DirectoryBudgetBytes) continue;
                try
                {
                    // Only the individual protection check + deletion is mutually
                    // exclusive with starting a new writer; enumeration stays outside.
                    lock (_activeGate)
                    {
                        if (!IsProtected(file.FullName) && TryDeleteInactive(file.FullName)) total -= file.Length;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Debug.WriteLine($"Log retention skipped: {error.GetType().Name}"); }
        finally { Monitor.Exit(_maintenanceGate); }
    }

    internal void SchedulePrune()
    {
        if (_clock().UtcTicks < Volatile.Read(ref _nextMaintenanceTicks) ||
            Interlocked.CompareExchange(ref _maintenanceScheduled, 1, 0) != 0) return;
        // Process output readers can be entered on a host thread before their
        // first await. Directory maintenance always starts on a background worker.
        _ = Task.Run(() =>
        {
            try { Prune(); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Log retention skipped: {error.GetType().Name}"); }
            finally { Volatile.Write(ref _maintenanceScheduled, 0); }
        });
    }

    private bool TryDeleteInactive(string path)
    {
        if (!IsSafeDirectory(Root) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        if (!OperatingSystem.IsWindows())
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        // Open the entry itself, without following a replaced symlink. Exclusive
        // sharing protects other processes' active logs. Mark this same handle for
        // deletion only after verifying its identity, eliminating unlock/delete races.
        using var handle = CreateFile(path, 0x80000000 | 0x00010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information) ||
            (information.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var resolved = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, resolved, (uint)resolved.Capacity, 0);
        if (length == 0 || length >= resolved.Capacity) return false;
        var finalPath = resolved.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) finalPath = @"\\" + finalPath[8..];
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
        if (!finalPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return false;
        var disposition = new FileDisposition { DeleteFile = true };
        return SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] internal bool DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint size);

    private bool IsProtected(string path)
    {
        if (Path.GetFileName(path).Equals("application.log", StringComparison.OrdinalIgnoreCase)) return true;
        var basePath = path.EndsWith(".1", StringComparison.Ordinal) || path.EndsWith(".2", StringComparison.Ordinal)
            ? path[..^2] : path;
        return _active.ContainsKey(basePath) || _active.ContainsKey(path);
    }

    internal static bool IsSafeDirectory(string directory)
    {
        for (string? current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private sealed class Protection(LogFilePolicy policy, string path) : IDisposable
    {
        private LogFilePolicy? _policy = policy;
        public void Dispose() => Interlocked.Exchange(ref _policy, null)?.Release(path);
    }
}

/// <summary>One bounded rolling stream. Callers keep draining their producer after rotation.</summary>
internal sealed class RollingLogStream : IDisposable
{
    private readonly string _path;
    private readonly LogFilePolicy _policy;
    private readonly IDisposable _protection;
    private FileStream _stream;

    internal RollingLogStream(string path, LogFilePolicy policy, bool createNew)
    {
        _path = Path.GetFullPath(path);
        _policy = policy;
        _protection = policy.Protect(_path);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            if (!LogFilePolicy.IsSafeDirectory(directory) ||
                File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Log paths cannot traverse reparse points.");
            _stream = new FileStream(_path, createNew ? FileMode.CreateNew : FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            _stream.Seek(0, SeekOrigin.End);
            policy.SchedulePrune();
        }
        catch { _protection.Dispose(); throw; }
    }

    internal void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_stream.Length >= _policy.MaximumFileBytes) Rotate();
            var count = (int)Math.Min(bytes.Length, _policy.MaximumFileBytes - _stream.Length);
            // Text appends arrive as complete UTF-8. Keep each segment independently
            // decodable rather than cutting a multibyte character at the size cap.
            while (count > 0 && count < bytes.Length && (bytes[count] & 0xc0) == 0x80) count--;
            if (count == 0) { Rotate(); continue; }
            _stream.Write(bytes[..count]);
            bytes = bytes[count..];
        }
    }

    internal async Task WriteAsync(ReadOnlyMemory<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_stream.Length >= _policy.MaximumFileBytes) Rotate();
            var count = (int)Math.Min(bytes.Length, _policy.MaximumFileBytes - _stream.Length);
            await _stream.WriteAsync(bytes[..count]).ConfigureAwait(false);
            bytes = bytes[count..];
        }
    }

    private void Rotate()
    {
        if (!LogFilePolicy.IsSafeDirectory(Path.GetDirectoryName(_path)!))
            throw new IOException("Log paths cannot traverse reparse points.");
        _stream.Dispose();
        for (var index = _policy.Backups; index >= 1; index--)
        {
            var destination = _path + "." + index;
            var source = index == 1 ? _path : _path + "." + (index - 1);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Log backups cannot be reparse points.");
            if (File.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Log backups cannot be reparse points.");
            if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        }
        _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
    }

    public void Dispose()
    {
        try { _stream.Dispose(); }
        finally { _protection.Dispose(); }
    }
}
