using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Disc;

public sealed class DiscImageValidator
{
    public Task<DiscImageValidationResult> ValidateAsync(
        string path,
        DiscImageFormat format,
        CancellationToken cancellationToken = default) =>
        format switch
        {
            DiscImageFormat.Iso => ValidateIsoAsync(path, cancellationToken),
            DiscImageFormat.Cso => ValidateCsoAsync(path, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    public async Task<DiscImageValidationResult> ValidateIsoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            await using var input = new FileStream(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length <= 0)
            {
                throw new InvalidDataException(LocalizationService.T("ConversionUiIsoImageEmpty"));
            }

            var blockCount = ((input.Length - 1L) / DiscImageWorker.DefaultBlockSize) + 1L;
            return new DiscImageValidationResult(
                true,
                DiscImageFormat.Iso,
                input.Length,
                DiscImageWorker.DefaultBlockSize,
                checked((int)Math.Min(int.MaxValue, blockCount)));
        }
        catch (Exception exception) when (IsValidationFailure(exception))
        {
            return Invalid(DiscImageFormat.Iso, exception.Message);
        }
    }

    public async Task<DiscImageValidationResult> ValidateCsoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            await using var input = new FileStream(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            return await new DiscImageWorker(ConversionWorkerProcess.ExecutablePath).CheckAsync(input, verify: true, cancellationToken);
        }
        catch (Exception exception) when (IsValidationFailure(exception))
        {
            return Invalid(DiscImageFormat.Cso, exception.Message);
        }
    }

    private static bool IsValidationFailure(Exception exception) => exception is
        ArgumentException or
        IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        OverflowException;

    private static DiscImageValidationResult Invalid(DiscImageFormat format, string message) =>
        new(false, format, 0, 0, 0, message);
}
