using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Disc;

internal sealed class DiscImageWorker(string executable)
{
    internal const int DefaultBlockSize = 2048;
    internal static bool IsValidBlockSize(int size) =>
        size is >= 512 and <= 16 * 1024 * 1024 && (size & (size - 1)) == 0;

    internal async Task<DiscImageValidationResult> CheckAsync(FileStream source, bool verify, CancellationToken token)
    {
        ProcessExecutionResult result;
        try
        {
            result = await ProcessRunner.RunAsync(executable,
                [ConversionWorkerProcess.ProtocolVersionArgument, "disc-check", verify ? "verify" : "inspect", source.Name],
                Timeout.InfiniteTimeSpan, new ProcessRunOptions { MaximumOutputBytes = 256, MaximumErrorBytes = 1024 }, token)
                .ConfigureAwait(false);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiCannotStartExternalTool", ConversionWorkerProcess.FileName),
                executable, error);
        }

        var errorLine = result.StandardError.TrimEnd('\r', '\n');
        if (result.ExitCode != 0)
        {
            var error = WorkerFailure(errorLine, result.ExitCode, 0, 0, source.Length);
            if (error is InvalidDataException) RecordInputFacts(error, source, errorLine);
            throw error;
        }
        var lines = result.StandardOutput.TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n');
        if (errorLine.Length != 0 || result.OutputTruncated || lines is not [_, "ok"] ||
            lines[0].Split(' ') is not ["cso", var length, var size, var count] ||
            !long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total <= 0 ||
            !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var blockSize) || !IsValidBlockSize(blockSize) ||
            !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var blocks) ||
            blocks != (total - 1) / blockSize + 1 || blocks >= int.MaxValue)
            throw InvalidProtocol();
        return new(true, DiscImageFormat.Cso, total, blockSize, blocks);
    }

    // The worker reports a fixed error code and optional block number. Recover
    // the original numeric diagnostics with at most one header and two entries.
    private static void RecordInputFacts(Exception error, FileStream source, string line)
    {
        var fields = line.Split(' ');
        var reason = fields.ElementAtOrDefault(1) switch
        {
            "ConversionUiUnexpectedEndOfStream" => FormatFailureReason.HeaderRead,
            "ConversionUiCsoMagicInvalid" => FormatFailureReason.Magic,
            "ConversionUiCsoHeaderSizeInvalid" => FormatFailureReason.HeaderSize,
            "ConversionUiCsoUncompressedSizeInvalid" => FormatFailureReason.TotalSize,
            "ConversionUiCsoBlockSizeInvalid" or "ConversionUiCsoBlockSizePowerOfTwo" => FormatFailureReason.BlockSize,
            "ConversionUiCsoVersion1Only" => FormatFailureReason.Version,
            "ConversionUiCsoAlignmentInvalid" => FormatFailureReason.Alignment,
            "ConversionUiCsoReservedHeaderInvalid" => FormatFailureReason.Reserved,
            "ConversionUiCsoTooManyBlocks" => FormatFailureReason.BlockCount,
            "ConversionUiCsoIndexTruncated" => FormatFailureReason.IndexTruncated,
            "ConversionUiCsoFinalIndexFlagInvalid" => FormatFailureReason.FinalIndexFlag,
            "ConversionUiCsoDataOverlapsIndex" => FormatFailureReason.IndexOverlap,
            "ConversionUiCsoBlockOffsetRangeInvalid" => FormatFailureReason.OffsetRange,
            "ConversionUiCsoPlainBlockStoredSizeInvalid" => FormatFailureReason.PlainBlockSize,
            "ConversionUiCsoDataBeyondFinalIndex" => FormatFailureReason.TrailingData,
            "ConversionUiCsoBlockExpandsBeyondDeclaredSize" => FormatFailureReason.DecodedSize,
            "ConversionUiCsoBlockDataTruncatedOrInvalid" => FormatFailureReason.DecodeRead,
            _ => FormatFailureReason.Unknown
        };
        var stage = reason switch
        {
            FormatFailureReason.IndexTruncated or FormatFailureReason.FinalIndexFlag or FormatFailureReason.IndexOverlap or
            FormatFailureReason.OffsetRange or FormatFailureReason.PlainBlockSize or FormatFailureReason.TrailingData => FormatFailureStage.CsoIndex,
            FormatFailureReason.DecodedSize or FormatFailureReason.DecodeRead => FormatFailureStage.CsoDecode,
            _ => FormatFailureStage.CsoHeader
        };
        var fact = new FormatFailureFact(stage, reason) { InputLength = source.Length };
        try
        {
            Span<byte> header = stackalloc byte[24];
            source.Position = 0;
            source.ReadExactly(header);
            fact = fact with { HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
                TotalBytes = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]), BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
                Version = header[20], Alignment = header[21], Reserved = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]) };
            if (fields.Length == 3 && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                fact = fact with { Index = index };
                if (stage == FormatFailureStage.CsoIndex && header[21] <= 31)
                {
                    source.Position = 24L + index * 4L;
                    Span<byte> pair = stackalloc byte[8];
                    source.ReadExactly(pair);
                    fact = fact with { Start = (long)(BinaryPrimitives.ReadUInt32LittleEndian(pair) & 0x7fffffff) << header[21],
                        End = (long)(BinaryPrimitives.ReadUInt32LittleEndian(pair[4..]) & 0x7fffffff) << header[21] };
                }
            }
        }
        catch (IOException) { }
        FormatFailureDiagnostics.Record(error, fact);
    }

    internal async Task ConvertAsync(string operation, string input, string temporary, int blockSize,
        long totalBytes, long inputLength, IProgress<DiscImageConversionProgress>? progress, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { ConversionWorkerProcess.ProtocolVersionArgument, "disc-image", operation, "--input", input,
                     "--output", temporary, "--block-size", blockSize.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);

        ExternalProcessLifetime lifetime;
        try { lifetime = ExternalProcessLifetime.Start(start, token); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiCannotStartExternalTool", ConversionWorkerProcess.FileName),
                executable, error);
        }
        using var ownedLifetime = lifetime;
        var process = lifetime.Process;
        var output = ReadProgressAsync(process.StandardOutput, totalBytes, blockSize, progress, token);
        var errorOutput = BoundedProcessOutput.ReadAsync(process.StandardError.BaseStream, "stderr", 1024, requireComplete: true);
        await lifetime.WaitAndDrainAsync(Timeout.InfiniteTimeSpan, token, output, errorOutput).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var errorLine = (await errorOutput.ConfigureAwait(false)).Text.TrimEnd('\r', '\n');
        if (process.ExitCode != 0)
            throw WorkerFailure(errorLine, process.ExitCode, totalBytes, blockSize, inputLength);
        if (!await output.ConfigureAwait(false) || errorLine.Length != 0)
            throw InvalidProtocol();
    }

    private static async Task<bool> ReadProgressAsync(StreamReader reader, long totalBytes, int blockSize,
        IProgress<DiscImageConversionProgress>? progress, CancellationToken token)
    {
        var expectedBlocks = checked((int)((totalBytes - 1) / blockSize + 1));
        var previousBlock = -1;
        var completed = false;
        await foreach (var line in BoundedProcessOutput.ReadLinesAsync(reader, cancellationToken: token).ConfigureAwait(false))
        {
            if (completed) throw InvalidProtocol();
            if (line == "ok")
            {
                if (previousBlock != expectedBlocks) throw InvalidProtocol();
                completed = true;
                continue;
            }

            var fields = line.Split(' ');
            if (fields is not ["progress", _, _, _, _] ||
                !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var block) ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var blocks) ||
                total != totalBytes || blocks != expectedBlocks || block > blocks || block <= previousBlock ||
                (previousBlock == -1 && block != 0) || bytes != Math.Min((long)block * blockSize, totalBytes))
                throw InvalidProtocol();

            previousBlock = block;
            progress?.Report(new DiscImageConversionProgress(bytes, total, block, blocks));
        }
        return completed;
    }

    private static Exception WorkerFailure(string line, int exitCode, long totalBytes, int blockSize, long inputLength)
    {
        var fields = line.Split(' ');
        if (fields.Length is not (2 or 3) || fields[0] != "error")
            return ToolFailure(exitCode);
        int? detail = null;
        if (fields.Length == 3)
        {
            if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return InvalidProtocol();
            detail = number;
        }

        switch (fields[1])
        {
            case "io":
                if (detail is not > 0 or > ushort.MaxValue) return ToolFailure(exitCode);
                var native = new Win32Exception(detail.Value);
                return detail == 5
                    ? new UnauthorizedAccessException(native.Message, native)
                    : new IOException(native.Message, unchecked((int)0x80070000) | detail.Value);
            case "ConversionUiCsoBlockExpandsBeyondDeclaredSize":
            case "ConversionUiCsoBlockDataTruncatedOrInvalid":
                var expanded = fields[1] == "ConversionUiCsoBlockExpandsBeyondDeclaredSize";
                if (expanded && detail is null) return InvalidProtocol();
                var message = expanded ? LocalizationService.T(fields[1], detail!.Value) : LocalizationService.T(fields[1]);
                if (totalBytes == 0) return new InvalidDataException(message);
                return FormatFailureDiagnostics.Attach(new InvalidDataException(message),
                    new(FormatFailureStage.CsoDecode, expanded ? FormatFailureReason.DecodedSize : FormatFailureReason.DecodeRead)
                    { TotalBytes = (ulong)totalBytes, BlockSize = (ulong)blockSize, Index = detail, InputLength = inputLength });
            case "ConversionUiCsoBlockOffsetRangeInvalid" or "ConversionUiCsoPlainBlockStoredSizeInvalid" when detail >= 0:
                return new InvalidDataException(LocalizationService.T(fields[1], detail.Value));
            case "ConversionUiIsoImageEmpty" or "ConversionUiIsoTooManyCsoBlocks" or "ConversionUiIsoTooLargeForCsoV1"
                or "ConversionUiCsoBlockSizePowerOfTwo" or "ConversionUiCsoBlockOffsetExceedsV1"
                or "ConversionUiUnexpectedEndOfStream" or "ConversionUiCsoMagicInvalid" or "ConversionUiCsoHeaderSizeInvalid"
                or "ConversionUiCsoUncompressedSizeInvalid" or "ConversionUiCsoBlockSizeInvalid" or "ConversionUiCsoVersion1Only"
                or "ConversionUiCsoAlignmentInvalid" or "ConversionUiCsoReservedHeaderInvalid" or "ConversionUiCsoTooManyBlocks"
                or "ConversionUiCsoIndexTruncated" or "ConversionUiCsoDataOverlapsIndex" or "ConversionUiCsoFinalIndexFlagInvalid"
                or "ConversionUiCsoDataBeyondFinalIndex":
                return new InvalidDataException(LocalizationService.T(fields[1]));
            default:
                return ToolFailure(exitCode);
        }
    }

    private static IOException ToolFailure(int exitCode) => new(LocalizationService.T(
        "ConversionUiToolExecutionFailed", ConversionWorkerProcess.FileName, exitCode, "disc-image"));

    private static InvalidDataException InvalidProtocol() =>
        new($"{ConversionWorkerProcess.FileName}: Invalid disc-image protocol v1 response.");
}
