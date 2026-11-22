using System.Buffers.Binary;
using System.Text;

namespace FormatFactoryWinUI.Services;

/// <summary>Reads container timing independently of still-only image decoders.</summary>
internal sealed record ImageAnimationInfo(int Width, int Height, IReadOnlyList<int> FrameDurations, int LoopCount, bool IsLossless = true)
{
    public bool IsAnimated => FrameDurations.Count > 1;
    public bool Matches(ImageAnimationInfo other, bool requireLossless) =>
        Width == other.Width && Height == other.Height && LoopCount == other.LoopCount &&
        FrameDurations.SequenceEqual(other.FrameDurations) && (!requireLossless || other.IsLossless);
}

internal static class ImageAnimationInfoReader
{
    private const int MaximumFrames = 100_000;

    public static ImageAnimationInfo Read(string path, CancellationToken token = default)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        Span<byte> signature = stackalloc byte[12];
        stream.ReadExactly(signature);
        stream.Position = 0;
        if (signature[..6].SequenceEqual("GIF87a"u8) || signature[..6].SequenceEqual("GIF89a"u8))
            return ReadGif(reader, token);
        if (signature[..4].SequenceEqual("RIFF"u8) && signature[8..].SequenceEqual("WEBP"u8))
            return ReadWebp(reader, token);
        throw new InvalidDataException(LocalizationService.T("NativeImageAnimationUnsupported"));
    }

    private static ImageAnimationInfo ReadGif(BinaryReader reader, CancellationToken token)
    {
        var stream = reader.BaseStream;
        stream.Position = 6;
        var width = reader.ReadUInt16();
        var height = reader.ReadUInt16();
        var flags = reader.ReadByte();
        Skip(stream, 2);
        if ((flags & 0x80) != 0) Skip(stream, 3L << ((flags & 7) + 1));
        var durations = new List<int>();
        var delay = 0;
        var loops = 1;
        var ended = false;
        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            var marker = reader.ReadByte();
            if (marker == 0x3b) { ended = true; break; }
            if (marker == 0x21)
            {
                var label = reader.ReadByte();
                if (label == 0xf9)
                {
                    if (reader.ReadByte() != 4) throw InvalidAnimation();
                    reader.ReadByte();
                    delay = reader.ReadUInt16() * 10;
                    reader.ReadByte();
                    if (reader.ReadByte() != 0) throw InvalidAnimation();
                }
                else if (label == 0xff)
                {
                    var application = reader.ReadBytes(reader.ReadByte());
                    var recognized = application.AsSpan().SequenceEqual("NETSCAPE2.0"u8) ||
                        application.AsSpan().SequenceEqual("ANIMEXTS1.0"u8);
                    ReadSubBlocks(reader, token, block =>
                    {
                        if (recognized && block.Length == 3 && block[0] == 1)
                        {
                            var repetitions = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(1));
                            loops = repetitions == 0 ? 0 : repetitions + 1;
                        }
                    });
                }
                else ReadSubBlocks(reader, token);
            }
            else if (marker == 0x2c)
            {
                Skip(stream, 8);
                var imageFlags = reader.ReadByte();
                if ((imageFlags & 0x80) != 0) Skip(stream, 3L << ((imageFlags & 7) + 1));
                reader.ReadByte();
                ReadSubBlocks(reader, token);
                if (durations.Count == MaximumFrames) throw InvalidAnimation();
                durations.Add(delay);
                delay = 0;
            }
            else throw InvalidAnimation();
        }
        if (!ended || width == 0 || height == 0 || durations.Count == 0) throw InvalidAnimation();
        return new(width, height, durations, loops);
    }

    private static ImageAnimationInfo ReadWebp(BinaryReader reader, CancellationToken token)
    {
        var stream = reader.BaseStream;
        stream.Position = 4;
        if ((long)reader.ReadUInt32() + 8 != stream.Length) throw InvalidAnimation();
        stream.Position = 12;
        var width = 0;
        var height = 0;
        var loops = -1;
        var durations = new List<int>();
        var lossless = true;
        while (stream.Position + 8 <= stream.Length)
        {
            token.ThrowIfCancellationRequested();
            var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            var end = checked(stream.Position + length);
            if (end > stream.Length) throw InvalidAnimation();
            if (tag == "VP8X")
            {
                if (length != 10 || width != 0 || durations.Count != 0) throw InvalidAnimation();
                // Writer-side validation follows the WebP container specification:
                // animation is bit 1, and all reserved header bits must be zero.
                var flags = reader.ReadByte();
                if ((flags & 0xc1) != 0 || (flags & 2) == 0 || ReadUInt24(reader) != 0) throw InvalidAnimation();
                width = ReadUInt24(reader) + 1;
                height = ReadUInt24(reader) + 1;
                if ((ulong)width * (ulong)height > uint.MaxValue) throw InvalidAnimation();
            }
            else if (tag == "ANIM")
            {
                if (length != 6 || width == 0 || loops >= 0 || durations.Count != 0) throw InvalidAnimation();
                reader.ReadUInt32();
                loops = reader.ReadUInt16();
            }
            else if (tag == "ANMF")
            {
                if (length < 24 || durations.Count == MaximumFrames) throw InvalidAnimation();
                var x = ReadUInt24(reader) * 2;
                var y = ReadUInt24(reader) * 2;
                var frameWidth = ReadUInt24(reader) + 1;
                var frameHeight = ReadUInt24(reader) + 1;
                var duration = ReadUInt24(reader);
                if ((reader.ReadByte() & 0xfc) != 0 || loops < 0 ||
                    x + frameWidth > width || y + frameHeight > height) throw InvalidAnimation();
                var hasImage = false;
                var hasAlpha = false;
                while (stream.Position + 8 <= end)
                {
                    var frameTag = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    var frameLength = reader.ReadUInt32();
                    var frameEnd = checked(stream.Position + frameLength + (frameLength & 1));
                    if (frameEnd > end) throw InvalidAnimation();
                    if (frameTag is "VP8L" or "VP8 ")
                    {
                        if (hasImage || hasAlpha && frameTag == "VP8L") throw InvalidAnimation();
                        ValidateBitstreamHeader(reader, frameTag, frameLength, frameWidth, frameHeight);
                        hasImage = true;
                        lossless &= frameTag == "VP8L";
                    }
                    else if (frameTag == "ALPH")
                    {
                        if (frameLength < 2 || hasImage || hasAlpha) throw InvalidAnimation();
                        var alphaFlags = reader.ReadByte();
                        if ((alphaFlags & 0xc0) != 0 || (alphaFlags & 3) > 1 || ((alphaFlags >> 4) & 3) > 1 ||
                            (alphaFlags & 3) == 0 && (ulong)frameLength != 1 + (ulong)frameWidth * (ulong)frameHeight)
                            throw InvalidAnimation();
                        hasAlpha = true;
                    }
                    stream.Position = frameEnd - (frameLength & 1);
                    ReadPadding(reader, frameLength);
                }
                if (!hasImage || stream.Position != end) throw InvalidAnimation();
                durations.Add(duration);
            }
            stream.Position = end;
            ReadPadding(reader, length);
        }
        if (stream.Position != stream.Length || width <= 0 || height <= 0 || loops < 0 || durations.Count < 2)
            throw InvalidAnimation();
        return new(width, height, durations, loops, lossless);
    }

    private static bool ValidateBitstreamHeader(BinaryReader reader, string tag, uint length, int width, int height)
    {
        // https://developers.google.com/speed/webp/docs/webp_lossless_bitstream_specification
        // https://datatracker.ietf.org/doc/html/rfc6386#section-9.1
        if (tag == "VP8L")
        {
            if (length < 5 || reader.ReadByte() != 0x2f) throw InvalidAnimation();
            var bits = reader.ReadUInt32();
            if ((bits >> 29) != 0 || (bits & 0x3fff) + 1 != width ||
                ((bits >> 14) & 0x3fff) + 1 != height) throw InvalidAnimation();
            return (bits & (1u << 28)) != 0;
        }
        if (length < 10) throw InvalidAnimation();
        var frameTag = ReadUInt24(reader);
        if ((frameTag & 1) != 0 || ((frameTag >> 1) & 7) > 3 || (frameTag & 16) == 0 ||
            (uint)(frameTag >> 5) > length - 10 || reader.ReadByte() != 0x9d ||
            reader.ReadByte() != 1 || reader.ReadByte() != 0x2a ||
            (reader.ReadUInt16() & 0x3fff) != width || (reader.ReadUInt16() & 0x3fff) != height)
            throw InvalidAnimation();
        return false;
    }

    private static void ReadPadding(BinaryReader reader, uint length)
    {
        if ((length & 1) != 0 && reader.ReadByte() != 0) throw InvalidAnimation();
    }

    private sealed record WebpFrame(long Offset, long Length, int Width, int Height, bool HasAlpha);

    /// <summary>Checks every compressed frame with a real decoder, using one bounded scratch file.</summary>
    internal static async Task<bool> ValidateWebpFramesAsync(string path, string ffmpegPath,
        CancellationToken token = default, Func<string, CancellationToken, Task<bool>>? decodeFrame = null)
    {
        token.ThrowIfCancellationRequested();
        if (decodeFrame is null && (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))) return false;
        // Validate structure before any payload is copied or decoder is started.
        Read(path, token);
        var directory = Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "animation-validation", Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(directory, "frame.webp");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            Directory.CreateDirectory(directory);
            using var source = File.OpenRead(path);
            var decodedFrames = 0;
            foreach (var frame in EnumerateWebpFrames(path, linked.Token))
            {
                linked.Token.ThrowIfCancellationRequested();
                await WriteStaticFrameAsync(source, frame, temporary, linked.Token);
                if (decodeFrame is not null)
                {
                    if (!await decodeFrame(temporary, linked.Token)) return false;
                }
                else
                {
                    var result = await ProcessRunner.RunAsync(ffmpegPath,
                        ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-threads", "1", "-i", temporary,
                            "-map", "0:V:0", "-frames:v", "1", "-progress", "pipe:1", "-nostats", "-f", "null", "-"],
                        TimeSpan.FromSeconds(30), linked.Token);
                    if (result.ExitCode != 0 || !result.StandardOutput.Split('\n').Any(line => line.Trim() == "frame=1")) return false;
                }
                decodedFrames++;
            }
            return decodedFrames > 1;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { return false; }
        finally
        {
            // Only this known file belongs to the verifier; never remove recursively.
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<WebpFrame> EnumerateWebpFrames(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        stream.Position = 12;
        while (stream.Position + 8 <= stream.Length)
        {
            token.ThrowIfCancellationRequested();
            var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            var end = checked(stream.Position + length);
            if (end > stream.Length) throw InvalidAnimation();
            if (tag == "ANMF")
            {
                if (length < 24) throw InvalidAnimation();
                Skip(stream, 6);
                var width = ReadUInt24(reader) + 1;
                var height = ReadUInt24(reader) + 1;
                Skip(stream, 4);
                var offset = stream.Position;
                var alpha = false;
                while (stream.Position + 8 <= end)
                {
                    var frameTag = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    var frameLength = reader.ReadUInt32();
                    var frameEnd = checked(stream.Position + frameLength + (frameLength & 1));
                    if (frameEnd > end) throw InvalidAnimation();
                    if (frameTag == "ALPH") alpha = true;
                    else if (frameTag is "VP8L" or "VP8 ") alpha |= ValidateBitstreamHeader(reader, frameTag, frameLength, width, height);
                    stream.Position = frameEnd;
                }
                yield return new(offset, end - offset, width, height, alpha);
            }
            stream.Position = end;
            ReadPadding(reader, length);
        }
    }

    private static async Task WriteStaticFrameAsync(Stream source, WebpFrame frame, string outputPath, CancellationToken token)
    {
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            65_536, FileOptions.Asynchronous);
        using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(checked((uint)(4 + 18 + frame.Length)));
            writer.Write("WEBPVP8X"u8);
            writer.Write(10u);
            writer.Write(frame.HasAlpha ? (byte)16 : (byte)0);
            WriteUInt24(writer, 0);
            WriteUInt24(writer, frame.Width - 1);
            WriteUInt24(writer, frame.Height - 1);
        }
        source.Position = frame.Offset;
        var remaining = frame.Length;
        var buffer = new byte[65_536];
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token);
            if (read == 0) throw InvalidAnimation();
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            remaining -= read;
        }
    }

    private static void WriteUInt24(BinaryWriter writer, int value)
    {
        writer.Write((byte)value);
        writer.Write((byte)(value >> 8));
        writer.Write((byte)(value >> 16));
    }

    private static int ReadUInt24(BinaryReader reader) =>
        reader.ReadByte() | (reader.ReadByte() << 8) | (reader.ReadByte() << 16);

    private static void ReadSubBlocks(BinaryReader reader, CancellationToken token, Action<byte[]>? visit = null)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var length = reader.ReadByte();
            if (length == 0) return;
            if (visit is null) Skip(reader.BaseStream, length);
            else
            {
                var block = reader.ReadBytes(length);
                if (block.Length != length) throw InvalidAnimation();
                visit(block);
            }
        }
    }

    private static void Skip(Stream stream, long length)
    {
        if (length < 0 || length > stream.Length - stream.Position) throw InvalidAnimation();
        stream.Position += length;
    }

    private static InvalidDataException InvalidAnimation() =>
        new(LocalizationService.T("NativeImageAnimationValidationFailed"));
}
