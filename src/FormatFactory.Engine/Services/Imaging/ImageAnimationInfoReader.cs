namespace FormatFactoryWinUI.Services;

/// <summary>Container timing used when preserving animations across formats.</summary>
internal sealed record ImageAnimationInfo(int Width, int Height, IReadOnlyList<int> FrameDurations, int LoopCount, bool IsLossless = true)
{
    public bool IsAnimated => FrameDurations.Count > 1;
    public bool Matches(ImageAnimationInfo other, bool requireLossless) =>
        Width == other.Width && Height == other.Height && LoopCount == other.LoopCount &&
        FrameDurations.SequenceEqual(other.FrameDurations) && (!requireLossless || other.IsLossless);
}

internal static class ImageAnimationInfoReader
{
    public static ImageAnimationInfo Read(string path, CancellationToken token = default) =>
        ReadAsync(path, false, token).GetAwaiter().GetResult();

    internal static async Task<ImageAnimationInfo> ReadAsync(string path, bool verifyWebp, CancellationToken token)
    {
        using var deadline = verifyWebp ? new CancellationTokenSource(TimeSpan.FromMinutes(5)) : null;
        using var linked = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var value = await ConversionBackend.RequestAsync<ImageAnimationInfo>("image-inspect",
                new { mode = verifyWebp ? "verify-webp" : "animation", path = Path.GetFullPath(path) }, linked?.Token ?? token).ConfigureAwait(false);
            if (value.Width <= 0 || value.Height <= 0 || value.LoopCount is < 0 or > 65536 ||
                value.FrameDurations is not { Count: > 0 and <= 100_000 } || value.FrameDurations.Any(delay => delay is < 0 or > 0xffffff))
                throw new InvalidDataException(LocalizationService.T("NativeImageAnimationValidationFailed"));
            return value;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw new TimeoutException(LocalizationService.T("NativeImageAnimationValidationFailed"));
        }
    }
}
