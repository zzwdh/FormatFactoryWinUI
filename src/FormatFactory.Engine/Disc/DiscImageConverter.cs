using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Disc;

public sealed class DiscImageConverter
{
    public Task ConvertAsync(
        string inputPath,
        DiscImageFormat inputFormat,
        string outputPath,
        DiscImageFormat outputFormat,
        IProgress<DiscImageConversionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        (inputFormat, outputFormat) switch
        {
            (DiscImageFormat.Iso, DiscImageFormat.Cso) =>
                ConvertIsoToCsoAsync(inputPath, outputPath, progress, cancellationToken),
            (DiscImageFormat.Cso, DiscImageFormat.Iso) =>
                ConvertCsoToIsoAsync(inputPath, outputPath, progress, cancellationToken),
            _ => throw new NotSupportedException(LocalizationService.T(
                "ConversionUiDiscRouteUnsupported",
                inputFormat,
                outputFormat))
        };

    public Task ConvertIsoToCsoAsync(
        string isoPath,
        string csoPath,
        IProgress<DiscImageConversionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ConvertIsoToCsoAsync(
            isoPath,
            csoPath,
            CsoImageCodec.DefaultBlockSize,
            progress,
            cancellationToken);

    public async Task ConvertIsoToCsoAsync(
        string isoPath,
        string csoPath,
        int blockSize,
        IProgress<DiscImageConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (input, output, temporary) = PreparePaths(isoPath, csoPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CsoImageCodec.WriteAsync(input, temporary, blockSize, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var check = OpenCso(temporary))
            {
                _ = await CsoImageCodec.ReadLayoutAsync(check, cancellationToken);
            }

            File.Move(temporary, output, overwrite: false);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    public async Task ConvertCsoToIsoAsync(
        string csoPath,
        string isoPath,
        IProgress<DiscImageConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (input, output, temporary) = PreparePaths(csoPath, isoPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = OpenCso(input);
            var layout = await CsoImageCodec.ReadLayoutAsync(source, cancellationToken);
            await using (var destination = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                FormatFactoryWinUI.Services.ProcessResourcePolicy.TrySetFileIoPriority(
                    destination,
                    FormatFactoryWinUI.Services.FileIoPriority.Low);
                await CsoImageCodec.DecodeAsync(source, destination, layout, progress, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                if (destination.Length != layout.TotalBytes)
                {
                    throw new InvalidDataException(LocalizationService.T("ConversionUiDecodedIsoSizeMismatch"));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: false);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private static (string Input, string Output, string Temporary) PreparePaths(
        string inputPath,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiOutputPathMustDiffer"), nameof(outputPath));
        }

        if (!File.Exists(input))
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiDiscImageInputNotFound"), input);
        }

        var directory = Path.GetDirectoryName(output)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiOutputPathNeedsDirectory"), nameof(outputPath));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        return (input, output, temporary);
    }

    private static FileStream OpenCso(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        FormatFactoryWinUI.Services.ProcessResourcePolicy.TrySetFileIoPriority(
            stream,
            FormatFactoryWinUI.Services.FileIoPriority.Low);
        return stream;
    }

    private static void DeleteTemporary(string temporary)
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}
