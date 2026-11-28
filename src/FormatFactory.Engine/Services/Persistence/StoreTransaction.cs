using System.Buffers.Binary;
using System.Text.Json;

namespace FormatFactoryWinUI.Services;

internal sealed class StoreTransaction : IDisposable
{
    private readonly ExternalProcessLifetime _worker;
    private readonly string _temporary;
    private readonly bool _hash;
    private readonly Task<string> _errors;
    private readonly Dictionary<string, int> _sources = new(StringComparer.OrdinalIgnoreCase);

    private StoreTransaction(string path, bool hash, CancellationToken token)
    {
        _hash = hash;
        _temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        _worker = ConversionBackend.Start("store-transaction", token);
        _errors = _worker.Process.StandardError.ReadToEndAsync();
    }

    internal static async Task<StoreTransaction> OpenAsync(string path, CancellationToken token, bool direct = false, bool hash = false, bool directoryLock = false)
    {
        var transaction = new StoreTransaction(path, hash, token);
        try
        {
            await transaction.WriteLineAsync(new { Path = path, Temporary = transaction._temporary, Direct = direct, Hash = hash, DirectoryLock = directoryLock }, token).ConfigureAwait(false);
            if (await transaction.ReadLineAsync(token).ConfigureAwait(false) != "ready")
                throw new InvalidDataException("Invalid storage worker handshake.");
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }

    internal static Task<StoreTransaction> OpenReceiptAsync(string path, CancellationToken token = default) =>
        OpenAsync(path, token, direct: true, directoryLock: true);

    internal Task DeleteAsync(CancellationToken token = default) => PublishAsync<object?>(null, JsonSerializerOptions.Default, token);

    internal record Row(Guid Id, int Source, bool Grouped);
    internal Row Identify(Guid id, string source, bool grouped = false)
    {
        if (!_sources.TryGetValue(source, out var value))
            _sources.Add(source, value = _sources.Count);
        return new(id, value, grouped);
    }

    internal async Task<T[]> SelectAsync<T>(string mode, IReadOnlyList<T> existing, IReadOnlyList<T> incoming,
        Func<T, Row> identity, IEnumerable<Guid> known, CancellationToken token)
    {
        await WriteLineAsync(new { Mode = mode, Existing = existing.Select(identity), Incoming = incoming.Select(identity), Known = known }, token).ConfigureAwait(false);
        var indices = JsonSerializer.Deserialize<int[]>(await ReadLineAsync(token).ConfigureAwait(false))
            ?? throw new InvalidDataException("Missing storage worker selection.");
        return indices.Select(i => i < existing.Count ? existing[i] : incoming[i - existing.Count]).ToArray();
    }

    private async Task WriteLineAsync(object request, CancellationToken token)
    {
        var input = _worker.Process.StandardInput.BaseStream;
        await JsonSerializer.SerializeAsync(input, request, request.GetType(), cancellationToken: token).ConfigureAwait(false);
        await input.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
        await input.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        var line = await _worker.Process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
        if (line is not null) return line;
        var failure = await _errors.WaitAsync(token).ConfigureAwait(false);
        if (failure.Contains("store-lock-timeout", StringComparison.Ordinal))
            throw new TimeoutException("Timed out acquiring the storage file lock.");
        throw ConversionBackend.DecodeFailure(failure);
    }

    internal async Task<byte[]?> PublishAsync<T>(T snapshot, JsonSerializerOptions options, CancellationToken token)
    {
        var input = _worker.Process.StandardInput;
        var frames = new SnapshotFrames(input.BaseStream);
        if (snapshot is not null)
            await JsonSerializer.SerializeAsync(frames, snapshot, options, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await input.BaseStream.WriteAsync(new byte[4], CancellationToken.None).ConfigureAwait(false);
        input.Close();
        // Once the complete commit vote has been sent, observe the result under the same lock.
        var hash = _hash ? Convert.FromHexString(await ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) : null;
        if (await ReadLineAsync(CancellationToken.None).ConfigureAwait(false) != "ok")
            throw new InvalidDataException("Invalid storage worker commit response.");
        await _worker.Exited.ConfigureAwait(false);
        return hash;
    }

    public void Dispose()
    {
        _worker.Dispose();
        try { File.Delete(_temporary); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SafeDiagnostics.Report(error); }
    }

    // Explicit end-of-snapshot frame distinguishes a complete write from host death.
    private sealed class SnapshotFrames(Stream stream) : Stream
    {
        private readonly byte[] _length = new byte[4];
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            if (data.IsEmpty) return;
            BinaryPrimitives.WriteInt32LittleEndian(_length, data.Length);
            await stream.WriteAsync(_length, token).ConfigureAwait(false);
            await stream.WriteAsync(data, token).ConfigureAwait(false);
        }
        public override Task WriteAsync(byte[] data, int offset, int count, CancellationToken token) =>
            WriteAsync(data.AsMemory(offset, count), token).AsTask();
        public override void Write(byte[] data, int offset, int count) =>
            WriteAsync(data.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override void Flush() => stream.Flush();
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
