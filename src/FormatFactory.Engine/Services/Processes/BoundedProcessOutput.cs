using System.Runtime.CompilerServices;
using System.Text;

namespace FormatFactoryWinUI.Services;

internal sealed class ProcessOutputLimitExceededException(string streamName, int maximumBytes)
    : IOException($"External process {streamName} exceeded its {maximumBytes:N0}-byte capture limit.")
{
    public string StreamName { get; } = streamName;
    public int MaximumBytes { get; } = maximumBytes;
}

internal sealed record CapturedProcessOutput(string Text, bool Truncated);

internal sealed class BoundedTextCapture(bool requireComplete)
{
    private readonly StringBuilder _text = new();
    private int _bytes;
    public bool Truncated { get; private set; }
    public bool RequiresCompleteOutput => requireComplete;
    public void MarkTruncated() => Truncated = true;

    public void Append(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (requireComplete && _bytes + bytes > BoundedProcessOutput.MaximumParsedBytes)
            throw new ProcessOutputLimitExceededException("parsed output", BoundedProcessOutput.MaximumParsedBytes);
        _text.Append(text);
        _bytes += bytes;
        // UTF-16 character cap also bounds UTF-8 output at 64 KiB without splitting surrogate pairs.
        const int tailCharacters = BoundedProcessOutput.TailBytes / 4;
        if (!requireComplete && _text.Length > tailCharacters)
        {
            var remove = _text.Length - tailCharacters;
            if (char.IsLowSurrogate(_text[remove])) remove++;
            _text.Remove(0, remove);
            Truncated = true;
            _bytes = Encoding.UTF8.GetByteCount(_text.ToString());
        }
    }

    public override string ToString() => _text.ToString();
}

internal static class BoundedProcessOutput
{
    public const int MaximumParsedBytes = 16 * 1024 * 1024;
    public const int TailBytes = 64 * 1024;
    private const int MaximumLineCharacters = 64 * 1024;

    public static async Task<CapturedProcessOutput> ReadAsync(Stream stream, string name,
        int maximumBytes, bool requireComplete, string? logPath = null, LogFilePolicy? logPolicy = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var log = logPath is null ? null : new RollingLogStream(logPath, logPolicy ?? LogFilePolicy.Shared, createNew: true);
        using var capture = requireComplete ? new MemoryStream(Math.Min(maximumBytes, 8192)) : null;
        var tail = requireComplete ? null : new byte[maximumBytes];
        var count = 0;
        var cursor = 0;
        var truncated = false;
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (log is not null) await log.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            if (requireComplete)
            {
                if (capture!.Length + read > maximumBytes) throw new ProcessOutputLimitExceededException(name, maximumBytes);
                capture.Write(buffer, 0, read);
            }
            else
            {
                truncated |= count + read > maximumBytes;
                for (var i = 0; i < read; i++)
                {
                    tail![cursor] = buffer[i];
                    cursor = (cursor + 1) % maximumBytes;
                }
                count = Math.Min(maximumBytes, count + read);
            }
        }
        if (requireComplete) return new CapturedProcessOutput(Encoding.UTF8.GetString(capture!.GetBuffer(), 0, (int)capture.Length), false);
        var bytes = new byte[count];
        var start = count == maximumBytes ? cursor : 0;
        for (var i = 0; i < count; i++) bytes[i] = tail![(start + i) % maximumBytes];
        // A tail may begin inside a UTF-8 sequence. Drop only the incomplete prefix.
        var offset = 0;
        while (offset < bytes.Length && (bytes[offset] & 0xc0) == 0x80) offset++;
        return new CapturedProcessOutput(Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset), truncated);
    }

    /// <summary>Logs full chunks without allowing an unterminated line to grow without bound.</summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(StreamReader reader,
        Action<string>? logChunk = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool requireCompleteLine = true,
        Action? onTruncatedLine = null,
        Action<int>? onCharactersRead = null)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var ignoreLf = false;
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            onCharactersRead?.Invoke(read);
            logChunk?.Invoke(new string(buffer, 0, read));
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (value == '\n' && ignoreLf) { ignoreLf = false; continue; }
                ignoreLf = value == '\r';
                if (value is '\r' or '\n')
                {
                    yield return truncated ? "…" + line : line.ToString();
                    line.Clear();
                    truncated = false;
                }
                else
                {
                    if (line.Length >= MaximumLineCharacters)
                    {
                        if (requireCompleteLine) throw new ProcessOutputLimitExceededException("line", MaximumLineCharacters);
                        var remove = MaximumLineCharacters / 2;
                        if (char.IsLowSurrogate(line[remove])) remove++;
                        line.Remove(0, remove);
                        if (!truncated) onTruncatedLine?.Invoke();
                        truncated = true;
                    }
                    line.Append(value);
                }
            }
        }
        if (line.Length != 0) yield return truncated ? "…" + line : line.ToString();
    }
}
