namespace FormatFactoryWinUI.Services;

/// <summary>Recognizes sequence containers that installed still-image codecs may expose as one frame.</summary>
public static class ImageAnimationDetector
{
    public static bool HasAnimation(string path) => HasAnimationAsync(path).GetAwaiter().GetResult();

    public static async Task<bool> HasAnimationAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[12];
        if (input.Read(prefix) != 12) return false;
        if (!(prefix[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
              prefix[..4].SequenceEqual("RIFF"u8) && prefix[8..].SequenceEqual("WEBP"u8) ||
              prefix[4..8].SequenceEqual("ftyp"u8))) return false;
        return await ConversionBackend.RequestAsync<bool>("image-inspect", new { mode = "detect", path = input.Name }, token)
            .ConfigureAwait(false);
    }
}
