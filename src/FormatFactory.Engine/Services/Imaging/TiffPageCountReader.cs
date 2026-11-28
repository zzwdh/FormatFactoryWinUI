namespace FormatFactoryWinUI.Services;

internal static class TiffPageCountReader
{
    public static int? Read(string path, CancellationToken token = default) =>
        ReadAsync(path, token).GetAwaiter().GetResult();

    internal static async Task<int?> ReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != 4 ||
            !(signature.SequenceEqual("II*\0"u8) || signature.SequenceEqual("II+\0"u8) ||
              signature.SequenceEqual("MM\0*"u8) || signature.SequenceEqual("MM\0+"u8))) return null;
        var pages = await ConversionBackend.RequestAsync<int>("image-inspect", new { mode = "tiff-pages", path = stream.Name }, token)
            .ConfigureAwait(false);
        return pages is > 0 and <= 100_000 ? pages : throw new InvalidDataException(LocalizationService.T("NativeImageValidationFailed"));
    }
}
